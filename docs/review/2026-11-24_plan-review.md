# P2 Plan Review：刷新导致相机软件卡顿

- 日期：2026-11-24
- 复核者：Claude Sonnet5.5（实施前复核）
- 状态：**REVIEW REQUIRED**
- 输入：`docs/handoff/HANDOFF_latest.md`、`docs/PLAN.md`、`docs/ACCEPTANCE.md` 及当前工作树代码
- 本次范围：复核计划与 AC；不改代码，不擅自修订计划。

## 1. 先前阶段待落盘文件

上一阶段的文件已经存在，不需要重复写入：

- `docs/PLAN.md`
- `docs/ACCEPTANCE.md`
- `docs/handoff/HANDOFF_latest.md`
- `docs/analysis/2026-11-24_analysis.md`

本次只新增本 review 文件。

## 2. 复核摘要

计划涉及的主要现有入口均存在，项目依赖大体匹配：

- `MainViewModel.RefreshDataAsync`、`ToggleRun`、`ResetAndRefreshAsync`、`SetupTimer`、`WriteLogToFile`、`Cleanup` 均存在。
- `[RelayCommand]` 位于 `RefreshDataAsync` 和 `Cleanup` 上，生成命令的名称分别依约为 `RefreshDataCommand`、`CleanupCommand`。若为了区分自动/手动刷新而改名，应保留兼容入口；Handoff 对此已提示。
- `CsvDataProvider.GetLatestDataAsync`、`GetLatestCsvFile`、`FindCsvInDirectory`、`GetRealFileSize`、Win32 `GetFileAttributesEx` 和 `WIN32_FILE_ATTRIBUTE_DATA` 均存在。S2/S9/S10 可在现有服务内接入，不需改控制核心。
- `AppSettings`、`SettingsService.Load`、XAML `IsLoading` 遮罩绑定存在；`PerformanceSettings`、`AppSettings.Performance`、`LastReadMetrics`、`IsLoadingOverlayVisible` 和计划中的线程优先级功能是新增 API，不是现存 API。
- 项目目标为 `net8.0-windows`；`System.Threading.Channels` 和 `System.Diagnostics.Process` 属于框架能力，不需要新 NuGet 依赖；`MainViewModel` 已引用 `System.Diagnostics`。线程后台模式所需 P/Invoke 目前不存在，但可按计划新增在 `CsvDataProvider`。
- `MainViewModel` 中已存在的调用关系：运行按钮和 DispatcherTimer 都调用 `RefreshDataAsync`；重置刷新调用它；配方切换仅在 `IsRunning` 时调用它。刷新期间 `IsLoading` 阻止重入，并停止心跳 DispatcherTimer；心跳已经启动的异步操作不受 `Stop()` 取消。
- `MockPlcAdapter` 会产生 `OnLog`，`MainViewModel.InitPlcAdapter` 把日志接到 `WriteLogToFile`；AC-R01/R03 可以依赖已有 Mock 日志做事务类型/顺序对比。
- 没有发现自动化测试项目。项目文件引用的 HslCommunication DLL 存在于本地 `bin`，但未纳管；Handoff 中“不要 clean/删除 bin”提醒合理。**本次未构建**，基线编译状态仍未验证。

复核发现下列设计/验收冲突，不能直接按当前 Handoff 次序实施，须由 Claude Opus5.5 决定是否修订计划及 AC。

## 3. 需 Claude Opus5.5 决定的事项

### R1 — 现场测量门与开发实施顺序互相矛盾

- **原计划：** `PLAN.md` 将 S4 放在阶段 0，并说明现场测量是阶段 2 的前置条件；停止条件要求若刷新无相关性则停止后续优化。
- **代码/文档证据：** 最新 `HANDOFF_latest.md` 的实施表把 S4 排在 S0～S11、S13 之后；表格此前步骤会先实现缓存路径、隐藏遮罩、调低进程优先级等会改变行为的改动。`ACCEPTANCE.md` 的 T4/T5 依赖这些改动。
- **冲突与影响：** Developer 按 Handoff 顺序执行会在完成基线/阶段 1/阶段 2 代码后才做现场测量，违背 PLAN 的阶段门和停止条件；若根因并非本软件，可能先上线无效且改变行为的改动。
- **需 Opus5.5 决定：** 是否将 S4 明确排在 S5～S13 前：先 S0～S3，现场基线/归因，用户审阅测量结论后再进入减压/行为改动；或者允许先做阶段 1 的无行为变更项，但明确 S9/S10/S11/S13 在现场证据门之前不得默认部署。同步调整 Handoff 的实施顺序与“阶段 2 前置条件”用语。

### R2 — S9 的 60 秒新文件时限没有得到算法保证

- **原计划：** 用户确认 Q2=B：同一天可能新建 CSV，要求 60 秒内识别；S9 只在“缓存文件大小 60s 不增长”、跨天、缓存文件丢失或 300s 强制重扫时扫描目录。AC-B02 当前写“60s + 一个刷新周期”后切换。
- **代码证据：** 当前默认刷新间隔由设置驱动；开发机配置为 20s，生产刷新间隔尚未确认。现有 `GetLatestCsvFile` 每次会重新枚举，缓存后则只有 S9 条件触发才会再选最新 CSV。`_lastGrowthUtc` 方案检测的是**旧缓存文件大小是否增长**，不是目录中是否出现了新文件。
- **冲突与影响：** 若旧文件停止增长，等满 60s 后还需等下一次 DispatcherTimer Tick，最坏可约 60s + 刷新间隔；20s 间隔时可能约 80s，超过用户的 60s 上限。若新文件出现时旧文件仍增长，则 idle 条件一直不成立，最多等 300s 强制扫描，明显超过 60s。当前 AC-B02 与 Q2 的“60s 内”也不一致。
- **需 Opus5.5 决定：** 明确 60s 时限从“新文件创建”还是“旧 CSV 停止增长”开始；并决定重扫上限是否必须保证 `≤60s`（例如固定最多 60s 周期扫描，或有可靠的新文件出现提示），还是由用户重新确认较长延迟。同步给出包括刷新间隔的最大拾取时限，并统一 PLAN/AC/Handoff。不能把“空闲 60s + 一个刷新周期”称为满足“60s 内”。

### R3 — PLC 单侧时间戳不必然能测量“信号延迟”

- **原计划：** Q6-measure=A 被解释为 PLC 为相机信号打时间戳；AC-N03 以相机信号延迟 p99 相对软件停止基线增加 ≤20ms 验收，并用 PLC 时间戳与本机 perf 日志对齐。
- **代码/外部接口证据：** 本软件只有 PLC 事务与本机刷新时间日志；没有相机信号发送时间戳 API、相机事件序号或 PLC 原始触发时间来源。PLC 对“收到信号”打一个接收时间戳，只给出到达时刻；`perf_*.log` 记录的是本软件刷新阶段耗时，与每个相机信号的产生时刻无直接对应关系。PLC 与 PC 时钟差异也需要校准。
- **冲突与影响：** 仅有接收时间戳不能直接算单个信号的端到端传输延迟，也不能仅凭到达时刻计算相对延迟增量；除非 PLC 同时能记录相机信号产生/预期触发时刻，或有可定义的源端事件/周期基准。AC-N03 当前测量方法可能产不出所需 p99 数值。
- **需 Opus5.5 决定：** 明确 PLC 可记录的时间戳字段和延迟公式（起点、终点、是否需相机源时间/已知周期、PLC-PC 同步误差如何处理）。如果目前只能量到 PLC 到达时刻，决定是否把验收指标改成可观测的到达间隔抖动/超时，或要求先由产线人员补充可测的源端标记；不要把“PLC 可 timestamp”直接等同“可测端到端 delay”。

### R4 — AC-D01/D02 的逐项数据等价检查没有当前可用观测通道

- **原计划：** AC-D01 要逐条比较每个记录的时间戳和双头高度；AC-D02 要逐项比较私有 `ChartData` 的数组、范围、直方图计数和标签。Plan/Handoff 说项目无自动化测试工程，并声明不新增文件。
- **代码证据：** `CsvDataProvider` 是 public，`GetLatestDataAsync` 也 public，故独立 harness 可以读取行；但现有 UI 只显示有效样本数、首尾 timestamp 和统计值，不显示每条记录。`MainViewModel.PrepareChartData`、内部 `ChartData` 结构及 `ApplyChartData` 为 private；没有现成导出或对比接口。AC-D01 的“临时打开 Debug 输出逐条对比”在当前代码中不存在；AC-D02 也没有现成方式读出完整私有结果。
- **冲突与影响：** 按现有验收文字无法实际取得所需逐记录和图表数组，单看截图/首尾值不足以证明“每条完全一致”；不能声称 AC-D01/D02 通过。
- **需 Opus5.5 决定：** 选择可审计的验证途径：允许在仓库外用临时 harness/脚本（并说明如何调用 private ChartData），允许添加短期但不提交的诊断导出，或缩窄 AC 到现有 UI 可观察量。若仍要求逐项/逐行严格一致，应把临时 harness 的准备、运行与输出比对步骤写入 AC；并决定是否需要一个受控的内部可测 seam / test project（与“无新增文件”约束冲突时需明确优先级）。

### R5 — 日志队列数据结构冲突，且关闭 AC 与有界 flush 不相容

- **原计划：** `PLAN.md` S5 写 `Channel<string>`；S3 随后又要求不同输出目标，允许“另开目标文件或带路径 tuple”。最新 Handoff 直接指定 `Channel<(string path, string line)>`。S5 只等待 ≤1s flush；AC-E04 同时要求 1s 内退出且两个日志文件最后一行都已落盘。
- **代码证据：** 当前 `WriteLogToFile` 是静态同步 `File.AppendAllText`，PLC/Mock 日志都走这个入口；perf 日志需要不同 `perf_*.log` 文件。当前 `Cleanup` 会由 `MainWindow.Closing` 调用，并在 `App.OnExit` 再调用一次；所以新队列的完成/flush 必须幂等。计划预期的共享磁盘/过滤驱动延迟意味着后台写也可能超过 1s。
- **冲突与影响：** PLAN/Handoff 对 Channel 消息类型不同，直接照办会造成实现接口不一致。若 I/O 卡住超过 1s，既要求进程 1s 内退出，又要求所有待写日志末行都已落盘无法同时保证；若 `Cleanup` 等待期间同步阻塞 UI，恰好重新引入刷新/停机卡顿。
- **需 Opus5.5 决定：** 统一队列消息结构（建议文档只指定一个含 path、line 的记录，而非多种可选方案）；并确定 shutdown 语义：1s 到时允许丢弃未刷日志（修订 AC-E04 明确可丢尾批），还是必须完整 flush（则放宽 1s 退出要求，定义超时/不可写行为）。同时规定 `Cleanup` 双重调用的幂等要求及消费者仍在写时的处理方式。

### R6 — AC-R02 对现有心跳暂停行为表述过强

- **原计划：** AC-R02 要求“刷新期间不发心跳”，成功、无数据、异常都验证；PLAN 禁止改变 `SendHeartbeatAsync` 业务语义。
- **代码证据：** `RefreshDataAsync` 在刷新开头调用 `_heartbeatTimer?.Stop()`，结束时重新 `Start()`；然而 `DispatcherTimer.Tick` 的 handler 是 `async`，会启动 `SendHeartbeatAsync()`，`Stop()` 不取消已经开始、正在等待 PLC adapter / semaphore 的操作。
- **冲突与影响：** 现有代码只保证停止新的定时 Tick，不保证刷新边界处已经开始的 heartbeat 不会在刷新期间完成。AC-R02 若逐字判定，既存行为可能失败，或者诱导开发新增取消/等待逻辑，违反不改 PLC 业务时序的范围。
- **需 Opus5.5 决定：** 是否将 AC 限定为“刷新期间不再启动新的 heartbeat tick，已有 in-flight 操作可完成”；或者明确需要计入等待/取消现有请求，并调整计划的禁止修改项、握手时序风险及测试。

### R7 — S9 的“目录扫描”证据与缓存性能目标需按实际测量描述

- **原计划：** PLAN §0 R3 和 H1 将最新日期目录里大量 `*_NG.jpg` 说成每次 CSV 枚举需要扫描的目录内容；AC-D05 要求每次刷新 QueryDirectory 从有到无（缓存时）。
- **代码证据：** `FindCsvInDirectory` 调用 `Directory.EnumerateFiles(directory, "*.csv", options)`，只将匹配 `*.csv` 的结果送入 LINQ 排序；排序的文件日期/last-write 查询对候选 CSV 执行。Windows/文件系统如何遍历和筛选大量非 CSV 目录项、ProcMon 会暴露成多少 QueryDirectory/QueryInformation，取决于文件系统/过滤驱动，当前代码不能给出实际成本。缓存命中仍会对缓存 CSV 调用 `GetRealFileSize`，目录查询才会被避免。
- **冲突与影响：** 将“有很多 JPG，所以每次为每个 JPG 做时间属性查询”当作事实不准确；AC-D05 也可能因底层 API/ProcMon 事件形态不同而无法按字面计数。H1 方向合理，但需要 trace 证据，不应预先确认根因。
- **需 Opus5.5 决定：** 是否把 R3/H1 改成带条件的假设，明确只预计减少目录枚举/CSV 候选属性访问，不宣称减少 JPG 的单文件属性查询；AC-D05 改为比较刷新期内本进程在相机目录下的 QueryDirectory/QueryInformation 总次数和耗时（按 ProcMon 实测事件命名），并要求先采一轮基线确认这些事件可见。

## 4. 其他可测性/范围备注

- **AC-N01：** `RefreshDataAsync` 的 guard（面板打开、已加载、无 provider）会直接返回，不应算作一轮已开始的刷新。perf 行数应按“通过 guard 并创建 Stopwatch 的刷新实例”判定。无数据分支存在 early return，因此确需在 `finally` 统一写；`RefreshDataCommand` 由 `[RelayCommand]` 生成。
- **AC-B02：** 即使接受“60s 停止增长 + 一个刷新周期”，当前生产刷新间隔仍未确认。验收应直接判定从新 CSV 首次出现到 `LastUsedFilePath` 切换的 elapsed time，而非只观察经过 60s 后的状态。
- **AC-D04：** 只读检查可以用 ProcMon，但应以进程和相机路径过滤；文件打开的 desired access 与列出的具体事件类型要按实际 ProcMon 版本核实。不能仅凭“当前代码用 GENERIC_READ”就跳过现场证明。
- **AC-N03/T0-T5：** 2h 基线与 2h 默认配置轮次合理作为总体验收，但需控制产品/产量、信号样本量和 PLC 时钟偏差；T1/T3 30min 只够相关性线索，不足以替代最终 2h AC-N03。
- **AC-E04：** 关闭动作经 `Closing` 和 `OnExit` 两处清理，测试必须覆盖第二次 Cleanup；此处与 R5 的队列完成语义相关。
- **交付范围：** S0～S13 涉及文件基本限制在计划指定的 `CrimpHeightAnalyzer`；未看到必须触碰 `CrimpHeight.Core` 的功能理由。`S9` 需要新增缓存状态，`S10` 需要更改 `ParseLine` 回退时间来源，是 Handoff 已批准的两个明确例外。

## 5. 评审结论

上述 R1～R7 包含实施顺序、用户明确的 60s 行为界限、主验收指标可测性和 AC/实现契约冲突，属于设计级问题；本复核不替 Opus 修改 PLAN、AC 或 Handoff，也不运行构建/产线测试。

请 Claude Opus5.5 对每条 R1～R7 作出决定并同步更新规范文档后，再交付实施。

**PLAN STATUS: REVIEW REQUIRED**
