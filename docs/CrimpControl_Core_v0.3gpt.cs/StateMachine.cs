
using CrimpControl.Core.Models;

namespace CrimpControl.Core.StateMachine
{
    public class StateMachine
    {
        public CrimpState Evaluate(int validSamples, int minSamples, double deviation, double T, bool drift, bool outlier)
        {
            if (outlier) return CrimpState.Outlier;

            if (validSamples < minSamples) return CrimpState.WarmUp;

            if (System.Math.Abs(deviation) <= T && !drift) return CrimpState.Stable;

            if (drift) return CrimpState.Drift;

            return CrimpState.Stable;
        }
    }
}
