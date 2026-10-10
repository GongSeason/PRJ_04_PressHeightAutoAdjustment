# 代码评审与修复记录 — 2026-10-09

> 范围：`PRJ_04_PressHeightAutoAdjustment`（压接高度自动调整 / 铆头压接预警系统）
> 评审重点：能否真正反馈铆头压接预警（告警可见性、安全护栏、PLC 下发、失活检测）
> 验证：`CrimpHeight.Core` 与 `CrimpHeightAnalyzer`(WPF) 均 `dotnet build` 成功，0 error。

---

## 一、已完成修复

### 1. [高危] Drift 调整类预警不会在 UI 上报警（漏报）

- **文件**：`CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`
- **问题**：告警收集仅纳入 `HeadState.Outlier` 和 `DivergenceLevel.Stop`。当压头进入 `Drift`（偏差越界或漂移确认、需下发调整）时，`HasAlert` 不置位，界面告警栏保持空白，操作员看不到压接偏移预警。
- **修复**：新增 `ShouldAlert(HeadDecision)` 辅助方法，告警收集改为纳入 `Outlier`、`Drift`（含 Degraded）、`Stop` 三类。
- **代码位置**：`RefreshDataAsync` 内告警收集段；新增 `ShouldAlert` 方法（位于 `UpdateAlerts` 上方）。

### 2. [中危] σ 基线取历史最小值，σ 突增预警几乎永不触发

- **文件**：`CrimpHeight.Core/Safety/SafetyGuard.cs`
- **问题**：`UpdateBaselineSigma` 仅在 `sigma < _baselineSigma` 时更新，基线被锁死在历史最小 σ 上。导致正常波动被判 Outlier（误报），或真实突增难触发（漏报），工序能力慢性退化捕捉不到。
- **修复**：改为最近 `BaselineWindow = 20` 个稳态 σ 的**滑动均值**作基线（`Queue<double>` + 累加和）。`ResetBaseline` 同步清空窗口与累加和。`UpdateBaselineSigma` 增加非有限/负值过滤。
- **可调参数**：`BaselineWindow`（当前常量 20），如需可提升为 `ControllerConfig` 配置项。

### 3. [低危] PLC 写入失败被误记为"已下发"，同一指令不再重试

- **文件**：
  - `CrimpHeight.Core/Interfaces/IPlcAdapter.cs`
  - `CrimpHeightAnalyzer/Services/KeyencePlcAdapter.cs`
  - `CrimpHeightAnalyzer/Services/MockPlcAdapter.cs`
  - `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`
- **问题**：`SendAdjustmentAsync` 写失败时不抛异常，`SendPlcCommandsAsync` 在 `await` 后无条件把 key 记入 `_issuedPlcCommandKeys`，导致写失败的调整量被当作已发，不再重试。
- **修复**：
  - `IPlcAdapter.SendAdjustmentAsync` / `SendStopAsync` 返回类型由 `Task` 改为 `Task<bool>`（true=写入成功）。
  - 两个适配器实现相应返回成功/失败。
  - `MainViewModel.SendPlcCommandsAsync` 与 `SendStopOnceAsync` 改为**仅在返回 true 时**才记入 `_issuedPlcCommandKeys`，失败时下一拍可重发。

### 4. [低危] 数据失活计时被历史时间戳污染，可能首刷即误停机

- **文件**：`CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`
- **问题**：无数据时以 `_lastAnalyzedDataTimestamp`（上次数据时间）作为 `_dataUnavailableSince` 起点。若 CSV 末条时间戳很旧，`unavailableSeconds` 一上来即为巨大值，首次刷新直接触发停机，缺少缓冲。
- **修复**：失活计时起点改为"检测到无数据的墙钟时刻"（`DateTime.Now`）。

### 5. [次要] 规格限与安全限缺少交叉校验

- **文件**：`CrimpHeight.Core/Models/ControllerConfig.cs`
- **问题**：若配置使规格限 `[LowerLimit, UpperLimit]` 落在安全限 `[SafetyLowerBound, SafetyUpperBound]` 之外，合理性预警逻辑失效，`Validate()` 未拦截。
- **修复**：`Validate()` 增加校验 —— 安全范围必须覆盖规格范围，否则抛 `InvalidOperationException("安全范围必须覆盖压接高度规格范围 [下限, 上限]。")`。

---

## 二、待处理项（本次未改动，需后续 Agent 处理）

### A. [需产品确认 / 可能高危] 单产品模式下发散预警不可达

- **文件**：`CrimpHeight.Core/Controller/DecisionArbiter.cs`、`DivergenceGrader.cs`、`MainViewModel.cs`
- **现状**：实际数据流走 `ArbitrateSingle`（每压头单路高度），恒传 `DivergenceLevel.Normal`，UI 固定显示"单产品模式"。`DivergenceGrader` 仅被旧的双产品接口 `Arbitrate` 使用，当前不可达。
- **影响**：若"两产品压接发散"本应触发降级/停机预警，则该预警链路在当前建模下完全失效。
- **待办**：确认数据建模 —— 每个压头是否应承载双产品序列。若是，需让 UI/数据流改用 `Arbitrate` 或在 `ArbitrateSingle` 中引入对应发散判据。

### B. [中危] 漂移确认的"拍"语义与刷新周期耦合，灵敏度易被误解

- **文件**：`CrimpHeight.Core/Controller/DriftDetector.cs`、`ControllerConfig.DriftConfirmCycles`
- **现状**：每个刷新周期对整段窗口只计算一个 slope，`DriftConfirmCycles=3` 实际需约 3 个刷新周期（默认 10s/次 ≈ 30s）才能确认漂移。注释称"连续 N 拍"，但"拍"实为"刷新周期"，与单次压接不符。
- **待办**：明确文档/配置项命名中"拍 = 刷新周期"的语义；或改为按真实压接样本序列推进漂移窗口。

### C. [低危] σ 基线窗口大小硬编码

- **文件**：`CrimpHeight.Core/Safety/SafetyGuard.cs`（`BaselineWindow = 20`）
- **待办**：如现场需要调参，建议提升为 `ControllerConfig` 配置项并纳入 `Validate()`。

### D. [文档] README 仍为 Gitee 模板

- **文件**：`README.md`
- **待办**：替换为实际项目说明（架构、安装、使用）。`README.en.md` 一并检查。

---

## 三、构建验证

| 工程 | 结果 |
| --- | --- |
| `CrimpHeight.Core` | 构建成功，0 Warning / 0 Error |
| `CrimpHeightAnalyzer` (WPF) | 构建成功，0 Error（9 个 NU1701 为既有第三方包兼容性警告，与本次改动无关） |

命令：
```
dotnet build CrimpHeight.Core/CrimpHeight.Core.csproj -c Debug
dotnet build CrimpHeightAnalyzer/CrimpHeightAnalyzer.csproj -c Debug
```

---

## 四、改动文件清单

- `CrimpHeight.Core/Safety/SafetyGuard.cs` — σ 基线改滑动均值（修复 2）
- `CrimpHeight.Core/Models/ControllerConfig.cs` — 规格/安全限交叉校验（修复 5）
- `CrimpHeight.Core/Interfaces/IPlcAdapter.cs` — 写入方法返回 `Task<bool>`（修复 3）
- `CrimpHeightAnalyzer/Services/KeyencePlcAdapter.cs` — 返回成功标志（修复 3）
- `CrimpHeightAnalyzer/Services/MockPlcAdapter.cs` — 返回成功标志（修复 3）
- `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs` — 告警纳入 Drift（1）、写成功才记 key（3）、失活墙钟计时（4）
