# Quantower-MGC-4Trades-100Ticks-Each-1Lot-Algo

# LUCID GOLD SCALPER
### Quantower C# Strategy — 25K Lucid Flex | MGC / GC Futures
### ICT Concepts + Order Flow | 1M Scalper

---

## Account Rules Summary (Lucid 25K Flex)

| Rule | Eval | Funded |
|------|------|--------|
| Profit Target | $1,250 | — |
| Max Loss (EOD) | $1,000 | $1,000 |
| Daily Loss Limit | None (eval) | None |
| Consistency | 50% (eval) | None |
| Max Size | 2 Mini OR 20 Micro | 2 Mini OR 20 Micro |
| Payouts to Live | — | 5 |
| Scaling Plan | — | Yes |

**Our internal daily cap: $150 | Max SL/day: 2**  
This is far tighter than the account rules — by design. Preserving the account.

---

## Setup Instructions

### 1. Visual Studio Project
1. Open `LucidGoldScalper.csproj` in Visual Studio 2022+
2. **Add reference**: Right-click References → Add Reference → Browse  
   Navigate to: `C:\Program Files\Quantower\Bin\`  
   Select: `TradingPlatform.BusinessLayer.dll`
3. **Build** (Ctrl+Shift+B) → should produce zero errors
4. The DLL auto-copies to Quantower's strategies folder (see `.csproj` OutputPath)

### 2. Quantower Setup
1. Open Quantower → **Settings → General → Algo**
2. Set Socket port: **21,000**
3. Check ✅ "Allow connection from Visual Studio (for scripts debugging)"
4. Go to **Strategy Manager** panel → Add → find `LucidGoldScalper`

### 3. Rithmic Connection
- Platform: Quantower with Rithmic data feed (as shown in your setup)
- Connection Name parameter: match exactly what shows in Quantower connections panel
- Symbol: `MGC` for Micro Gold, `GC` for full Gold

### 4. Contract Suffix (Update Quarterly!)
The futures contract rolls 4× per year:
| Month | Suffix | Roll Dates (approx) |
|-------|--------|---------------------|
| March | H5 | Roll before Feb expiry |
| June  | M5 | Roll before May expiry |
| Sep   | U5 | Roll before Aug expiry |
| Dec   | Z5 | Roll before Nov expiry |

Currently set to `Z5`. Update `ContractSuffix` each roll.

---

## Strategy Logic

### The Core Model (ICT + Order Flow)
This is a **Sweep → Displace → Retest** scalping model:

```
1. Price sweeps liquidity (stop run above highs / below lows)
2. Displacement move away from swept level (strong directional candle)
3. Market Structure Shift (breaks a swing high/low in new direction)
4. Price retraces into a Fair Value Gap or Order Block
5. Order Flow confirms direction (positive delta for long, negative for short)
6. Entry at the FVG/OB zone with tight SL and 1.5-2R TP
```

### Kill Zones (default UTC times — adjust for DST)
| Session | UTC | ET (Standard) | ET (Daylight) |
|---------|-----|---------------|---------------|
| London KZ | 08:00–10:00 | 3:00–5:00 AM | 4:00–6:00 AM |
| NY Open KZ | 13:00–16:00 | 8:00–11:00 AM | 9:00 AM–12:00 PM |

> **Note**: Adjust UTC hours seasonally (US DST: 2nd Sun March → 1st Sun Nov)

### ICT Concepts Implemented
| Concept | Implementation |
|---------|---------------|
| Fair Value Gap (FVG) | 3-bar pattern: Bar3.Low > Bar1.High (bull) or Bar3.High < Bar1.Low (bear) |
| Order Block (OB) | Last opposite candle before displacement (1.5× body ratio) |
| Liquidity Sweep | Wick beyond session high/low → reversal close |
| Market Structure Shift | Close beyond recent 10-bar swing high/low |
| Session Levels | Rolling high/low updated each London open |

### Order Flow Analysis
- **Delta estimation**: Uses bar's close position in range × volume (proxy)
- **Production upgrade**: Connect to Quantower footprint/cluster data from Rithmic  
  (requires `HistoryType.Bid` + `HistoryType.Ask` separation — available with Rithmic full feed)

---

## Configuration Guide

### Risk Parameters
| Param | Default | Notes |
|-------|---------|-------|
| Max Daily Loss | $150 | Hard stop — no trades after |
| Max SL Per Day | 2 | After 2 losses, done for day |
| Contracts | 2 (MGC) | Max 20 MGC or 2 GC per rules |
| Stop Loss Ticks | 20 | 20 ticks MGC = $20 risk/contract |
| Take Profit Ticks | 35 | ~1.75R ratio |

**With defaults (2 MGC, 20-tick SL):**
- Risk per trade: 2 × 20 × $1 = **$40**
- Max 2 SLs = **$80** max daily loss (well under $150 cap)
- TP per trade: 2 × 35 × $1 = **$70**

**Want more contracts?** Scale up gradually after profitable days. Max allowed = 20 MGC.

### Tick Values Reference
| Instrument | Tick Size | Tick Value (1 contract) |
|------------|-----------|------------------------|
| MGC (Micro Gold) | 0.10 | $1.00 |
| GC (Gold) | 0.10 | $10.00 |

> With GC: 1 contract, 10-tick SL = $100 risk. Be careful with GC sizing.

---

## News Schedule
The strategy has a hardcoded blackout ±30min before / ±15min after these recurring events:

| Day | Event | UTC Time |
|-----|-------|----------|
| Tuesday | CPI / PPI | 12:30 |
| Wednesday | ADP, FOMC, Crude Oil, ISM | 12:15 / 18:00 / 15:00 / 14:00 |
| Thursday | Jobless Claims, GDP | 12:30 |
| Friday | NFP, PCE, Retail Sales, ISM Mfg | 12:30 / 14:00 |

> These are **recurring day-of-week** filters. They fire every week on that day.
> For non-recurring high-impact news (Fed speeches, geopolitical events), pause the strategy manually.

---

## Visual Studio Debugging

With Socket port 21,000 enabled in Quantower:

1. Start Quantower + load strategy
2. In VS: **Debug → Attach to Process** → find `Quantower.exe`
3. Set breakpoints anywhere in `LucidGoldScalper.cs`
4. Strategy will pause at breakpoints on next bar

Useful breakpoints to set:
- `EvaluateSignal()` → watch confluence logic
- `PlaceEntry()` → inspect prices before order
- `OnPositionClosed()` → watch PnL accumulation

---

## Known Upgrade Paths

### Phase 2 Enhancements
- [ ] **Real footprint delta**: Quantower supports Rithmic Bid/Ask volume split — replace `EstimateDelta()` with actual cluster data
- [ ] **HTF bias**: Add 5M/15M trend filter so 1M trades align with higher-timeframe direction
- [ ] **Partial TP**: Close 50% at 1R, move SL to BE, let rest run to 2R
- [ ] **Session high/low from previous day** (PDH/PDL) as key liquidity levels
- [ ] **Time-based exit**: Force flat 15 min before NY close (20:45 UTC)
- [ ] **Win streak scaling**: Allow 3 contracts after 3 consecutive green days

### Phase 3 (Production)
- [ ] ForexFactory API integration for real-time news (or webhook from your news feed)
- [ ] Telegram/Discord alert on each trade entry/exit
- [ ] CSV trade log for daily review + stats dashboard

---

## File Structure
```
LucidGoldScalper/
├── LucidGoldScalper.cs      ← Main strategy (all logic)
├── LucidGoldScalper.csproj  ← VS project file
└── README.md                ← This file
```

---

## Lucid Prop Account Checklist (Before Going Live)

- [ ] Contract suffix updated to current front month
- [ ] Contracts set correctly (MGC: 1–20, GC: 1–2)
- [ ] Daily loss = $150 (well under $1,000 account limit)
- [ ] Kill zones checked for DST / timezone
- [ ] Kill zones checked for DST / timezone
- [ ] Tested on sim for minimum 1 week before funding
- [ ] No overnight positions (futures settle — check contract specs)
- [ ] Consistency rule in eval: no single day > 50% of total profit target

---

*Built for Pranay Gajbhiye — BlackObsidian AMC / Zorvain Street*
