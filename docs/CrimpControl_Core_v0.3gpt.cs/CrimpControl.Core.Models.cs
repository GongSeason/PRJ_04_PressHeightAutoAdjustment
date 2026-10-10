
using System;

namespace CrimpControl.Core.Models
{
    public enum CrimpState
    {
        WarmUp,
        Stable,
        Drift,
        Outlier,
        Diverged
    }

    public enum ControlMode
    {
        KOnly,
        EWMAOnly
    }

    public class CrimpMeasurement
    {
        public DateTime Timestamp { get; set; }

        public double Product1 { get; set; }
        public double Product2 { get; set; }
        public double Product3 { get; set; }
        public double Product4 { get; set; }

        public bool IsValid { get; set; }

        public double D => Product3 - Product4;
    }

    public class CrimpConfig
    {
        public double ToleranceT { get; set; } = 0.01;
        public double K { get; set; } = 0.5;
        public double DegradedGainFactor { get; set; } = 0.5;
        public int MinSamplesPerEpoch { get; set; } = 100;
        public int DriftConfirmCycles { get; set; } = 3;
        public double DriftNoiseFactor { get; set; } = 2.5;
    }

    public class ControlResult
    {
        public CrimpState State { get; set; }
        public double Adjustment { get; set; }
        public string Message { get; set; }
    }
}
