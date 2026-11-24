# Developer Handoff 复核（第 2 轮）

- 日期：2026-11-24
- 复核者：Claude Sonnet5.5
- 状态：**PLAN STATUS: REVIEW REQUIRED**
- 本轮读取：`docs/handoff/HANDOFF_latest.md`、`docs/review/2026-11-24_decision.md`、`docs/PLAN.md`、`docs/ACCEPTANCE.md` 和目标项目当前工作树代码。
- 约束：只读代码复核；不改计划，不改代码。

## 1. 先前阶段待落盘文件

前序要求的分析、计划、AC、Handoff、Opus 裁决文档均已存在，本轮无需补写。日期相同的第一份 review `docs/review/2026-11-24_plan-review.md` 已存在，因此本文件按规则命名为 `_2`。

## 2. 代码与依赖核对摘要

- **现有调用/API：** `MainViewModel.RefreshDataAsync`、`SetupTimer`、`ResetAndRefreshAsync`、`SwitchRecipeAsync`、`PollRecipeAsync`、`WriteLogToFile`、`Cleanup`、`CsvDataProvider.GetLatestDataAsync`、`GetLatestCsvFile`、`FindCsvInDirectory`、`GetRealFileSize` 均存在。定时器、自动启动、手动重置和配方切换的刷新调用关系与最新 Handoff 描述一致。
- **构造调用点：** 当前 `CsvDataProvider` 构造函数为 `(string basePath, int scanDays = 0)`。`MainViewModel` 当前有三处创建 provider：初始化、配方轮询未命中时回退默认路径、配方切换。S9 将新配置参数改为可选参数，并更新这三处，代码范围完整。
- **S6 数据依赖：** `PrepareChartData` 会调用 `ComputeRobustTrendRange`；后者也读取 `Settings.Controller`。新 Handoff 已要求将 Controller 参数快照传入两者，此要求与实际调用图吻合。
- **S11 命令/API：** `RefreshDataAsync` 带 `[RelayCommand]`，会生成 `RefreshDataCommand`；Handoff 保留命令兼容入口并建议提取 `RefreshCoreAsync(bool manual)`，方向正确。当前没有发现 XAML 直接绑定 `RefreshDataCommand`，但保留它能避免破坏生成 API/其他调用者。
- **S3/S5 日志：** 当前 `WriteLogToFile` 是同步追加文件且失败不影响主流程；`Cleanup` 由 Window Closing 与 App.OnExit 两处调用。S3 队列仅给 perf 日志使用、S5 才接入常规日志，可让构建 A 的常规日志路径维持现状；`LogEntry(FilePath, Line)` 足以区分日志目标。
- **S8/依赖：** 项目目标 `net8.0-windows`，System.Threading.Channels 和 System.Diagnostics.Process 均可用，不需新增包。后台 I/O 的 Win32 P/Invoke 尚不存在，但可按计划添加到 CsvDataProvider；必须只在 Task.Run 的同一线程 BEGIN/END。
- **S13/配置：** `SettingsService.Load`、`AppSettings` 存在，新增 Performance 属性及旧 JSON 的 `??= new()` 有现成接入点。当前 `ValidateSettings` 只限制 `RefreshIntervalSeconds >= 1`，没有上限。此处触发了 R8。
- **验收可测性：** AC-D01 的仓库外 harness 可调用 public CsvDataProvider API；AC-D02 现在降为两个计算函数的受限差异审查及 UI 截图，适合现有 private ChartData 结构。AC-R02 已与 Timer.Stop 不取消在途心跳的实际行为对齐。AC-N01 明确只统计通过守卫的刷新。未发现自动化测试工程；本次未运行构建，基线编译仍未验证。

## 3. 新发现的设计级问题（请 Claude Opus5.5 裁决）

### R8 — S9 的 60 秒承诺未覆盖刷新间隔大于 60 秒

- **原计划：** Opus 裁决将重扫门槛定义为 `max(0, NewFileMaxLatencySeconds - RefreshIntervalSeconds)`，并要求新文件出现到 `LastUsedFilePath` 切换 ≤60s。Handoff 说明刷新间隔 ≥60s 时 S9 每次刷新都扫描、没有省扫描次数；生产刷新间隔仍待确认。
- **代码证据：** `SetupTimer` 的刷新周期来自 `Settings.RefreshIntervalSeconds`。`ValidateSettings` 当前只拒绝 `<1`，没有最大值约束。刷新仅在 `DispatcherTimer.Tick` 调用 `RefreshDataAsync` 时检查/定位 CSV；provider 不会自行启动另一个目录扫描定时器。
- **冲突与影响：** 当刷新间隔 >60s 时，阈值计算为 0 只能保证“每次刷新都重新扫描”，不能保证新文件在 60s 内被发现；最坏要等一个完整刷新周期。当前算法、配置验证和验收承诺彼此不一致。产线刷新间隔待确认，可能直接落入此区间。
- **需 Claude Opus5.5 决定什么：** 选择并在计划/AC/Handoff 统一：
  1. 将 60s 作为硬上限，并规定/校验 `RefreshIntervalSeconds <= NewFileMaxLatencySeconds`；若产线现有刷新间隔超过 60s，S9 不满足 AC，需用户确认是否允许缩短刷新周期；或
  2. 保留产线刷新间隔，明确用户允许的新文件拾取上限至少为 `max(60s, RefreshIntervalSeconds)`；或
  3. 为 S9 增加独立于刷新周期的发现机制（会改变当前设计及磁盘访问频度，需另行评估）。

  在刷新间隔得到确认之前，可以继续构建 A 诊断；但 S9 的 60s AC 在该问题裁决前无法判为可实施/可通过。

### R9 — 备选“到达间隔抖动”不能替代端到端延迟验收

- **原计划：** 若 PLC 只能记录相机信号的到达时刻，AC-N03 可用“相邻两次到达间隔相对节拍周期的偏差”作为指标 L 的备选；AC-N03 的最终阈值仍描述为 p99 相对停止本软件的基线增加 ≤20ms。
- **代码/需求证据：** 当前应用没有相机信号的源端时间戳/事件号 API。用户原始表象是 PLC 收到相机信号的时间变长约 200ms；R3 主指标“PLC 发触发 → 收到相机结果”目前只是 Opus 推测，尚待现场确认。即使 PLC 可以给每个信号的**接收时刻**打时间戳，相邻到达间隔也只测周期抖动，不测单个信号从生成到收到的绝对/相对延迟。
- **冲突与影响：** 如果每个信号都固定迟到 200ms，到达间隔仍可能恰好等于名义周期；备选指标会判为正常，却漏掉用户关注的接收延迟。并且当前 AC 对这个不同量纲/定义的指标仍套用同一个“p99 增量 ≤20ms”，没有具体被测量、计算公式或另行批准的阈值。
- **需 Claude Opus5.5 决定什么：** 是否要求现场必须取得“事件源/触发时刻 + PLC 收到时刻”以计算端到端间隔，未取得前将 AC-N03 标为 **BLOCKED / 不可验收**；或改成相机源端可提供的序号/时间标记。若接受到达间隔抖动，只能将其作为 H1/H2 的辅助诊断，不应替代 AC-N03 的端到端延迟目标；若要把它作为验收指标，需用户重新确认定义、阈值及其与“约 200ms 延迟”的关系。

## 4. 结论

最新 Handoff 的文件/函数/API、调用关系、框架依赖和大部分 AC 均已与实际代码核对；构建 A（S0～S3）范围可开工，且当前代码没有既有实现需要 KEEP/ROLLBACK。**但 R8 使 S9 的 60s AC 取决于未确认的产线刷新间隔；R9 使端到端 AC-N03 在 PLC 不记录两个端点时不可由备选抖动指标证明。**这两项需要 Opus 明确计划边界后，才能将整体状态判为 READY。

本轮没有修改 PLAN、AC、Handoff 或代码；只新增本复核文件。

**PLAN STATUS: REVIEW REQUIRED**
