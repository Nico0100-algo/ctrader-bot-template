cTrader cBot Template
A production-shaped starting point for cTrader Automate bots.
The strategy in this template has no edge, and that is deliberate. It is an EMA crossover — the most published, most arbitraged entry rule in existence. It is here so the file compiles and runs.
What this repository is actually about is the infrastructure around the strategy: the parts that determine whether a bot survives contact with a live account, and the parts that are almost always missing from example code.
Swap out `EvaluateSignal()` for your own logic. Leave everything else.
---
What it demonstrates
1. Bar-close evaluation
Entries are evaluated once per completed bar via `Bars.BarOpened`, reading index `1` — the bar that just closed. Index `0` is still forming and using it means acting on information that did not exist at the time.
`OnTick()` is deliberately empty. Tick-level entry logic is the most common source of backtest results that cannot be reproduced live.
2. Risk-based position sizing
Volume is derived from account balance, a risk percentage, and the actual stop distance in price terms:
```
units = (balance × risk%) / (stop distance in pips × pip value)
```
Never a fixed lot size. Volatility changes, account size changes, and fixed lots mean your real risk per trade drifts constantly without you noticing.
The result is normalised against `VolumeInUnitsMin` and `VolumeInUnitsMax`, and the trade is rejected outright — with a log line explaining why — if the required size falls below the symbol minimum.
3. Protective stops that are verified, not assumed
Two things most examples get wrong:
The stop is recalculated from the actual fill price, not the pre-order estimate. Slippage between the two is real, and sizing a stop off the estimate means your risk is not what you think it is.
The stop is confirmed, and retried if it fails. `ModifyPosition` can be rejected — during session gaps, at market reopen, in fast conditions. If it fails, the bot sets a flag and a timer retries every N seconds until the stop is attached. A filled position with no stop on it is the single most expensive bug in automated trading, and it happens quietly.
4. Restart recovery
If the bot is restarted while a position is open, `OnTimer` detects a position with no stop attached and reconstructs one from current ATR. Without this, a restart during a trade leaves an unprotected position indefinitely.
5. Label scoping
Every position is opened with a label and retrieved with `Positions.Find(label, symbol)`. The bot will never modify or close a position it did not open — whether that belongs to you, or to another bot running on the same account.
If you run a portfolio of bots on one account, this is not optional.
6. Logging that is actually useful
Every decision produces a line, including the ones where nothing happened:
```
ENTRY | Buy EURUSD @ 1.08432 | vol 15000 | ATR 0.00412 | stop 1.07608
PROTECTED | id 88214 | SL 1.07608 | TP 1.10080
REJECTED | calculated volume 0 below minimum — no trade
PROTECTION FAILED | id 88215 | MarketClosed | will retry every 15s
TRAIL | id 88214 | SL 1.07608 -> 1.07940
```
Rejections matter more than fills when you are diagnosing why live behaviour diverged from a backtest.
7. Configuration validation
Impossible parameter combinations are caught in `OnStart` and the bot stops with an explanatory message, rather than running and producing nothing while you wonder why.
---
Parameters
Parameter	Default	Notes
Fast EMA	20	Must be shorter than Slow EMA
Slow EMA	50	
ATR Period	14	Drives both stop distance and sizing
Stop Loss (ATR multiple)	2.0	Volatility-scaled, not fixed pips
Take Profit (ATR multiple)	4.0	Set to 0 to disable
Risk Per Trade (%)	1.0	Percentage of balance at risk if the stop is hit
Trail Stop	true	Trails at the same ATR multiple, favourable direction only
Bot Label	CBotTemplate	Change this if running multiple instances
Stop Verify Retry (seconds)	15	Retry interval for rejected stop attachment
---
Installation
Open cTrader → Automate
Create a new cBot and replace the contents with `CBotTemplate.cs`
Build
Attach to a chart, set parameters, run on demo first
---
Backtesting note
If you test this, use tick data from server, not the default bar data.
The difference is not cosmetic. A strategy I tested on bar data returned over 28,000%. The same strategy on tick data returned −9%. The gap was entirely down to intrabar fill assumptions the bar data could not see.
Bar-data backtests are a screening tool at best. They are not evidence.
---
Known limitations
Single position per symbol per instance by design
No news or economic calendar awareness
No spread filter — worth adding for lower-timeframe work
Trailing stop moves on bar close only, not intrabar
Pip-value sizing assumes the symbol reports `PipValue` correctly; verify on exotic instruments
Not tested against every broker's stop-level restrictions
The retry path for rejected stop attachment did not fire during testing — the Strategy Tester does not simulate broker rejections, so that branch is unproven in practice.
---
Licence
MIT. Use it, modify it, ship it.
---
Built by a developer who runs automated strategies on live capital. If you want a strategy implemented or independently tested before you automate it, get in touch.
