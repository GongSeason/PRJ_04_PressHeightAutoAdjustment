# 压接高度反馈控制系统 — C# 核心算法库（v1 最小安全核）

依据 **PRD v0.3** 实现的可复用 C# 类库。严格遵循 PRD **§K 范围纪律**：只实现 **v1 最小安全核**，把 v1.5+ 精化（EWMA、β 偏差校正、双窗口、σ-加权漂移）留作**干净的扩展点**而**不实现**。

> 范式：**离散 R2R（单阻尼）+ 四状态机 + 决策仲裁 + 单自由度发散分级 + 安全护栏**。

---

## 一、已实现（v1 最小安全核，对应 §K“必做”）

| 能力 | 类 | PRD 出处 |
|---|---|---|
| 决策仲裁（单遍命中即止，唯一 state/action） | `DecisionArbiter` | §A FR-ARB-01/02/03 |
| 控制律：死区阻尼（**仅 K**，单阻尼） | `DampedStepControlLaw` | §B FR-DAMP-01/03、§L FR-CTRL-04~06 |
| 四状态机（WarmUp/Stable/Drift/Outlier）+ Diverged 覆盖态 | `ControlState`、由仲裁产出 | §C FR-STATE-01/05、§A |
| 发散三级（normal/degraded/stop） | `DivergenceEvaluator` | §D FR-DIV-03/03a |
| DriftScore 抗噪（方向 + 幅度双条件，可退化为仅方向） | `DriftDetector` | §E FR-STATE-02/03/03a |
| 安全护栏（合理性阈 / σ 突增 / 数据失活） | `SafetyGuardrail` | §L FR-SAFETY-01~07 |
| `HardCut` epoch（β-无关）+ `MinSamplesPerEpoch` 闸 | `CrimpHeadController.ResetEpoch` | §F FR-CTRL-07b |
| 固定窗口 n + 截尾均值 / σ / 斜率 | `MeasurementWindow` | §G FR-WINDOW-01、§L FR-ANALYSIS |
| PLC 抽象 + Mock | `IPlcAdapter`、`MockPlcAdapter` | §L FR-PLC-01~03 |
| 单压头编排（采集→护栏→统计→发散→仲裁） | `CrimpHeadController` | 全局串联 |
| 参数与默认值 + 校验 | `ControlConfig` | §H / §J |

`ControlDecision` 携带 `State / Action / Adjustment / IsDegraded / Message` 及诊断量（`EstimatedDeviation / D / Sigma / SampleCount / Drift / Divergence`），可直接喂 UI / 告警 / 审计日志。

## 二、**未**实现（v1.5+ 精化，留扩展点，对应 §K“逐项加，均为替换非叠加”）

| 精化 | 现状（守门方式） |
|---|---|
| `EWMA`-only 模式（替换 K，不并存） | `EwmaControlLaw` 已留**带骨架的扩展点**，但默认禁用；`ControlConfig.Validate()` 在 `ControlMode=Ewma` 且未显式 `AllowEwmaMode=true` 时抛 `NotSupportedException`。**未经现场验证，勿用于 v1 产线。** |
| `BiasCorrectedDecay` epoch（绑 β 置信） | `EpochDataMode.BiasCorrectedDecay` 枚举占位；`Validate()` 直接拒绝。v1 不引入任何 β 依赖。 |
| 双窗口自适应 n（Fast/Stable） | 仅 `WindowSize` 固定窗口；双窗口参数列入 PRD 待定项，本库不实现。 |
| DriftScore σ-加权显著性 | 已实现为可开关的幅度门 `UseDriftMagnitudeGate`（默认开，满足 AC-23）。如需先走 §K 基础版“仅连续 N 拍同向”，置为 `false`。 |

> 这三处（β 依赖 / 双阻尼 / 自适应窗口）正是审核反复警示的不稳定来源，按 §K 推迟到有实测数据与现场稳定后再上。

---

## 三、关键实现假设（落码时需与现场口径核对）

1. **每压头固定 `productCount` 个工位**（默认 2 = P3/P4）。每拍须提供恰好 `productCount` 个测量高度，数量不符即判 `Outlier`（数据失活）。
2. **两套统计口径**：
   - *中心窗口*：每拍存“各工位均值”，其**截尾均值 − 设定 = 中心偏差**；σ、斜率（DriftScore）均取自中心窗口。
   - *各工位窗口*：每工位均值 − 设定 = 该工位偏差；`D = max(工位偏差) − min(工位偏差)`（极差），用于发散分级。
3. **死区归属仲裁层**：控制律是“纯阻尼步 + 限幅”，是否动作由仲裁用 `Deadband` 判定（故漂移可在死区内提前触发修正）。控制律类名 `DampedStepControlLaw`，但模式枚举仍叫 `DeadbandDamped`。
4. **过程增益 β = `ProcessGain`**：`Δ = K·(−偏差/β)`，再按 `MaxStepPerAdjust` 限幅。
5. **护栏分两段**：原始样本（空/NaN/超阈/间隔）在**入窗前**判（坏值不入窗）；σ 突增在**入窗后**判。
6. **数据间隔**自“上一收到的拍”计，每拍都更新基线。
7. **量纲**：默认按 mm（设定/偏差/容差/步长）与秒。`MinReasonableHeight/MaxReasonableHeight/MaxSigma/MaxSampleGapSeconds` 默认放空（±∞ / 0），**须实测后设**才会真正生效。
8. **发散基准 T**：默认沿用 `Tolerance`；如需独立标定可设 `DivergenceThreshold>0`。

---

## 四、如何调用

```csharp
using CrimpHeightControl;

var cfg = new ControlConfig
{
    Setpoint = 5.000,        // 目标压接高度
    Tolerance = 0.02,        // 容差 T（也作发散 1T/2T 基准）
    Deadband = 0.005,        // 死区半宽
    ControllerGain = 0.5,    // 阻尼 K
    ProcessGain = 1.0,       // 过程增益 β
    MaxStepPerAdjust = 0.01, // 单拍限幅
    MinSamplesPerEpoch = 30, // WarmUp 闸（现场按实测定）
    WindowSize = 250,        // 固定窗口 n（现场按实测定）
    // 安全护栏阈值务必按现场实测填入：
    MinReasonableHeight = 4.5,
    MaxReasonableHeight = 5.5,
    MaxSigma = 0.05,
    MaxSampleGapSeconds = 2.0
};

var head = new CrimpHeadController(cfg, productCount: 2) { HeadId = "HEAD-1" };
var plc  = new MockPlcAdapter(cfg.Setpoint); // 换成你的 IPlcAdapter 实现

// 换模 / 复标后：
head.ResetEpoch();

// 每拍：
ControlDecision d = head.ProcessCycle(
    new CrimpCycleInput(new double[] { p3Height, p4Height }, DateTime.UtcNow));

// 路由到 PLC（示例见 UsageExample.Route）：
switch (d.Action)
{
    case ControlActionType.Adjust: plc.ApplySetpointDelta("HEAD-1", d.Adjustment.Value); break;
    case ControlActionType.Halt:   plc.Halt("HEAD-1", d.Message); break;
    // None：WarmUp / Stable 不动作
}
```

跑内置演示（含 WarmUp→Stable、共模偏移→Adjust、AC-20 的 Diverged、超阈 Outlier）：

```csharp
UsageExample.RunDemo();
```

---

## 五、扩展指引（上 v1.5 时）

- **换 EWMA**：实现/启用 `EwmaControlLaw`，置 `ControlMode=Ewma`、`AllowEwmaMode=true`。注意 §B：EWMA 输出**不再乘 K**——本库已据此装配（仲裁对 EWMA 路径只传发散折减、不传 K）。**整体替换 K-only，禁双开。**
- **β 偏差校正**：去掉 `Validate()` 对 `BiasCorrectedDecay` 的拦截，并在控制器内实现 β 新鲜度判定与过期回落 HardCut。
- **双窗口**：在 `CrimpHeadController` 内维护 Fast/Stable 两套 `MeasurementWindow`，按扰动状态切换。
- 任何精化都建议先走 PRD 收尾建议：**①完成“锁压头采 σ/基线”定下真实 n 与阈值；②用回放仿真器验证 §A 仲裁与 AC-20~25**，再落到 PLC。

---

## 六、目标框架与构建

- `netstandard2.0` + `LangVersion 7.3`：兼容 .NET Framework 4.6.1+ 与 .NET 5/6/7/8+。
- 纯算法、无第三方依赖、无 IO（PLC 经 `IPlcAdapter` 注入）。
- 构建（在装有 .NET SDK 的环境）：`dotnet build -c Release`。

> 说明：本库在当前环境**未经编译验证**（无 .NET SDK），代码已按 C# 7.3 / netstandard2.0 约束逐文件人工审校。请在你的工程内 `dotnet build` 一次确认，并以回放数据跑通 §A / AC-20~25 后再接产线。
