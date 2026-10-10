
using CrimpControl.Core.Models;
using CrimpControl.Core.Engine;
using CrimpControl.Core.StateMachine;
using CrimpControl.Core.Analysis;

namespace CrimpControl.Core
{
    public class CrimpController
    {
        private readonly ArbitrationEngine _arbiter;
        private readonly StateMachine _stateMachine;
        private readonly DivergenceAnalyzer _divergence;

        public CrimpController(CrimpConfig cfg)
        {
            _arbiter = new ArbitrationEngine(cfg);
            _stateMachine = new StateMachine();
            _divergence = new DivergenceAnalyzer();
        }

        public ControlResult Step(int validSamples, double d, double T, bool drift, bool outlier)
        {
            bool warmup = validSamples < 100;
            bool degraded = _divergence.IsDegraded(d, T);
            bool diverged = _divergence.IsStop(d, T);

            var state = _stateMachine.Evaluate(validSamples, 100, d, T, drift, outlier);

            return _arbiter.Decide(outlier, warmup, diverged, drift, d, T, degraded);
        }
    }
}
