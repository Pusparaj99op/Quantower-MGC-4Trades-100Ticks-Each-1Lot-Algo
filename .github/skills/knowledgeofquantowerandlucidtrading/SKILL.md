---
name: knowledgeofquantowerandlucidtrading
description: Build, debug, and audit algorithmic trading strategies and indicators for the Quantower platform using the Quantower ALGO (Quantower Algo) C# extension in Visual Studio 2022 on Windows 11, engineered to trade compliantly on a Lucid Trading LucidFlex evaluation or funded futures account. Use whenever the user mentions Quantower, Quantower ALGO/Algo, a Strategy or Indicator class for Quantower, the Strategy Runner, Lucid Trading, LucidFlex, LucidPro, LucidDirect, a prop-firm futures eval/funded account, Max Loss Limit, consistency rule, scaling plan, or "my Lucid account" — even if only one half of the combo is mentioned (e.g. "write a Quantower strategy" with no firm named, or "will this breach my Lucid consistency rule" with no code shown). Also covers setting up the Quantower ALGO VS extension on Windows 11, debugging in VS2022, and reviewing existing Quantower C# code for risk-rule compliance before it goes live on a funded account.
---

# Quantower ALGO + Lucid Trading (Flex / Pro / Direct) Strategy Development

## What this skill is for

Pranay trades futures through Lucid Trading's prop firm program (currently on a LucidFlex account) and builds his execution logic in Quantower, writing C# Strategies and Indicators in Visual Studio 2022 via the Quantower ALGO extension. This skill also covers LucidPro and LucidDirect, since the underlying Quantower work and most of the compliance machinery (EOD drawdown, hedge/microscalping bans, trading hours) are shared — only the specific rule numbers and a few behaviors (Daily Loss Limit, consistency phase) differ by account type. That combination has two failure modes that show up constantly if you treat them separately:

1. **Quantower-shaped bugs** — getting the Strategy/Indicator lifecycle wrong (e.g. placing orders before `OnRun()`, never unsubscribing in `OnRemove()`, hardcoding a symbol/account instead of exposing them as Input Parameters) that only surface once the strategy is actually running live or in the Strategy Tester.
2. **Lucid-shaped account breaches** — code that trades a perfectly fine signal but ignores the account's Max Loss Limit, consistency rule, or the 4:45 PM EST flatten requirement, and quietly blows the account on day one.

The job here is to never produce one without the other: every strategy that touches a Lucid account should be a Quantower-correct strategy with the Lucid compliance layer wired in from the start, not bolted on after the fact. Treat the compliance layer as a separate concern from the trading signal — the signal decides *whether* to trade, the guard decides *whether it's currently allowed*. Keeping them separate means Pranay can swap strategies without re-deriving the risk logic each time, and swap accounts/sizes without touching the signal.

## Rule-churn caveat — read this before hardcoding any number

Lucid Trading has restructured its rules several times since launching (renaming LucidTest to LucidPro Eval, adjusting payout counts/caps, moving the profit split from 80/20 to 90/10, adding/removing account sizes, discontinuing LucidBlack). The figures in `references/lucid-account-rules.md` are a snapshot — accurate as of when this skill was written, sourced from Lucid's own help center plus cross-checked third-party guides (some figures cross-validated against two independent worked examples) — but they **will** drift. Two consequences for how you write code:

- **Never hardcode a threshold inline in trading logic.** Every dollar figure, percentage, and time cutoff should be an `[InputParameter]` on the strategy (see `assets/LucidRiskGuard.cs` for the pattern), so a rule change is a settings-screen edit, not a recompile.
- If a number matters enough that getting it wrong could breach the account (Max Loss Limit, Daily Loss Limit, consistency %, profit target), say so explicitly and suggest Pranay confirm it against his actual Lucid dashboard or `support.lucidtrading.com` before trusting it in something that trades real simulated capital. Don't silently assume the snapshot is still current.

## Quantower ALGO essentials

Quantower ALGO is Quantower's official extension for Visual Studio (2022 supported) that lets you write Strategies and Indicators in plain C# against the Quantower API (`api.quantower.com`) — no proprietary scripting language. Full cheat sheet in `references/quantower-api.md`; the load-bearing pieces:

**Strategy lifecycle** — a Strategy is a class inheriting `Strategy`, with methods called by the Strategy Runner panel:
- `OnCreated()` — once, when selected from Strategy Lookup. One-time setup.
- `OnRun()` — when the user presses Run. Subscribe to quotes, validate inputs, start the risk guard here.
- `OnStop()` — when the user presses Stop. Unsubscribe, flatten if appropriate, clear state.
- `OnRemove()` — when the panel closes or another strategy is selected. Final cleanup.
- `OnGetMetrics()` — returns `List<StrategyMetric>` shown live in the Strategy Runner panel (use this to surface MLL headroom, today's consistency %, etc. — Pranay should be able to glance at the panel and know if he's near a breach).

**Input Parameters** — any field marked `[InputParameter("Display name", sortIndex, min, max, increment, decimalPlaces)]` becomes an editable setting in the Strategy/Indicator's Settings screen. Symbol and Account also take this attribute (`[InputParameter("Account")] public Account account;`), which is how a strategy stays reusable across account sizes instead of being rewritten per account.

**Trading operations** — `Core.Instance` is the entry point for everything:
```csharp
Core.Instance.PlaceOrder(new PlaceOrderRequestParameters() {
    Account = this.account,
    Symbol = this.symbol,
    Side = Side.Buy,
    Quantity = qty,
    OrderTypeId = OrderType.Market,
    StopLoss = SlTpHolder.CreateSL(stopOffsetTicks, PriceMeasurement.Offset),
    TakeProfit = SlTpHolder.CreateSL(targetOffsetTicks, PriceMeasurement.Offset),
});
```
`Core.Instance.ModifyOrder(...)`, `CancelOrder(...)`, and `ClosePosition(...)` (or `position.Close()`) round out the set. Every call returns a `TradingOperationResult` with `Status`/`Message` — always check it and log on failure rather than assuming the order went through.

**Indicators** inherit `Indicator`, declare line series in the constructor (`AddLineSeries(name, color, width, style)`), and compute in `OnUpdate()` using `GetPrice(PriceType, offset)` plus `SetValue(...)`.

**Logging** — `Log(message, StrategyLoggingLevel.Info | .Error | .Trading)`. Use `.Trading` for anything that should read as an audit trail of compliance decisions (blocked order, flatten triggered, consistency warning) — that log is the first thing to check after the fact if Pranay ever disputes a breach with support.

## Lucid compliance essentials (Flex / Pro / Direct)

Full detail, current account-size tables, and worked drawdown-lock math in `references/lucid-account-rules.md`. The behaviors that belong in code, not just in Pranay's head:

1. **End-of-day Max Loss Limit (MLL) on all three account types, not intraday.** Lucid only evaluates drawdown at the daily close — intraday swings don't matter as long as the closing balance stays above the MLL. Don't build an intraday-trailing kill switch (that's a different firm's rule); build an EOD check that runs once per session close.
2. **Daily Loss Limit (DLL) — Pro and Direct only, never Flex — and it's a *soft* breach.** Hitting the DLL pauses new entries for the rest of the session; it does **not** fail the account the way an MLL breach does. Keep these as two separate flags in code (`IsBreached` vs `DailyLossLimitHitToday`) — collapsing them into one "stop trading" boolean loses the distinction Pranay actually needs (today's pause vs. the account being gone).
3. **Consistency rule — cap and *active phase* both differ by account type.** Flex: 50% cap during evaluation only, then dropped entirely once funded. Pro: no cap in eval, then a 40% cap that applies to every payout cycle for the life of the funded account. Direct: 20% cap from day one (no eval to be exempt during). Getting the active-phase logic backwards (e.g. enforcing Flex's eval cap after it's funded, or skipping Pro's funded cap because there was none in eval) is an easy and consequential mistake.
4. **Flatten by 4:45 PM EST, trade only Sun 6:00 PM–Thu 4:45 PM EST, closed all Friday/Saturday.** Shared across all three. Positions left open are auto-closed by Lucid anyway, but a strategy that doesn't flatten itself is relying on the firm's safety net instead of its own — close that gap. Remember Quantower runs on the local machine clock (Windows 11), so convert to US Eastern explicitly (`TimeZoneInfo.ConvertTimeFromUtc(..., "Eastern Standard Time")`) rather than assuming the PC's timezone, and don't forget Thursday's session doesn't reopen in the evening the way Mon/Tue/Wed's does.
5. **Flex-only funded-phase scaling plan.** Allowed contract size scales up with simulated profit and updates end-of-day, not in real time — Pro and Direct trade full size immediately with no scaling, so don't carry Flex's scaling-tier logic into a Pro/Direct strategy by habit.
6. **No hedging** (same account, and across correlated instruments/multiple accounts) and **no microscalping** (firm flags accounts where >50% of profit comes from trades held ≤5 seconds) — both shared across all three. The hedge check is mechanically enforceable (block an opposing-side order while a position is open in the same symbol); microscalping is a pattern to flag and let Pranay judge, not something to silently auto-block, since legitimate fast scalps exist.

`assets/LucidRiskGuard.cs` packages 1–6 into one reusable, account-type-aware class — with static factory methods (`ForFlexEvaluation`, `ForFlexFunded`, `ForProEvaluation`, `ForProFunded`, `ForDirect`) — so a new trading strategy wraps it instead of re-deriving this logic per account type. Wire it in roughly like:

```csharp
private LucidRiskGuard guard;

protected override void OnRun()
{
    // Example: $50K LucidFlex evaluation. Swap the factory call for whichever
    // account type/phase actually applies, with numbers confirmed off the dashboard.
    this.guard = LucidRiskGuard.ForFlexEvaluation(initialBalance: 50000, initialMaxLossLimit: 48000);
    // ... subscribe to quotes etc.
}

private void OnSignal(Side side)
{
    DateTime nowUtc = Core.Instance.TimeUtils.DateTimeUtcNow;
    if (!this.guard.CanTrade(nowUtc, out string reason))
    {
        Log($"Entry blocked: {reason}", StrategyLoggingLevel.Trading);
        return;
    }
    // place order as normal
}
```

Don't treat the guard file as gospel to copy verbatim every time — read it, understand what each check is doing and why, and adapt the factory call/constructor to whatever the current strategy actually needs (e.g. a Direct strategy never needs the scaling-tier concept; a Flex strategy never needs the DLL fields at all). The point is reusing the *pattern*, not stamping out an identical file five times.

## Workflow for a request

**Writing a new strategy for Lucid Flex:**
1. Get the trading logic/signal clear first (entries, exits, stop/target) — that's ordinary Quantower Strategy code per `references/quantower-api.md`.
2. Confirm which account type (Flex/Pro/Direct), phase (evaluation vs funded), and account size — since MLL, DLL presence, consistency cap/phase, and scaling all key off that combination — ask if it's not stated rather than guessing.
3. Wire in the risk guard pattern from `assets/LucidRiskGuard.cs` (pick the right factory method for the account type/phase), parameterized via Input Parameters, not hardcoded.
4. Surface the live compliance state in `OnGetMetrics()` (MLL headroom, today's P&L, consistency % if in eval).
5. Remind Pranay to run it in Quantower's Strategy Tester (Backtest & Optimize panel) against historical data before any live/funded run, and to double check the snapshot numbers against his dashboard if real money exposure is on the line.

**Auditing existing code:** read it the same way a Quantower compiler + a Lucid risk officer would — check the lifecycle methods are used correctly, check every Lucid rule in the list above against what the code actually does (not just what comments claim it does), and call out anything hardcoded that should be an Input Parameter. Be specific about which line breaches which rule rather than giving a general "looks risky" verdict.

**Environment/setup questions** (installing the VS2022 extension, debugging, project templates): answer from `references/quantower-api.md`'s setup section; it points to Quantower's own install guide for the exact click-path since that UI changes between Quantower releases independently of the API.

## Disclaimers worth keeping in the response when relevant

This is C# code for a real account that can lose real (simulated, then real) money — not a backtest toy. When a request involves going live or sizing real risk, it's worth a brief reminder that Claude isn't a substitute for testing in the Strategy Tester first, and that Pranay should be the one deciding actual position size and risk tolerance, not the AI.