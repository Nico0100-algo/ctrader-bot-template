using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Robots
{
    /// <summary>
    /// Production cBot template.
    ///
    /// The strategy here (EMA cross, ATR stop) is a PLACEHOLDER and has no edge.
    /// The point of this file is the infrastructure around it:
    ///
    ///   1. Bar-close evaluation, not tick-by-tick
    ///   2. Position sizing derived from risk % and stop distance
    ///   3. Hard protective stops verified after fill, with retry
    ///   4. Timer-based recovery for session gaps and rejected modifications
    ///   5. Label scoping so the bot only ever touches its own positions
    ///   6. Structured logging of every decision, including rejections
    ///
    /// Replace EvaluateSignal() with your own logic. Everything else stays.
    /// </summary>
    [Robot(AccessRights = AccessRights.None, AddIndicators = true)]
    public class CBotTemplate : Robot
    {
        #region Parameters

        [Parameter("Fast EMA", DefaultValue = 20, MinValue = 2, Group = "Strategy")]
        public int FastPeriod { get; set; }

        [Parameter("Slow EMA", DefaultValue = 50, MinValue = 3, Group = "Strategy")]
        public int SlowPeriod { get; set; }

        [Parameter("ATR Period", DefaultValue = 14, MinValue = 2, Group = "Risk")]
        public int AtrPeriod { get; set; }

        [Parameter("Stop Loss (ATR multiple)", DefaultValue = 2.0, MinValue = 0.1, Step = 0.1, Group = "Risk")]
        public double StopAtrMultiple { get; set; }

        [Parameter("Take Profit (ATR multiple)", DefaultValue = 4.0, MinValue = 0.0, Step = 0.1, Group = "Risk")]
        public double TargetAtrMultiple { get; set; }

        [Parameter("Risk Per Trade (%)", DefaultValue = 1.0, MinValue = 0.01, MaxValue = 100.0, Step = 0.1, Group = "Risk")]
        public double RiskPercent { get; set; }

        [Parameter("Trail Stop", DefaultValue = true, Group = "Risk")]
        public bool UseTrailingStop { get; set; }

        [Parameter("Bot Label", DefaultValue = "CBotTemplate", Group = "Operations")]
        public string BotLabel { get; set; }

        [Parameter("Stop Verify Retry (seconds)", DefaultValue = 15, MinValue = 1, Group = "Operations")]
        public int RetrySeconds { get; set; }

        #endregion

        #region State

        private ExponentialMovingAverage _fastEma;
        private ExponentialMovingAverage _slowEma;
        private AverageTrueRange _atr;

        // Pending protective levels for a position whose stop has not yet been confirmed.
        private double _pendingStopPrice;
        private double _pendingTargetPrice;
        private bool _awaitingProtection;

        #endregion

        protected override void OnStart()
        {
            if (FastPeriod >= SlowPeriod)
            {
                Print("CONFIG ERROR: Fast EMA ({0}) must be shorter than Slow EMA ({1}). Stopping.",
                      FastPeriod, SlowPeriod);
                Stop();
                return;
            }

            _fastEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, FastPeriod);
            _slowEma = Indicators.ExponentialMovingAverage(Bars.ClosePrices, SlowPeriod);
            _atr = Indicators.AverageTrueRange(AtrPeriod, MovingAverageType.Exponential);

            // Bar-close driven. OnTick is used only for trailing, never for entries.
            Bars.BarOpened += OnBarOpened;

            // Exits are logged from the platform's own close event, so stop-outs and
            // target hits appear in the log alongside entries.
            Positions.Closed += OnPositionClosed;

            // Recovery timer: re-attempts protective stops that were rejected at fill time.
            Timer.Start(TimeSpan.FromSeconds(RetrySeconds));

            Print("STARTED | {0} | {1} {2} | Risk {3}% | Stop {4}xATR | Balance {5:F2}",
                  BotLabel, SymbolName, TimeFrame, RiskPercent, StopAtrMultiple, Account.Balance);
        }

        protected override void OnStop()
        {
            Bars.BarOpened -= OnBarOpened;
            Positions.Closed -= OnPositionClosed;
            Timer.Stop();
            Print("STOPPED | {0} | open positions left untouched", BotLabel);
        }

        /// <summary>
        /// Logs every exit with its reason. Without this, the log shows entries
        /// but no closes, and a trade cannot be reconstructed from it.
        /// </summary>
        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var position = args.Position;

            if (position.Label != BotLabel || position.SymbolName != SymbolName)
                return;

            _awaitingProtection = false;

            Print("EXIT | id {0} | {1} | reason {2} | entry {3:F5} | {4:F1} pips | net {5:F2} | balance {6:F2}",
                  position.Id, position.TradeType, args.Reason,
                  position.EntryPrice, position.Pips,
                  position.NetProfit, Account.Balance);
        }

        /// <summary>
        /// Fires once per completed bar. Index 1 is the bar that just closed;
        /// index 0 is the bar now forming and must never be used for signals.
        /// </summary>
        private void OnBarOpened(BarOpenedEventArgs args)
        {
            if (Bars.Count < Math.Max(SlowPeriod, AtrPeriod) + 2)
                return;

            var position = Positions.Find(BotLabel, SymbolName);

            if (position != null)
            {
                ManageOpenPosition(position);
                return;
            }

            var signal = EvaluateSignal();
            if (signal.HasValue)
                OpenPosition(signal.Value);
        }

        /// <summary>
        /// PLACEHOLDER STRATEGY. Replace this method with your own rules.
        /// Returns null when no trade is warranted.
        /// </summary>
        private TradeType? EvaluateSignal()
        {
            double fastNow = _fastEma.Result.Last(1);
            double slowNow = _slowEma.Result.Last(1);
            double fastPrev = _fastEma.Result.Last(2);
            double slowPrev = _slowEma.Result.Last(2);

            bool crossedUp = fastPrev <= slowPrev && fastNow > slowNow;
            bool crossedDown = fastPrev >= slowPrev && fastNow < slowNow;

            if (crossedUp) return TradeType.Buy;
            if (crossedDown) return TradeType.Sell;
            return null;
        }

        private void OpenPosition(TradeType tradeType)
        {
            double atr = _atr.Result.Last(1);

            if (atr <= 0 || double.IsNaN(atr))
            {
                Print("REJECTED | invalid ATR ({0}) — no trade", atr);
                return;
            }

            double stopDistance = atr * StopAtrMultiple;
            double volume = CalculateVolume(stopDistance);

            if (volume <= 0)
            {
                Print("REJECTED | calculated volume {0} below minimum — no trade", volume);
                return;
            }

            double entryEstimate = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            _pendingStopPrice = tradeType == TradeType.Buy
                ? entryEstimate - stopDistance
                : entryEstimate + stopDistance;

            _pendingTargetPrice = TargetAtrMultiple > 0
                ? (tradeType == TradeType.Buy
                    ? entryEstimate + atr * TargetAtrMultiple
                    : entryEstimate - atr * TargetAtrMultiple)
                : 0;

            var result = ExecuteMarketOrder(tradeType, SymbolName, volume, BotLabel);

            if (!result.IsSuccessful)
            {
                Print("ENTRY FAILED | {0} | {1} vol {2} | {3}",
                      BotLabel, tradeType, volume, result.Error);
                _awaitingProtection = false;
                return;
            }

            Print("ENTRY | {0} {1} @ {2:F5} | vol {3} | ATR {4:F5} | stop {5:F5}",
                  tradeType, SymbolName, result.Position.EntryPrice, volume, atr, _pendingStopPrice);

            // Recompute protection from the ACTUAL fill, not the estimate. Slippage matters.
            _pendingStopPrice = tradeType == TradeType.Buy
                ? result.Position.EntryPrice - stopDistance
                : result.Position.EntryPrice + stopDistance;

            if (TargetAtrMultiple > 0)
            {
                _pendingTargetPrice = tradeType == TradeType.Buy
                    ? result.Position.EntryPrice + atr * TargetAtrMultiple
                    : result.Position.EntryPrice - atr * TargetAtrMultiple;
            }

            ApplyProtection(result.Position);
        }

        /// <summary>
        /// Position size from account risk and stop distance.
        /// Never a fixed lot size — the whole point is that risk stays constant
        /// as volatility and account size change.
        /// </summary>
        private double CalculateVolume(double stopDistancePrice)
        {
            double riskAmount = Account.Balance * (RiskPercent / 100.0);
            double stopPips = stopDistancePrice / Symbol.PipSize;

            if (stopPips <= 0)
                return 0;

            double valuePerPipPerUnit = Symbol.PipValue;
            if (valuePerPipPerUnit <= 0)
            {
                Print("REJECTED | PipValue unavailable for {0}", SymbolName);
                return 0;
            }

            double units = riskAmount / (stopPips * valuePerPipPerUnit);
            double normalised = Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down);

            if (normalised < Symbol.VolumeInUnitsMin)
            {
                Print("SIZING | required {0:F2} units is below symbol minimum {1}",
                      units, Symbol.VolumeInUnitsMin);
                return 0;
            }

            if (normalised > Symbol.VolumeInUnitsMax)
                normalised = Symbol.VolumeInUnitsMax;

            return normalised;
        }

        /// <summary>
        /// Attaches the protective stop. If the broker rejects it — common during
        /// session gaps or immediately after reopen — the flag stays set and the
        /// timer retries until it sticks. A position without a stop is the single
        /// most expensive bug in automated trading.
        /// </summary>
        private void ApplyProtection(Position position)
        {
            double? target = _pendingTargetPrice > 0 ? _pendingTargetPrice : (double?)null;

            // ProtectionType.Absolute — the values passed are price levels, not distances.
            var result = ModifyPosition(position, _pendingStopPrice, target, ProtectionType.Absolute);

            if (result.IsSuccessful)
            {
                _awaitingProtection = false;
                Print("PROTECTED | id {0} | SL {1:F5} | TP {2}",
                      position.Id, _pendingStopPrice,
                      target.HasValue ? target.Value.ToString("F5") : "none");
            }
            else
            {
                _awaitingProtection = true;
                Print("PROTECTION FAILED | id {0} | {1} | will retry every {2}s",
                      position.Id, result.Error, RetrySeconds);
            }
        }

        /// <summary>
        /// Retry loop. Also catches the case where a position exists with no stop
        /// at all — for instance after the bot restarts mid-trade.
        /// </summary>
        protected override void OnTimer()
        {
            var position = Positions.Find(BotLabel, SymbolName);
            if (position == null)
            {
                _awaitingProtection = false;
                return;
            }

            if (_awaitingProtection || position.StopLoss == null)
            {
                if (position.StopLoss == null && !_awaitingProtection)
                {
                    // Restart recovery: rebuild a stop from current ATR.
                    double atr = _atr.Result.Last(1);
                    if (atr <= 0) return;

                    _pendingStopPrice = position.TradeType == TradeType.Buy
                        ? position.EntryPrice - atr * StopAtrMultiple
                        : position.EntryPrice + atr * StopAtrMultiple;

                    Print("RECOVERY | id {0} found without stop — reconstructing at {1:F5}",
                          position.Id, _pendingStopPrice);
                }

                ApplyProtection(position);
            }
        }

        private void ManageOpenPosition(Position position)
        {
            if (!UseTrailingStop || position.StopLoss == null)
                return;

            double atr = _atr.Result.Last(1);
            if (atr <= 0) return;

            double trailDistance = atr * StopAtrMultiple;

            double candidate = position.TradeType == TradeType.Buy
                ? Bars.ClosePrices.Last(1) - trailDistance
                : Bars.ClosePrices.Last(1) + trailDistance;

            // Stops only ever move in the favourable direction.
            bool shouldMove = position.TradeType == TradeType.Buy
                ? candidate > position.StopLoss.Value
                : candidate < position.StopLoss.Value;

            if (!shouldMove) return;

            // Capture before modifying — the Position object updates in place,
            // so reading StopLoss after the call returns the new value, not the old.
            double previousStop = position.StopLoss.Value;

            var result = ModifyPosition(position, candidate, position.TakeProfit, ProtectionType.Absolute);

            if (result.IsSuccessful)
                Print("TRAIL | id {0} | SL {1:F5} -> {2:F5}",
                      position.Id, previousStop, candidate);
            else
                Print("TRAIL FAILED | id {0} | {1}", position.Id, result.Error);
        }

        protected override void OnTick()
        {
            // Deliberately empty.
            //
            // Entries are evaluated on bar close only. Tick-level entry logic is the
            // most common cause of backtest results that cannot be reproduced live,
            // because bar-open data in a backtester does not reflect what was
            // actually knowable at that moment.
        }
    }
}
