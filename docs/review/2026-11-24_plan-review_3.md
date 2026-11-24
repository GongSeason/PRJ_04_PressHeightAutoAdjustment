# Developer Handoff 复核（第 3 轮）

- 日期：2026-11-24
- 复核者：Claude Sonnet5.5
- 状态：**PLAN STATUS: REVIEW REQUIRED**
- 本轮输入：最新 `docs/handoff/HANDOFF_latest.md`、`docs/review/2026-11-24_decision_2.md`、`docs/PLAN.md`、`docs/ACCEPTANCE.md` 和目标项目当前工作树代码。
- 约束：只读复核，不改方案或代码。

## 1. 先前阶段待落盘文件

前序要求的分析、PLAN、ACCEPTANCE、Handoff、两轮裁决及两轮复核文件都已经落盘，无待补写文件。已有 `docs/review/2026-11-24_plan-review.md` 和 `_2.md`，本次按日期重名规则使用 `_3.md`。

## 2. 代码核对摘要

- Handoff 中的现有入口和调用关系可在代码中找到：`MainViewModel.SetupTimer` 的 `DispatcherTimer.Tick` 调用 `RefreshDataAsync`；`ToggleRun` 启动后立即刷新；`ResetAndRefreshAsync` 调用刷新；`SwitchRecipeAsync` 仅在 `IsRunning` 时刷新。刷新被 `IsLoading` 防重入。
- `CsvDataProvider` 现有构造函数是 `(string basePath, int scanDays = 0)`，`GetLatestDataAsync`、`GetLatestCsvFile`、`FindCsvInDirectory` 和 `GetRealFileSize` 均存在。`MainViewModel` 的 provider 创建点共三处：`InitDataProvider`、`PollRecipeAsync` 的默认路径回退、`SwitchRecipeAsync`。Handoff 要求扩展可选构造参数并更新这三处，范围完整。
- S6 涉及的方法都存在；`PrepareChartData` 调用 `ComputeRobustTrendRange`，两者都读 `Settings.Controller`。Handoff 已要求二者都使用 UI 线程创建的快照。
- `RefreshDataAsync` 使用 `[RelayCommand]`，保留该方法可保持生成的 `RefreshDataCommand`；提取 `RefreshCoreAsync(bool manual)` 的提示合理。
- S3/S5 需要新增的 `Channel<LogEntry>`、`PerformanceSettings`、`LastReadMetrics`、`IsLoadingOverlayVisible`、Win32 线程后台模式 API 都是计划新增项；目标框架为 `net8.0-windows`，不需要额外 NuGet 包。现有 `WriteLogToFile` 为静态同步追加；`Cleanup` 从 Window Closing 和 App.OnExit 两处触发，队列幂等要求有代码依据。
- S9 新阈值公式对刷新间隔 ≤60s 可给出至多 60s 的周期重扫设计；刷新间隔 >60s 时每次刷新扫描只能做到不比现状更晚。`ValidateSettings` 目前只拒绝 `RefreshIntervalSeconds < 1`，没有上界。因此 R8 裁决与代码调度模型相符，但其用户需求含义需再核实（R10）。
- 没有发现自动化测试工程。仓库外 harness 调用 public `CsvDataProvider.GetLatestDataAsync` 可行；`PrepareChartData` 和 `ChartData` 是 private，所以已将 AC-D02 收窄为代码差异审查加 UI 对比。
- **未运行构建**：这是实施前计划复核，基线编译状态仍未验证。

## 3. 需要 Opus 决策的设计问题

### R10 — R8 的新时限可能放宽了用户确认的 60 秒要求

- **原计划 / 用户要求：** 用户针对“同一天可能新建 CSV”选择 Q2=B，答复为“60s 内识别即可”。PLAN §0.1 和此前 AC-B02 据此将 60s 作为新文件拾取要求。Opus 第 2 轮裁决把 S9 时限改为 `max(60s, RefreshIntervalSeconds)`，AC-B02 也相应改成刷新间隔 90s 时可等 90s。
- **代码证据：** `SetupTimer` 按 `Settings.RefreshIntervalSeconds` 安排刷新，`ValidateSettings` 没有上限，因此现场配置大于 60s 是允许的。只有每次刷新 Tick 才会检查/切换最新 CSV；当前无独立轮询器。
- **冲突与影响：** 如果“60s 内识别”是用户要求的绝对拾取上限，则产线刷新间隔 90s 时接受 90s 超过该要求。第 2 轮裁决把 60s 解释为“相对现状不得新增的延迟”，但用户原答复没有明确说这是相对时限。两种解释会导致不同验收结果。代码当然无法在不进行目录检查的情况下保证 60s 内识别；方案 3（独立扫描器）会增加磁盘访问，可能重新影响相机。
- **需 Opus 决定什么：** 在 PLAN/AC/Handoff 认定 R8 为最终裁决前，确认 Q2=B 的 60s 含义：
  1. **绝对上限 60s**：如果产线 `RefreshIntervalSeconds > 60`，需用户确认允许缩短刷新间隔或增加独立检查；不能按当前 `max(60s, R)` AC 宣称满足；或
  2. **相对当前周期不变差**：用户接受刷新间隔 >60s 时，新 CSV 可等到下一次刷新（时延上限 `R`）。需把此解释标为推测并取得用户确认后，AC-B02 的 90s 结果才可算通过。

  在用户回答前，是否允许构建 B 部署或 S9 验收通过，应由 Opus 明确。构建 A 的诊断不受此问题影响。

### R11 — 新增性能配置的下限仅记在 Handoff，未定义运行时保护

- **原计划 / Handoff：** `ProgressTextIntervalMs` 默认 1000，标注“下限 250”；`NewFileMaxLatencySeconds` 默认 60。PLAN 的 S1 只说明新增 `PerformanceSettings` 和旧版配置的 `??= new()`，未写数值字段验证/钳制，也没有相应 AC。
- **代码证据：** 现在 `RefreshDataAsync` 在 `try` 块**之前**创建 `DispatcherTimer` 并设置 250ms interval。实施后该值将来自 JSON 配置。如果被设为 0 或负数，WPF timer interval 的赋值可能抛 `ArgumentOutOfRangeException`；因发生在 `try` 之外，当前 catch/finally 不能保护此路径，可能使刷新任务异常退出且不写 perf 行。`SettingsService.Load` 会反序列化 `Performance` 对象，但现有加载逻辑未给对象内每个数值校验；`ValidateSettings` 目前只检查现有 refresh interval 下限等设置。
- **冲突与影响：** Handoff 写有下限却没有说明在哪里执行；以“默认值为 1000”不能防止用户/部署配置 JSON 中显式填 0。AC-R05 仅覆盖完全没有 Performance 段，不覆盖格式合法但数值越界的配置。S9 的负 `NewFileMaxLatencySeconds` 会使公式阈值退化为 0、每次刷新扫描，导致预期优化失效。
- **需 Opus 决定什么：** 指定统一保护策略及位置（`SettingsService.Load` 归一化、`PerformanceSettings.Validate` 并在刷新 `try` 内处理、或使用显式安全默认值），然后明确：
  - `ProgressTextIntervalMs` 是否钳制到 ≥250ms，还是错误配置时回退至 1000ms；
  - `NewFileMaxLatencySeconds` 是否必须为正值，错误时回退 60s 还是禁用 CachedFileLocate；
  - 增加一个可判定 AC：缺失 Performance 段、显式 0、负数配置时程序不在刷新守卫/计时器初始化处崩溃，perf 记录/错误状态符合所选策略。

  此为运行时配置健壮性设计决策，不要求为此阻止构建 A；构建 B 的 S1/S7/S9 应按 Opus 选定策略实施。

## 4. 结论

除 R10（Q2 的 60s 是绝对还是相对时限）及 R11（新 Performance 数值配置的无效值策略）外，最新 Handoff 的文件/API、依赖、调用点、构建 A/B 范围和多数 AC 均可按代码落地。R10 影响 S9 是否满足用户已确认的行为；R11 影响 S7/S9 的非法配置时行为。请 Claude Opus5.5 裁决后再更新计划/Handoff/受影响 AC。

本次没有修改代码、PLAN、ACCEPTANCE 或 Handoff；只写本复核文档。

**PLAN STATUS: REVIEW REQUIRED**
