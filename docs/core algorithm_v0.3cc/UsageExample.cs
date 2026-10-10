using System;

namespace CrimpHeightControl
{
    /// <summary>
    /// PLC 抽象接口（沿用 v0.1 §9 / FR-PLC-01~03，协议 TBD）。
    /// 算法库本身不做 IO；由宿主实现本接口把仲裁决策落到设备。
    /// </summary>
    public interface IPlcAdapter
    {
        /// <summary>读当前设定（压头基准位置）。</summary>
        double ReadCurrentSetpoint(string headId);
        /// <summary>对设定施加增量 Δ（Adjust 时）。</summary>
        void ApplySetpointDelta(string headId, double delta);
        /// <summary>报警（Outlier / Diverged / Degraded / WarmUp 等）。</summary>
        void RaiseAlarm(string headId, string message);
        /// <summary>停止 / 停机（Outlier / Diverged）。</summary>
        void Halt(string headId, string reason);
    }

    /// <summary>仅供演示 / 离线回放的 Mock PLC：打印到控制台并在内存中累计设定。</summary>
    public sealed class MockPlcAdapter : IPlcAdapter
    {
        private double _setpoint;

        public MockPlcAdapter(double initialSetpoint)
        {
            _setpoint = initialSetpoint;
        }

        public double CurrentSetpoint { get { return _setpoint; } }

        public double ReadCurrentSetpoint(string headId) { return _setpoint; }

        public void ApplySetpointDelta(string headId, double delta)
        {
            _setpoint += delta;
            Console.WriteLine("    [PLC] " + headId + " 设定 " + delta.ToString("+0.0000;-0.0000") +
                              " → " + _setpoint.ToString("F4"));
        }

        public void RaiseAlarm(string headId, string message)
        {
            Console.WriteLine("    [报警] " + headId + "：" + message);
        }

        public void Halt(string headId, string reason)
        {
            Console.WriteLine("    [停机] " + headId + "：" + reason);
        }
    }

    /// <summary>
    /// 用法示例（无 Main：库本身为 netstandard2.0，无入口点）。
    /// 在你的宿主工程里调用 <see cref="RunDemo"/> 即可看到四类典型决策的走向，含 AC-20（mean≈0 但 |D| 大判 Diverged）。
    /// </summary>
    public static class UsageExample
    {
        public static void RunDemo()
        {
            ControlConfig cfg = new ControlConfig
            {
                Setpoint = 5.000,
                Tolerance = 0.02,
                Deadband = 0.005,
                MaxStepPerAdjust = 0.01,
                ControllerGain = 0.5,
                ProcessGain = 1.0,
                MinSamplesPerEpoch = 5,   // demo 用小值；现场按 §H 实测
                WindowSize = 20,
                MinReasonableHeight = 4.5,
                MaxReasonableHeight = 5.5
            };

            MockPlcAdapter plc = new MockPlcAdapter(cfg.Setpoint);
            CrimpHeadController head = new CrimpHeadController(cfg, productCount: 2) { HeadId = "HEAD-1" };

            DateTime t = DateTime.UtcNow;

            Console.WriteLine("=== 场景 A：换模后 WarmUp → 达样本闸 → Stable ===");
            head.ResetEpoch();
            for (int i = 0; i < 7; i++)
            {
                t = t.AddSeconds(0.2);
                // 两工位都贴近设定，仅微噪声
                double noise = (i % 2 == 0) ? 0.001 : -0.001;
                CrimpCycleInput input = new CrimpCycleInput(new double[] { 5.000 + noise, 5.000 - noise }, t);
                ControlDecision d = head.ProcessCycle(input);
                Console.WriteLine("  拍" + (i + 1) + " " + d);
                Route(plc, "HEAD-1", d);
            }

            Console.WriteLine();
            Console.WriteLine("=== 场景 B：共模偏移 +0.02（越死区）→ Adjust（Δ 限幅 -0.01）===");
            head.ResetEpoch();
            for (int i = 0; i < 6; i++)
            {
                t = t.AddSeconds(0.2);
                CrimpCycleInput input = new CrimpCycleInput(new double[] { 5.020, 5.020 }, t);
                ControlDecision d = head.ProcessCycle(input);
                Console.WriteLine("  拍" + (i + 1) + " " + d);
                Route(plc, "HEAD-1", d);
            }

            Console.WriteLine();
            Console.WriteLine("=== 场景 C：mean≈0 但 |D|=0.06=3T → Diverged（AC-20，非 Stable）===");
            head.ResetEpoch();
            for (int i = 0; i < 6; i++)
            {
                t = t.AddSeconds(0.2);
                // 均值居中（5.00），但两工位相差 0.06
                CrimpCycleInput input = new CrimpCycleInput(new double[] { 5.030, 4.970 }, t);
                ControlDecision d = head.ProcessCycle(input);
                Console.WriteLine("  拍" + (i + 1) + " " + d);
                Route(plc, "HEAD-1", d);
            }

            Console.WriteLine();
            Console.WriteLine("=== 场景 D：超合理性阈（6.0 > 5.5）→ Outlier（停机）===");
            t = t.AddSeconds(0.2);
            ControlDecision od = head.ProcessCycle(new CrimpCycleInput(new double[] { 6.000, 5.000 }, t));
            Console.WriteLine("  " + od);
            Route(plc, "HEAD-1", od);
        }

        /// <summary>把仲裁决策路由到 PLC（宿主侧典型写法）。</summary>
        public static void Route(IPlcAdapter plc, string headId, ControlDecision d)
        {
            switch (d.Action)
            {
                case ControlActionType.Adjust:
                    if (d.Adjustment.HasValue && d.Adjustment.Value != 0.0)
                        plc.ApplySetpointDelta(headId, d.Adjustment.Value);
                    if (d.IsDegraded)
                        plc.RaiseAlarm(headId, "Degraded 工位一致性下降。");
                    break;

                case ControlActionType.Halt:
                    plc.Halt(headId, d.Message);
                    plc.RaiseAlarm(headId, d.Message);
                    break;

                case ControlActionType.None:
                default:
                    // WarmUp / Stable：不动作（如需可在 UI 显示 d.Message）
                    break;
            }
        }
    }
}
