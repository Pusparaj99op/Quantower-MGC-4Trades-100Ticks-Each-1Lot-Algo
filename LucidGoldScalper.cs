// ============================================================
//  LUCID GOLD SCALPER — Quantower C# Strategy
//  Author  : Pranay Gajbhiye / BlackObsidian AMC / Zorvain Street
//  Account : 25K Lucid Flex (Rithmic)
//  Pairs   : MGC (Micro Gold) | GC (Gold)
//  TF      : 1M (primary scalper)
//  Style   : ICT Concepts + Order Flow confluence
// ============================================================
//  Risk Rules (Lucid 25K Flex):
//    - Daily loss limit   : $150
//    - Max SL per day     : 2
//    - Max drawdown (EOD) : $1,000
//    - Max size           : 2 Mini (GC) OR 20 Micro (MGC)
// ============================================================
//  SETUP:
//    1. Open Visual Studio
//    2. Add references to Quantower DLLs from:
//       C:\Program Files\Quantower\Bin\
//         - TradingPlatform.BusinessLayer.dll
//    3. Set Socket port 21,000 in Quantower → Settings → Algo
//       and enable "Allow connection from Visual Studio"
//    4. Build → load .dll from Quantower strategy manager
// ============================================================

using System;
using System.Collections.Generic;
using System.Linq;
using TradingPlatform.BusinessLayer;

namespace LucidGoldScalper
{
    /// <summary>
    /// Lucid Gold Scalper — ICT + Order Flow scalping strategy for MGC/GC futures
    /// on a 25K Lucid Flex prop account via Quantower + Rithmic.
    /// </summary>
    public class LucidGoldScalper : Strategy
    {
        // ─────────────────────────────────────────────
        // INPUT PARAMETERS (shown in Quantower UI)
        // ─────────────────────────────────────────────

        #region Instrument

        [InputParameter("Instrument Name", 0)]
        public string InstrumentName = "MGC";   // "MGC" for micro, "GC" for full

        [InputParameter("Connection Name", 1)]
        public string ConnectionName = "Rithmic"; // Match exactly in Quantower

        [InputParameter("Contract Suffix (e.g. Dec25 = Z5)", 2)]
        public string ContractSuffix = "Z5";    // Update each roll: H=Mar, M=Jun, U=Sep, Z=Dec

        #endregion

        #region Risk Management

        [InputParameter("Max Daily Loss ($)", 10, 50, 500, 10)]
        public double MaxDailyLoss = 150.0;

        [InputParameter("Max Stop Losses Per Day", 11, 1, 5, 1)]
        public int MaxSLPerDay = 2;

        [InputParameter("Contracts Per Trade", 12, 1, 20, 1)]
        public int ContractsPerTrade = 2;       // MGC: 1–20 | GC: 1–2

        [InputParameter("Stop Loss (Ticks)", 13, 5, 100, 1)]
        public int StopLossTicks = 20;          // MGC 1 tick = $1 | GC 1 tick = $10

        [InputParameter("Take Profit (Ticks)", 14, 5, 300, 1)]
        public int TakeProfitTicks = 35;        // Targets ~1.75R default

        [InputParameter("Use Trailing Stop", 15)]
        public bool UseTrailingStop = false;

        [InputParameter("Trail Activation (Ticks)", 16, 5, 100, 1)]
        public int TrailActivationTicks = 20;

        [InputParameter("Trail Step (Ticks)", 17, 2, 50, 1)]
        public int TrailStepTicks = 8;

        #endregion

        #region ICT Settings

        [InputParameter("Use Kill Zones Only", 20)]
        public bool UseKillZonesOnly = true;

        [InputParameter("London KZ Start (UTC hour)", 21, 0, 23, 1)]
        public int LondonKZStartUTC = 8;        // 8 UTC = 3:00 AM ET (DST)

        [InputParameter("London KZ End (UTC hour)", 22, 0, 23, 1)]
        public int LondonKZEndUTC = 10;         // 10 UTC = 5:00 AM ET

        [InputParameter("NY Open KZ Start (UTC hour)", 23, 0, 23, 1)]
        public int NYOpenKZStartUTC = 13;       // 13 UTC = 8:00 AM ET (pre-market)

        [InputParameter("NY Open KZ End (UTC hour)", 24, 0, 23, 1)]
        public int NYOpenKZEndUTC = 16;         // 16 UTC = 11:00 AM ET

        [InputParameter("FVG Minimum Size (Ticks)", 25, 1, 30, 1)]
        public int FVGMinTicks = 4;             // Min gap size to qualify as FVG

        [InputParameter("Order Block Lookback (Bars)", 26, 5, 100, 5)]
        public int OBLookback = 30;

        [InputParameter("Require Liquidity Sweep", 27)]
        public bool RequireLiquiditySweep = true; // Wait for stop run before entry

        [InputParameter("Require Market Structure Shift", 28)]
        public bool RequireMSS = true;          // Break of structure confirmation

        #endregion

        #region Order Flow Settings

        [InputParameter("Min Delta Confirmation", 30, 0, 2000, 100)]
        public double MinDeltaThreshold = 300.0; // Minimum cumulative delta for entry

        [InputParameter("Use Imbalance Filter", 31)]
        public bool UseImbalanceFilter = true;

        [InputParameter("Imbalance Ratio", 32, 1.5, 5.0, 0.5)]
        public double ImbalanceRatio = 3.0;     // e.g. 3:1 bid/ask imbalance required

        #endregion

        #region News / Session Filter

        [InputParameter("News Blackout Before (Minutes)", 40, 0, 60, 5)]
        public int NewsBlackoutBefore = 30;

        [InputParameter("News Blackout After (Minutes)", 41, 0, 60, 5)]
        public int NewsBlackoutAfter = 15;

        [InputParameter("Hard Stop Hour (UTC) — End Trading", 42, 0, 23, 1)]
        public int HardStopHourUTC = 21;        // 21 UTC = 5 PM ET — stop before EOD

        #endregion

        // ─────────────────────────────────────────────
        // PRIVATE STATE
        // ─────────────────────────────────────────────

        #region Internal Fields

        private Symbol     _symbol;
        private Account    _account;
        private HistoricalData _bars;       // 1M bars

        // ── Risk tracking ──
        private int    _slCountToday      = 0;
        private double _dailyRealizedPnL  = 0.0;
        private bool   _dailyLimitHit     = false;
        private DateTime _currentDay      = DateTime.MinValue;
        private double _dayStartBalance   = 0.0;

        // ── Trade state ──
        private bool     _inTrade         = false;
        private Position _openPosition    = null;
        private double   _trailHighWater  = double.MinValue; // for trailing stop

        // ── ICT structures ──
        private readonly List<FairValueGap> _bullFVGs = new List<FairValueGap>();
        private readonly List<FairValueGap> _bearFVGs = new List<FairValueGap>();
        private readonly List<OrderBlock>   _bullOBs  = new List<OrderBlock>();
        private readonly List<OrderBlock>   _bearOBs  = new List<OrderBlock>();

        // ── Session levels (for liquidity / MSS) ──
        private double _sessionHigh     = double.MinValue;
        private double _sessionLow      = double.MaxValue;
        private double _prevSessionHigh = double.MinValue;
        private double _prevSessionLow  = double.MaxValue;
        private bool   _sessionInitialized = false;

        // ── News schedule (UTC) ──
        private readonly List<NewsEvent> _newsEvents = new List<NewsEvent>();

        // ── Sync ──
        private readonly object _lock = new object();

        #endregion

        // ─────────────────────────────────────────────
        // STRATEGY LIFECYCLE
        // ─────────────────────────────────────────────

        #region OnRun / OnStop

        protected override void OnRun()
        {
            Log("══════════════════════════════════════", StrategyLoggingLevel.Info);
            Log("  LUCID GOLD SCALPER — Starting Up   ", StrategyLoggingLevel.Info);
            Log("══════════════════════════════════════", StrategyLoggingLevel.Info);

            // Resolve full symbol name (e.g. "MGCZ5")
            string fullSymbol = $"{InstrumentName}{ContractSuffix}";
            _symbol = Core.Instance.GetSymbol(new GetSymbolRequestParameters
            {
                SymbolFilter = new SymbolFilter { Name = fullSymbol },
                ConnectionName = ConnectionName
            });

            if (_symbol == null)
            {
                Log($"[ERROR] Symbol '{fullSymbol}' not found on '{ConnectionName}'. " +
                    "Check InstrumentName and ContractSuffix.", StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            // Resolve account
            _account = Core.Instance.Accounts
                .FirstOrDefault(a => a.ConnectionName == ConnectionName);

            if (_account == null)
            {
                Log($"[ERROR] No account found for connection '{ConnectionName}'.", StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            _dayStartBalance = _account.Balance;
            _currentDay      = TradingDay(DateTime.UtcNow);

            // Subscribe to 1M historical data
            _bars = _symbol.GetHistory(new HistoryRequestParameters
            {
                Symbol      = _symbol,
                HistoryType = HistoryType.Last,
                Period      = Period.MIN1,
                FromTime    = DateTime.UtcNow.AddDays(-5),
                Aggregation = new HistoryAggregationTime(Period.MIN1)
            });

            if (_bars == null)
            {
                Log("[ERROR] Could not load 1M historical data.", StrategyLoggingLevel.Error);
                Stop();
                return;
            }

            _bars.NewHistoricalItem += OnNewBar;

            // Position event hooks
            Core.Instance.Positions.Added   += OnPositionAdded;
            Core.Instance.Positions.Removed += OnPositionClosed;

            // Load news schedule
            LoadNewsSchedule();

            Log($"[OK] Symbol: {fullSymbol} | Tick: {_symbol.TickSize} | " +
                $"TickVal: {_symbol.TickCost} | Balance: ${_dayStartBalance:F2}",
                StrategyLoggingLevel.Info);
            Log($"[RISK] Daily cap: ${MaxDailyLoss} | Max SL: {MaxSLPerDay} | " +
                $"Contracts: {ContractsPerTrade} | SL: {StopLossTicks}t | TP: {TakeProfitTicks}t",
                StrategyLoggingLevel.Info);
        }

        protected override void OnStop()
        {
            if (_bars != null)
                _bars.NewHistoricalItem -= OnNewBar;

            Core.Instance.Positions.Added   -= OnPositionAdded;
            Core.Instance.Positions.Removed -= OnPositionClosed;

            Log($"Lucid Gold Scalper stopped. Day PnL: ${_dailyRealizedPnL:F2} | SLs: {_slCountToday}",
                StrategyLoggingLevel.Info);
        }

        #endregion

        // ─────────────────────────────────────────────
        // BAR PROCESSING — called on every new 1M bar
        // ─────────────────────────────────────────────

        #region OnNewBar

        private void OnNewBar(object sender, HistoricalEventArgs e)
        {
            lock (_lock)
            {
                if (_bars == null || _bars.Count < OBLookback + 5) return;

                var now = DateTime.UtcNow;

                // 1. Daily reset check
                CheckDailyReset(now);

                // 2. Hard stop time
                if (now.Hour >= HardStopHourUTC)
                {
                    if (_inTrade) Log("Hard stop hour reached. Let existing trade manage to exit.", StrategyLoggingLevel.Trading);
                    return;
                }

                // 3. Daily risk gates
                if (_dailyLimitHit)
                {
                    Log("[GATE] Daily limit hit — no new trades.", StrategyLoggingLevel.Trading);
                    return;
                }
                if (_slCountToday >= MaxSLPerDay)
                {
                    Log($"[GATE] Max SL count ({MaxSLPerDay}) reached — done for today.", StrategyLoggingLevel.Trading);
                    return;
                }

                // 4. Manage open trade (trailing stop, time-based exits)
                if (_inTrade)
                {
                    ManageOpenTrade(now);
                    return;
                }

                // 5. Kill Zone filter
                if (UseKillZonesOnly && !InKillZone(now))
                    return;

                // 6. News blackout
                if (InNewsBlackout(now))
                {
                    Log("[GATE] News blackout — skipping bar.", StrategyLoggingLevel.Trading);
                    return;
                }

                // 7. Update market structure
                UpdateSessionLevels();
                UpdateFairValueGaps();
                UpdateOrderBlocks();

                // 8. Look for entry
                var signal = EvaluateSignal();
                if (signal != Direction.None)
                    PlaceEntry(signal);
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        // ICT ANALYSIS
        // ─────────────────────────────────────────────

        #region Session Levels

        private void UpdateSessionLevels()
        {
            var now = DateTime.UtcNow;

            // Reset at start of London session each day
            if (!_sessionInitialized || 
                (now.Hour == LondonKZStartUTC && now.Minute == 0))
            {
                _prevSessionHigh   = _sessionHigh;
                _prevSessionLow    = _sessionLow;
                _sessionHigh       = double.MinValue;
                _sessionLow        = double.MaxValue;
                _sessionInitialized = true;
            }

            int count = _bars.Count;
            var bar = Bar(count - 1); // most recent completed bar
            if (bar == null) return;

            if (bar.High > _sessionHigh) _sessionHigh = bar.High;
            if (bar.Low  < _sessionLow)  _sessionLow  = bar.Low;
        }

        #endregion

        #region Fair Value Gaps (FVG)

        private void UpdateFairValueGaps()
        {
            int count = _bars.Count;
            if (count < 3) return;

            // We need 3 completed bars: [n-3], [n-2], [n-1]
            var b1 = Bar(count - 3); // oldest
            var b2 = Bar(count - 2); // middle (the FVG body bar)
            var b3 = Bar(count - 1); // newest completed

            if (b1 == null || b2 == null || b3 == null) return;

            double minFVG = FVGMinTicks * _symbol.TickSize;

            // ── Bullish FVG: b3.Low > b1.High (price jumped up, gap left below)
            double bullGap = b3.Low - b1.High;
            if (bullGap >= minFVG)
            {
                _bullFVGs.Add(new FairValueGap
                {
                    Top      = b3.Low,
                    Bottom   = b1.High,
                    IsBull   = true,
                    Time     = b2.Time,
                    Valid    = true,
                    Mitigated = false
                });
                TrimList(_bullFVGs, 15);
                Log($"[FVG] Bullish FVG detected: {b1.High:F2}–{b3.Low:F2} ({bullGap / _symbol.TickSize:F0} ticks)", StrategyLoggingLevel.Trading);
            }

            // ── Bearish FVG: b3.High < b1.Low (price dropped, gap left above)
            double bearGap = b1.Low - b3.High;
            if (bearGap >= minFVG)
            {
                _bearFVGs.Add(new FairValueGap
                {
                    Top      = b1.Low,
                    Bottom   = b3.High,
                    IsBull   = false,
                    Time     = b2.Time,
                    Valid    = true,
                    Mitigated = false
                });
                TrimList(_bearFVGs, 15);
                Log($"[FVG] Bearish FVG detected: {b3.High:F2}–{b1.Low:F2} ({bearGap / _symbol.TickSize:F0} ticks)", StrategyLoggingLevel.Trading);
            }

            // Invalidate fully-mitigated FVGs
            var current = Bar(count - 1);
            if (current == null) return;

            foreach (var fvg in _bullFVGs)
                if (fvg.Valid && current.Low < fvg.Bottom)
                    fvg.Valid = false;

            foreach (var fvg in _bearFVGs)
                if (fvg.Valid && current.High > fvg.Top)
                    fvg.Valid = false;
        }

        #endregion

        #region Order Blocks (OB)

        private void UpdateOrderBlocks()
        {
            int count = _bars.Count;
            if (count < OBLookback + 2) return;

            _bullOBs.Clear();
            _bearOBs.Clear();

            // Scan lookback for displacement moves that reveal an order block
            for (int i = count - OBLookback; i < count - 2; i++)
            {
                var ob   = Bar(i);       // potential order block candle
                var disp = Bar(i + 2);   // displacement candle (2 bars later)
                if (ob == null || disp == null) continue;

                double dispBody = Math.Abs(disp.Close - disp.Open);
                double obBody   = Math.Abs(ob.Close   - ob.Open);
                if (obBody <= 0) continue;

                // ── Bullish OB: Bearish candle → displacement up
                if (ob.Close   < ob.Open   &&          // OB candle is bearish
                    disp.Close > disp.Open &&           // Displacement is bullish
                    dispBody   > obBody * 1.5)          // Strong displacement (1.5× OB body)
                {
                    _bullOBs.Add(new OrderBlock
                    {
                        Top    = ob.Open,   // Top of bearish OB = its open
                        Bottom = ob.Close,  // Bottom = its close
                        Time   = ob.Time,
                        IsBull = true,
                        Valid  = true
                    });
                }

                // ── Bearish OB: Bullish candle → displacement down
                if (ob.Close   > ob.Open   &&
                    disp.Close < disp.Open &&
                    dispBody   > obBody * 1.5)
                {
                    _bearOBs.Add(new OrderBlock
                    {
                        Top    = ob.Close,  // Top of bullish OB = its close
                        Bottom = ob.Open,   // Bottom = its open
                        Time   = ob.Time,
                        IsBull = false,
                        Valid  = true
                    });
                }
            }
        }

        #endregion

        #region Liquidity Sweep Detection

        /// <summary>
        /// Detects if the previous bar swept liquidity and reversed —
        /// i.e., wick below session low (for long) or above session high (for short),
        /// then closed back in the opposite direction.
        /// </summary>
        private bool LiquiditySweepDetected(Direction dir)
        {
            int count = _bars.Count;
            if (count < 3) return false;

            var sweep = Bar(count - 2); // the "sweep" candle (one bar ago)
            if (sweep == null) return false;

            if (dir == Direction.Long)
            {
                // Swept lows: wick below previous session low, closed bullish
                bool sweptLow   = sweep.Low  < _prevSessionLow || sweep.Low < _sessionLow;
                bool closedBull = sweep.Close > sweep.Open;
                return sweptLow && closedBull;
            }
            else
            {
                // Swept highs: wick above previous session high, closed bearish
                bool sweptHigh  = sweep.High > _prevSessionHigh || sweep.High > _sessionHigh;
                bool closedBear = sweep.Close < sweep.Open;
                return sweptHigh && closedBear;
            }
        }

        #endregion

        #region Market Structure Shift (MSS)

        /// <summary>
        /// Bullish MSS: Most recent bar closed above the highest high in the last N bars.
        /// Signals a break of bearish structure — potential reversal up.
        /// </summary>
        private bool BullishMSS()
        {
            int count = _bars.Count;
            if (count < 12) return false;

            var current = Bar(count - 1);
            if (current == null || current.Close <= current.Open) return false; // Must close bullish

            // Find the recent swing high across last 10 bars (excluding current)
            double swingHigh = double.MinValue;
            for (int i = count - 11; i < count - 1; i++)
            {
                var b = Bar(i);
                if (b != null && b.High > swingHigh) swingHigh = b.High;
            }

            return current.Close > swingHigh;
        }

        /// <summary>
        /// Bearish MSS: Most recent bar closed below the lowest low in the last N bars.
        /// Signals a break of bullish structure — potential reversal down.
        /// </summary>
        private bool BearishMSS()
        {
            int count = _bars.Count;
            if (count < 12) return false;

            var current = Bar(count - 1);
            if (current == null || current.Close >= current.Open) return false; // Must close bearish

            double swingLow = double.MaxValue;
            for (int i = count - 11; i < count - 1; i++)
            {
                var b = Bar(i);
                if (b != null && b.Low < swingLow) swingLow = b.Low;
            }

            return current.Close < swingLow;
        }

        #endregion

        // ─────────────────────────────────────────────
        // ORDER FLOW ANALYSIS
        // ─────────────────────────────────────────────

        #region Delta & Imbalance

        /// <summary>
        /// Estimates cumulative delta for the last N bars using volume + price position.
        /// In production, replace with Quantower's actual footprint/cluster data if available.
        /// The cluster data can be accessed via HistoricalData with HistoryType.Bid/Ask if
        /// your Rithmic feed supports it.
        /// </summary>
        private double EstimateDelta(int barsBack = 3)
        {
            int count = _bars.Count;
            if (count < barsBack) return 0;

            double cumDelta = 0;
            for (int i = count - barsBack; i < count; i++)
            {
                var b = Bar(i);
                if (b == null) continue;

                double range = b.High - b.Low;
                if (range <= 0) continue;

                // Directional volume estimate:
                // Bar close position in its range × volume gives a proxy for buying vs selling
                double closeRatio = (b.Close - b.Low) / range;  // 1 = closed at top, 0 = bottom
                double delta      = (closeRatio * 2 - 1) * b.Volume; // +vol to -vol scaled
                cumDelta += delta;
            }
            return cumDelta;
        }

        private bool OrderFlowAligned(Direction dir)
        {
            double delta = EstimateDelta(3);

            if (dir == Direction.Long  && delta < MinDeltaThreshold)  return false;
            if (dir == Direction.Short && delta > -MinDeltaThreshold) return false;

            return true;
        }

        #endregion

        // ─────────────────────────────────────────────
        // SIGNAL EVALUATION
        // ─────────────────────────────────────────────

        #region Signal Logic

        private Direction EvaluateSignal()
        {
            // ── LONG CONFLUENCES ──────────────────────────────────────────────
            //  The setup: Price has swept sell-side liquidity (lows), then reversed.
            //  We look to enter on the retracement back into a bullish FVG or OB.
            //
            //  Model:
            //   1. Liquidity sweep of session lows         (stop run below structure)
            //   2. Bullish MSS                             (price broke above swing high)
            //   3. Price retesting into bullish FVG / OB   (premium/discount logic)
            //   4. Order flow confirmation                  (delta positive)

            bool longFVG = _bullFVGs.Any(fvg =>
                fvg.Valid &&
                CurrentPrice() >= fvg.Bottom &&
                CurrentPrice() <= fvg.Top + _symbol.TickSize * 5);

            bool longOB = _bullOBs.Any(ob =>
                ob.Valid &&
                CurrentPrice() >= ob.Bottom - _symbol.TickSize * 3 &&
                CurrentPrice() <= ob.Top);

            bool longSweep = !RequireLiquiditySweep || LiquiditySweepDetected(Direction.Long);
            bool longMSS   = !RequireMSS            || BullishMSS();
            bool longOF    = OrderFlowAligned(Direction.Long);

            // Need: (FVG or OB) + sweep + MSS + order flow
            bool longReady = (longFVG || longOB) && longSweep && longMSS && longOF;

            if (longReady)
            {
                Log($"[SIGNAL] LONG — FVG:{longFVG} OB:{longOB} Sweep:{longSweep} MSS:{longMSS} OF:{longOF}",
                    StrategyLoggingLevel.Trading);
                return Direction.Long;
            }

            // ── SHORT CONFLUENCES ─────────────────────────────────────────────
            //  The setup: Price has swept buy-side liquidity (highs), then reversed.
            //  Enter on retracement into bearish FVG or bearish OB.
            //
            //  Model:
            //   1. Liquidity sweep of session highs        (stop run above structure)
            //   2. Bearish MSS                             (price broke below swing low)
            //   3. Price retesting into bearish FVG / OB  (sell from premium)
            //   4. Order flow confirmation                  (delta negative)

            bool shortFVG = _bearFVGs.Any(fvg =>
                fvg.Valid &&
                CurrentPrice() <= fvg.Top &&
                CurrentPrice() >= fvg.Bottom - _symbol.TickSize * 5);

            bool shortOB = _bearOBs.Any(ob =>
                ob.Valid &&
                CurrentPrice() <= ob.Top + _symbol.TickSize * 3 &&
                CurrentPrice() >= ob.Bottom);

            bool shortSweep = !RequireLiquiditySweep || LiquiditySweepDetected(Direction.Short);
            bool shortMSS   = !RequireMSS            || BearishMSS();
            bool shortOF    = OrderFlowAligned(Direction.Short);

            bool shortReady = (shortFVG || shortOB) && shortSweep && shortMSS && shortOF;

            if (shortReady)
            {
                Log($"[SIGNAL] SHORT — FVG:{shortFVG} OB:{shortOB} Sweep:{shortSweep} MSS:{shortMSS} OF:{shortOF}",
                    StrategyLoggingLevel.Trading);
                return Direction.Short;
            }

            return Direction.None;
        }

        #endregion

        // ─────────────────────────────────────────────
        // TRADE EXECUTION
        // ─────────────────────────────────────────────

        #region PlaceEntry

        private void PlaceEntry(Direction dir)
        {
            if (_inTrade) return;

            double tickSize  = _symbol.TickSize;
            double tickValue = _symbol.TickCost;   // $ per tick per contract
            double bid = _symbol.Bid;
            double ask = _symbol.Ask;

            double entryPrice, slPrice, tpPrice;
            Side side;

            if (dir == Direction.Long)
            {
                side       = Side.Buy;
                entryPrice = ask;
                slPrice    = entryPrice - StopLossTicks  * tickSize;
                tpPrice    = entryPrice + TakeProfitTicks * tickSize;
            }
            else
            {
                side       = Side.Sell;
                entryPrice = bid;
                slPrice    = entryPrice + StopLossTicks  * tickSize;
                tpPrice    = entryPrice - TakeProfitTicks * tickSize;
            }

            // Pre-trade risk check: does this trade risk push us over daily limit?
            double tradeRisk = StopLossTicks * tickValue * ContractsPerTrade;
            double remaining = MaxDailyLoss - Math.Abs(_dailyRealizedPnL);

            if (tradeRisk > remaining)
            {
                Log($"[SKIP] Trade risk ${tradeRisk:F2} > remaining budget ${remaining:F2}. Skipping.",
                    StrategyLoggingLevel.Trading);
                return;
            }

            // ── Place market order with SL + TP ──────────────────────────
            var request = new PlaceOrderRequestParameters
            {
                Symbol      = _symbol,
                Account     = _account,
                OrderTypeId = OrderType.Market,
                Side        = side,
                Quantity    = ContractsPerTrade,
                StopLoss    = SlTpHolder.CreateSL(slPrice, PriceMeasurement.Absolute),
                TakeProfit  = SlTpHolder.CreateTP(tpPrice, PriceMeasurement.Absolute),
                Comment     = $"LucidGold_{dir}_{DateTime.UtcNow:HHmm}"
            };

            var result = Core.Instance.PlaceOrder(request);

            if (result.Status == TradingOperationResultStatus.Success)
            {
                _inTrade          = true;
                _trailHighWater   = dir == Direction.Long ? entryPrice : entryPrice;

                Log($"[TRADE] {dir.ToString().ToUpper()} {ContractsPerTrade}× {_symbol.Name} " +
                    $"@ {entryPrice:F2} | SL: {slPrice:F2} | TP: {tpPrice:F2} | Risk: ${tradeRisk:F2}",
                    StrategyLoggingLevel.Trading);
            }
            else
            {
                Log($"[ERROR] Order failed: {result.Message}", StrategyLoggingLevel.Error);
            }
        }

        #endregion

        #region ManageOpenTrade (Trailing Stop)

        private void ManageOpenTrade(DateTime now)
        {
            if (!UseTrailingStop || _openPosition == null) return;

            double tickSize  = _symbol.TickSize;
            double price     = _symbol.Last;
            double activateBy = TrailActivationTicks * tickSize;
            double trailBy    = TrailStepTicks       * tickSize;

            if (_openPosition.Side == PositionSide.Long)
            {
                // Update high watermark
                if (price > _trailHighWater) _trailHighWater = price;

                double entry   = _openPosition.EntryPrice;
                double newSL   = _trailHighWater - trailBy;

                // Only trail once we're in profit by TrailActivationTicks
                if (_trailHighWater - entry >= activateBy)
                {
                    double currentSL = _openPosition.StopLoss;
                    if (newSL > currentSL + tickSize)
                    {
                        var result = Core.Instance.ModifyPosition(new ModifyPositionRequestParameters
                        {
                            Position = _openPosition,
                            StopLoss = SlTpHolder.CreateSL(newSL, PriceMeasurement.Absolute)
                        });
                        if (result.Status == TradingOperationResultStatus.Success)
                            Log($"[TRAIL] SL moved to {newSL:F2} (watermark: {_trailHighWater:F2})",
                                StrategyLoggingLevel.Trading);
                    }
                }
            }
            else // Short
            {
                // Update low watermark
                if (price < _trailHighWater || _trailHighWater == double.MinValue)
                    _trailHighWater = price;

                double entry = _openPosition.EntryPrice;
                double newSL = _trailHighWater + trailBy;

                if (entry - _trailHighWater >= activateBy)
                {
                    double currentSL = _openPosition.StopLoss;
                    if (newSL < currentSL - tickSize)
                    {
                        Core.Instance.ModifyPosition(new ModifyPositionRequestParameters
                        {
                            Position = _openPosition,
                            StopLoss = SlTpHolder.CreateSL(newSL, PriceMeasurement.Absolute)
                        });
                    }
                }
            }
        }

        #endregion

        // ─────────────────────────────────────────────
        // POSITION EVENT HANDLERS
        // ─────────────────────────────────────────────

        #region Position Events

        private void OnPositionAdded(object sender, PositionEventArgs e)
        {
            if (!IsOurPosition(e.Position)) return;
            _openPosition  = e.Position;
            _inTrade       = true;
            _trailHighWater = e.Position.EntryPrice;
            Log($"[POS OPEN] {e.Position.Side} {e.Position.Quantity}× @ {e.Position.EntryPrice:F2}",
                StrategyLoggingLevel.Trading);
        }

        private void OnPositionClosed(object sender, PositionEventArgs e)
        {
            if (!IsOurPosition(e.Position)) return;

            double pnl = e.Position.GrossPnl;
            _dailyRealizedPnL += pnl;

            if (pnl < 0)
            {
                _slCountToday++;
                Log($"[SL HIT] PnL: ${pnl:F2} | SL count: {_slCountToday}/{MaxSLPerDay} | Day PnL: ${_dailyRealizedPnL:F2}",
                    StrategyLoggingLevel.Trading);
            }
            else
            {
                Log($"[TP HIT] PnL: ${pnl:F2} | Day PnL: ${_dailyRealizedPnL:F2}",
                    StrategyLoggingLevel.Trading);
            }

            _inTrade      = false;
            _openPosition = null;

            // Check daily loss limit after close
            if (_dailyRealizedPnL <= -MaxDailyLoss)
            {
                _dailyLimitHit = true;
                Log($"[!!] DAILY LOSS LIMIT REACHED (${_dailyRealizedPnL:F2}). No more trades today.",
                    StrategyLoggingLevel.Error);
            }

            if (_slCountToday >= MaxSLPerDay)
                Log($"[!!] MAX SL COUNT REACHED ({_slCountToday}). Done for the day.",
                    StrategyLoggingLevel.Error);
        }

        private bool IsOurPosition(Position pos)
        {
            return pos != null &&
                   pos.Symbol?.Name?.StartsWith(InstrumentName, StringComparison.OrdinalIgnoreCase) == true &&
                   pos.Account?.Id == _account?.Id;
        }

        #endregion

        // ─────────────────────────────────────────────
        // RISK & DAILY RESET
        // ─────────────────────────────────────────────

        #region Risk Management

        private void CheckDailyReset(DateTime utcNow)
        {
            var day = TradingDay(utcNow);
            if (day == _currentDay) return;

            Log($"[DAY RESET] Prev day PnL: ${_dailyRealizedPnL:F2} | SLs: {_slCountToday}", StrategyLoggingLevel.Info);

            _currentDay         = day;
            _slCountToday       = 0;
            _dailyRealizedPnL   = 0;
            _dailyLimitHit      = false;
            _dayStartBalance    = _account?.Balance ?? _dayStartBalance;
            _sessionHigh        = double.MinValue;
            _sessionLow         = double.MaxValue;
            _sessionInitialized = false;

            _bullFVGs.Clear();
            _bearFVGs.Clear();
            _bullOBs.Clear();
            _bearOBs.Clear();

            Log($"[NEW DAY] Balance: ${_dayStartBalance:F2}", StrategyLoggingLevel.Info);
        }

        /// <summary>
        /// Emergency flat: called if somehow open PnL pushes us past daily limit.
        /// </summary>
        private void EmergencyFlatIfNeeded()
        {
            if (_openPosition == null) return;

            double unrealized = _openPosition.GrossPnl;
            if (_dailyRealizedPnL + unrealized <= -MaxDailyLoss)
            {
                Log("[!!] Unrealized loss breaching daily limit — emergency close!", StrategyLoggingLevel.Error);
                Core.Instance.ClosePosition(new ClosePositionRequestParameters { Position = _openPosition });
            }
        }

        /// <summary>Returns the CME trading day (rolls at 6 PM ET = 23:00 UTC).</summary>
        private static DateTime TradingDay(DateTime utc)
            => utc.Hour >= 23 ? utc.Date.AddDays(1) : utc.Date;

        #endregion

        // ─────────────────────────────────────────────
        // TIME FILTERS
        // ─────────────────────────────────────────────

        #region Kill Zones

        private bool InKillZone(DateTime utc)
        {
            double h = utc.Hour + utc.Minute / 60.0;
            bool london = h >= LondonKZStartUTC  && h < LondonKZEndUTC;
            bool ny     = h >= NYOpenKZStartUTC  && h < NYOpenKZEndUTC;
            return london || ny;
        }

        #endregion

        #region News Blackout

        private bool InNewsBlackout(DateTime utc)
        {
            foreach (var ev in _newsEvents)
            {
                if (utc.DayOfWeek != ev.Day) continue;

                var eventTime = utc.Date + ev.TimeUTC;
                double minsTo = (eventTime - utc).TotalMinutes;

                if (minsTo <= NewsBlackoutBefore && minsTo >= -NewsBlackoutAfter)
                {
                    Log($"[NEWS] Blackout: {ev.Name} at {ev.TimeUTC} UTC (in {minsTo:F0} min)",
                        StrategyLoggingLevel.Trading);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Hardcoded recurring news schedule (UTC).
        /// Update monthly or scrape ForexFactory for exact dates.
        /// High-impact events that move Gold:
        ///   CPI, NFP, FOMC, PCE, PPI, ISM, GDP, Jobless Claims, Retail Sales
        /// </summary>
        private void LoadNewsSchedule()
        {
            // ── Tuesday ──────────────────────────────────────────────────────
            _newsEvents.Add(new NewsEvent("CPI", DayOfWeek.Tuesday, 12, 30));      // 8:30 AM ET
            _newsEvents.Add(new NewsEvent("PPI", DayOfWeek.Tuesday, 12, 30));      // alternate weeks

            // ── Wednesday ────────────────────────────────────────────────────
            _newsEvents.Add(new NewsEvent("ADP Employment",      DayOfWeek.Wednesday, 12, 15));
            _newsEvents.Add(new NewsEvent("FOMC Decision",       DayOfWeek.Wednesday, 18,  0)); // 2 PM ET
            _newsEvents.Add(new NewsEvent("FOMC Minutes",        DayOfWeek.Wednesday, 18,  0));
            _newsEvents.Add(new NewsEvent("Crude Oil Inventory", DayOfWeek.Wednesday, 15,  0)); // 10:30 AM ET
            _newsEvents.Add(new NewsEvent("ISM Services",        DayOfWeek.Wednesday, 14,  0));

            // ── Thursday ─────────────────────────────────────────────────────
            _newsEvents.Add(new NewsEvent("Jobless Claims",  DayOfWeek.Thursday, 12, 30));
            _newsEvents.Add(new NewsEvent("GDP",             DayOfWeek.Thursday, 12, 30)); // advance/prelim

            // ── Friday ───────────────────────────────────────────────────────
            _newsEvents.Add(new NewsEvent("NFP",          DayOfWeek.Friday, 12, 30));   // 1st Friday
            _newsEvents.Add(new NewsEvent("PCE",          DayOfWeek.Friday, 12, 30));
            _newsEvents.Add(new NewsEvent("Retail Sales", DayOfWeek.Friday, 12, 30));
            _newsEvents.Add(new NewsEvent("ISM Mfg",     DayOfWeek.Friday, 14,  0));

            Log($"[NEWS] Loaded {_newsEvents.Count} recurring news events.", StrategyLoggingLevel.Info);
        }

        #endregion

        // ─────────────────────────────────────────────
        // UTILITY HELPERS
        // ─────────────────────────────────────────────

        #region Helpers

        private IHistoricalItem Bar(int index)
        {
            try { return _bars[index] as IHistoricalItem; }
            catch { return null; }
        }

        private double CurrentPrice()
        {
            return _symbol?.Last ?? 0;
        }

        private static void TrimList<T>(List<T> list, int max)
        {
            while (list.Count > max) list.RemoveAt(0);
        }

        #endregion

        // ─────────────────────────────────────────────
        // DATA STRUCTURES
        // ─────────────────────────────────────────────

        #region Inner Types

        private enum Direction { None, Long, Short }

        private class FairValueGap
        {
            public double   Top       { get; set; }
            public double   Bottom    { get; set; }
            public bool     IsBull    { get; set; }
            public DateTime Time      { get; set; }
            public bool     Valid     { get; set; }
            public bool     Mitigated { get; set; }
        }

        private class OrderBlock
        {
            public double   Top    { get; set; }
            public double   Bottom { get; set; }
            public bool     IsBull { get; set; }
            public DateTime Time   { get; set; }
            public bool     Valid  { get; set; }
        }

        private class NewsEvent
        {
            public string      Name    { get; }
            public DayOfWeek   Day     { get; }
            public TimeSpan    TimeUTC { get; }

            public NewsEvent(string name, DayOfWeek day, int hourUTC, int minuteUTC)
            {
                Name    = name;
                Day     = day;
                TimeUTC = new TimeSpan(hourUTC, minuteUTC, 0);
            }
        }

        #endregion
    }
}
