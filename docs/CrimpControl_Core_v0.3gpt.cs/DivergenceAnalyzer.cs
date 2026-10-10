
using System;

namespace CrimpControl.Core.Analysis
{
    public class DivergenceAnalyzer
    {
        public bool IsDegraded(double d, double T)
        {
            return Math.Abs(d) >= T && Math.Abs(d) <= 2 * T;
        }

        public bool IsStop(double d, double T)
        {
            return Math.Abs(d) > 2 * T;
        }
    }
}
