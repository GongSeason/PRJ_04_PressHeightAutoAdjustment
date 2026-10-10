
using System;
using CrimpControl.Core.Models;

namespace CrimpControl.Core.Engine
{
    public class ArbitrationEngine
    {
        private readonly CrimpConfig _cfg;

        public ArbitrationEngine(CrimpConfig cfg)
        {
            _cfg = cfg;
        }

        public ControlResult Decide(
            bool outlier,
            bool warmup,
            bool diverged,
            bool drift,
            double deviationD,
            double T,
            bool degraded)
        {
            if (outlier)
                return new ControlResult { State = CrimpState.Outlier, Adjustment = 0, Message = "Outlier STOP" };

            if (warmup)
                return new ControlResult { State = CrimpState.WarmUp, Adjustment = 0, Message = "WarmUp" };

            if (Math.Abs(deviationD) > 2 * T || diverged)
                return new ControlResult { State = CrimpState.Diverged, Adjustment = 0, Message = "Diverged STOP" };

            if (drift)
                return new ControlResult
                {
                    State = CrimpState.Drift,
                    Adjustment = -(_cfg.K * (degraded ? _cfg.DegradedGainFactor : 1.0) * deviationD),
                    Message = "Drift adjust"
                };

            return new ControlResult { State = CrimpState.Stable, Adjustment = 0, Message = "Stable" };
        }
    }
}
