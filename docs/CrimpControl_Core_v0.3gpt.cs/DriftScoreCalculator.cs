
using System;
using System.Collections.Generic;
using System.Linq;

namespace CrimpControl.Core.Analysis
{
    public class DriftScoreCalculator
    {
        public double ComputeSlope(IReadOnlyList<double> window)
        {
            if (window.Count < 2) return 0;

            double n = window.Count;
            double xMean = (n - 1) / 2.0;
            double yMean = window.Average();

            double num = 0;
            double den = 0;

            for (int i = 0; i < n; i++)
            {
                num += (i - xMean) * (window[i] - yMean);
                den += (i - xMean) * (i - xMean);
            }

            return den == 0 ? 0 : num / den;
        }
    }
}
