# Developer Handoff（最新）

- 日期：2026-11-24
- 来源：P2 设计计划（Claude Opus5.5），交给开发实施（Claude Sonnet5.5）；2026-11-24 按复核裁决更新
- 必读：`docs/PLAN.md`（§0 复核事实、§2 禁止修改项、§3 步骤、§5 默认值、§8 待确认）、`docs/ACCEPTANCE.md`
- 背景分析：`docs/analysis/2026-11-24_analysis.md`

## 1. 一句话任务

本软件每次刷新会导致同机的相机软件卡顿、PLC 收到相机信号延迟约 200ms。先**测量归因**（阶段 0），再做**不改变功能结果的减压**（阶段 1）；再做用户已确认的行为优化（阶段 2：S9 缓存 CSV 路径、S10 回退时间戳、S11 自动刷新隐藏遮罩、S13 进程 BelowNormal）。产线是本地 HDD，每次刷新都卡。根因尚未证实，主假设是 H1：对相机活动目录的路径级文件系统访问；次假设是 H2：分层窗口的渲染尖峰。

## 2. 当前状态

- PLAN 状态：**PLAN UPDATED**（2026-11-24 裁决见 `docs/review/2026-11-24_decision.md`、`docs/review/2026-11-24_decision_2.md`、`docs/review/2026-11-24_decision_3.md` 与 `docs/review/2026-11-24_decision_4.md`）。先交付**构建 A**（S0～S3）做现场测量；S5～S11、S13 可以继续开发，但**构建 B 要等用户审阅测量结论后才能部署**。**S12 不要动手**。
- 工作树有既存未提交改动：散点图功能（`MainWindow.xaml`、`MainViewModel.cs`），以及大量与本任务无关的 `rsc/` 等文件删除。**不要还原、不要清理、不要混进本次提交。**
- 基线是否可编译：**未验证**（P2 没有编译，以免写 bin/obj）。S0 第一件事就是编译。

## 3. 实施顺序与检查点

| 步骤 | 内容 | 文件 | 检查点 |
|---|---|---|---|
| S0 | 基线编译 `dotnet build CrimpHeightAutoAdjustment.slnx -c Debug`，记录警告数 | — | 失败就停止并报告 |
| S1 | `PerformanceSettings` + `AppSettings.Performance` + `SettingsService.Load` 中加 `??= new()`，之后调用 `PerformanceSettings.Normalize()`（R11：`ProgressTextIntervalMs < 250` → 250；`NewFileMaxLatencySeconds ≤ 0` → 60；被修正的字段记入 `[JsonIgnore]` 列表） | `Models/AppSettings.cs`、`Services/SettingsService.cs` | 编译；AC-R05、AC-R08 |
| S2 | CSV 细分计时，新增 `LastReadMetrics`（保留 `LastReadDiagnostics` 原样） | `Services/CsvDataProvider.cs` | 编译；AC-D01 |
| S3 | 新建静态后台日志队列 `Channel<LogEntry>`（`readonly record struct LogEntry(string FilePath, string Line)`，无界、单读者），**此时只给 perf 日志用**；每次**通过入口守卫**的刷新都在 `finally` 中写一行 `log/perf_yyyyMMdd.log`。每行以 `type=refresh` 或 `type=config` 开头（R12）：config 行只在构造函数 `Load` 之后写一次，内容为 Performance 生效值和 `NormalizedFields` | `ViewModels/MainViewModel.cs` | 编译；AC-N01、AC-R08 |
| **交付构建 A** | 构建 A = S0～S3，行为与现状一致，只多 perf 日志。用户在现场跑 T0/T1/T3（T2 可选），见 ACCEPTANCE §3 | — | **测量门**：用户审阅结论之前，构建 B 不得部署（开发可以继续编码） |
| S5 | `WriteLogToFile` 接入同一个 `Channel<LogEntry>`；`Cleanup` 幂等（`Closing` 和 `App.OnExit` 都会调用）；完成写端后最多等 1s，超时放弃剩余条目，不阻塞关闭 | `ViewModels/MainViewModel.cs` | 编译；AC-R03、AC-E04 |
| S6 | 在 UI 线程拍下配置快照，`await Task.Run(PrepareChartData)`；快照**同时传给 `ComputeRobustTrendRange`**（它也读 `Settings.Controller`）；`ApplyChartData` 仍在 UI 线程 | `ViewModels/MainViewModel.cs` | 编译；AC-D02（差异审查 + UI 对比） |
| S7 | `progressTimer` 间隔取 `Performance.ProgressTextIntervalMs`（默认 1000） | `ViewModels/MainViewModel.cs` | 编译 |
| S8 | `Task.Run` 内 `THREAD_MODE_BACKGROUND_BEGIN`，`finally` 中 `END`；开关 `CsvBackgroundIoMode` | `Services/CsvDataProvider.cs` | 编译；AC-D01、AC-B05、AC-E05 |
| S9 | 缓存最新 CSV 路径。重扫条件：跨天；文件消失（同一次刷新内立即重扫）；距上次扫描 ≥ `max(0, NewFileMaxLatencySeconds − RefreshIntervalSeconds)`。原“空闲 60s / 强制 300s”作废。perf 指标中记录 `fileChanged` 和 `rescan` | `Services/CsvDataProvider.cs`，以及 `MainViewModel` 中三处构造调用 | 编译；AC-B01～B03、AC-D05 |
| S10 | 回退时间戳改用 `GetFileAttributesEx` 已经取到的 `ftLastWriteTime`（UTC FILETIME 转本地时间） | `Services/CsvDataProvider.cs` | 编译；AC-D03 |
| S11 | 新增 `IsLoadingOverlayVisible`，XAML 遮罩改绑这个属性；自动刷新时为 false，手动重置时为 true | `ViewModels/MainViewModel.cs`、`MainWindow.xaml` | 编译；AC-N05、AC-R04 |
| S13 | 构造函数中按开关设置进程优先级 BelowNormal | `ViewModels/MainViewModel.cs` | 编译；AC-N04 |
| **交付构建 B** | 只在用户审阅构建 A 的结论之后；现场跑 T4/T5（ACCEPTANCE §3） | — | AC-N03；T3 的结果决定 S12 是否提请用户确认 |

说明：队列在 S3 建立，只给 perf 日志用；S5 再把 `WriteLogToFile` 接入。这样构建 A 的 PLC 日志写入方式与现状完全一致，T1 测到的才是现状行为。

### `PerformanceSettings` 字段（PLAN §5）

```
EnablePerfLog            bool  = true
ProgressTextIntervalMs   int   = 1000   (下限 250)
CsvBackgroundIoMode      bool  = true
CachedFileLocate           bool = true   // S9（Q2=B）
NewFileMaxLatencySeconds   int  = 60     // S9（R2 修订；取代已作废的 RescanWhenIdleSeconds / ForceRescanSeconds）
ShowOverlayOnAutoRefresh   bool = false  // S11（Q4a=A）
ProcessPriorityBelowNormal bool = true   // S13（Q5=A），需重启生效
```

## 4. 禁止事项（硬约束）

- 不要修改 `CrimpHeight.Core/**`、`KeyencePlcAdapter.cs`、`MockPlcAdapter.cs`、`Interfaces/*`。
- 不要修改 `OpenFileNoOplock` 的打开标志，也不要改 `ReadTailAndParse`、`ParseLine`、`ParseTimestamp`、`ExtractLinesFromTail` 的逻辑（只允许在外围插入计时代码；S10 例外：只允许替换回退时间的来源）。
- 不要修改控制与下发链路的语义（`SendPlcCommandsAsync`、`ComputeHeadPlcValue`、`OnHeadAdjustmentSent`、`ManualAdjustAsync`、心跳、配方轮询）。
- 不要引入 FileSystemWatcher，不要对相机目录做任何写入。
- **不要执行 `dotnet clean`，不要删除 bin/obj**：`bin\Debug\net8.0-windows\HslCommunication.dll` 是未纳管的引用。
- 不要改 `AllowsTransparency` 或窗口外观（S12 决策门，需要 T3 数据加用户再次确认）。
- 不要提交、推送或建 MR，除非用户明确要求；需要提交时，按 S 步骤分别提交，并且只暂存本任务涉及的代码块（散点图改动会出现在同一个 `MainViewModel.cs` 里，要用 `git add -p` 拆开）。

## 5. 实施注意事项

- `THREAD_MODE_BACKGROUND_BEGIN = 0x00010000`、`THREAD_MODE_BACKGROUND_END = 0x00020000`；只能对**当前线程**调用（`GetCurrentThread()`），并且同一线程上 BEGIN/END 必须成对出现。END 必须放在 `finally` 里。
- `PrepareChartData` 依赖 `Settings.Controller.WindowSize/UpperLimit/LowerLimit`，它内部调用的 `ComputeRobustTrendRange` 也读取 `Settings.Controller`。移到后台之前，先在 UI 线程把这些值读成一个快照 record，同时传给这两个函数。两个函数体除了 `Settings.Controller.X` → 快照字段之外不得有其他改动（AC-D02 的差异审查以此为准）。
- 日志队列（S3 建立，S5 接入 `WriteLogToFile`）：`readonly record struct LogEntry(string FilePath, string Line)`；`Channel.CreateUnbounded<LogEntry>(new UnboundedChannelOptions { SingleReader = true })`，静态，消费者在第一次投递时懒启动。消费者按批、按目标文件分组 `File.AppendAllText`，捕获所有异常（保持“日志失败不影响主流程”）。`Cleanup` 用 `Interlocked` 标志保证幂等：`Writer.TryComplete()` 后对消费者任务 `Wait(1000)`，超时直接返回，不再重试。
- perf 日志必须覆盖 `RefreshDataAsync` 的三个出口：正常完成、无数据提前 `return`、`catch`。建议放在 `finally` 里统一写。
- 计时统一用 `Stopwatch`；开始时间用 `DateTime.Now:HH:mm:ss.fff`。
- 配置健壮性（R11）：`Normalize()` 只在 `SettingsService.Load` 中调用（`Settings` 只通过 `Load` 赋值）。使用这些值的地方仍然要兜底：`progressTimer` 的间隔写 `Math.Max(250, Settings.Performance.ProgressTextIntervalMs)`；S9 的阈值按 `max(0, latency − R)` 计算。**注意**：`progressTimer` 是在 `try` 之前创建的，如果在那里抛异常，`IsLoading` 不会复位，心跳也不会恢复，所以兜底必不可少。perf 日志在启动时（或第一次写入时）记一行有效的 Performance 配置和被修正的字段。JSON 类型错误导致整份配置回到默认值，这是现有行为，不要改。
- S9（R2 修订）：在 `CsvDataProvider` 内维护 `_cachedLatestFile`、`_lastScanUtc`、`_scanDate`。重扫阈值 = `max(0, NewFileMaxLatencySeconds − RefreshIntervalSeconds)`，相关开关和数值通过构造函数的**可选参数**传入，保证现有两参数构造调用和 AC-D01 的 harness 都能用。`GetRealFileSize` 返回 -1 时在同一次刷新内立即重扫。`MainViewModel` 里的三处 `new CsvDataProvider(...)`（`InitDataProvider`、`PollRecipeAsync` 的回退分支、`SwitchRecipeAsync`）都要传入新参数。保存设置会走 `InitDataProvider` 重建实例，所以刷新间隔变化会自动生效。
- 时限说明（R8 修订）：从新文件出现到切换 ≤ `max(60s, RefreshIntervalSeconds)`，即 S9 不得比现状更晚。**不要**给 `RefreshIntervalSeconds` 增加上限校验，**不要**增加独立的扫描定时器；阈值公式 `max(0, 60 − R)` 不变。上述时限的前提是每次刷新都在一个刷新间隔内完成。为方便验收，perf 日志要能反映 Tick 落空（例如记录相邻两次刷新开始时间的间隔）。
- S10：`GetRealFileSize` 顺带返回 `ftLastWriteTime`，用 `DateTime.FromFileTimeUtc(...).ToLocalTime()` 换算，作为 `ParseLine` 的回退值传入（与原来的 `File.GetLastWriteTime` 语义一致，都是本地时间）。
- S11：定时器触发、`ToggleRun` 启动和配方切换引发的刷新都属于“自动”，`ResetAndRefreshAsync` 属于“手动”。建议新增私有方法 `RefreshCoreAsync(bool manual)`，保留 `RefreshDataCommand` 并让它调用 `RefreshCoreAsync(false)`，避免破坏已生成的命令。
- S13：`try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) { WriteLogToFile(...); }`

## 6. 验证要求

- 每步都要真正编译并看到成功；没有自动化测试工程，按 ACCEPTANCE §2 做离线对比。**没有实际运行的检查项，在汇报里一律标为“未验证”**。
- 汇报时附上：编译输出摘要、AC 逐条结果（通过 / 未通过 / 未验证 + 证据）、perf 日志样例行。

## 7. 用户答复（2026-11-24，详见 PLAN §0.1）

Q1=B 本地 HDD｜Q2=B 同一天可能新建 CSV，60s 内识别即可｜Q3=A 每次刷新都卡｜Q4a=A 自动刷新隐藏遮罩｜Q4b=C 测量后再定（S12 决策门）｜Q5=A BelowNormal｜Q6=A p99 增量 ≤20ms、2h 内无冻结｜Q6-measure=A PLC 程序打时间戳｜Q7=A 保留散点图改动，不混入提交。

仍待确认（不阻塞）：
- 产线 `UseRealPlc`、心跳和轮询的实际配置；
- 产线刷新间隔（决定 S9 的收益）；
- PLC 能否同时记录“触发”和“收到相机信号”两个时刻（R3，决定 AC-N03 用指标 L 还是备选指标；须在构建 A 现场测量开始前确认）。

## 8. 裁决变更（2026-11-24，详见 `docs/review/2026-11-24_decision.md`）

- R1 ACCEPT：拆成构建 A / 构建 B，测量门只管部署，不限制编码。
- R2 ACCEPT：S9 的重扫规则改为 `max(0, 60 − RefreshIntervalSeconds)`；空闲 60s / 强制 300s 两个条件作废。
- R3 PARTIAL：定义指标 L（PLC 单时钟测得的间隔）和备选指标。
- R4 PARTIAL：AC-D01 用仓库外的 harness 逐条比对；AC-D02 改为差异审查 + UI 对比；S6 的快照同时传给 `ComputeRobustTrendRange`。
- R5 ACCEPT：日志消息统一为 `LogEntry(FilePath, Line)`；`Cleanup` 幂等，最多等 1s，超时放弃剩余条目。
- R6 ACCEPT：AC-R02 只要求不启动新的心跳 Tick。
- R7 PARTIAL：对 JPG 并不逐个查询属性（复核报告的这一说法不成立）；AC-D05 改为先测基线再比较。

## 9. 第 2 轮裁决变更（2026-11-24，详见 `docs/review/2026-11-24_decision_2.md`）

- R8 ACCEPT：S9 的时限改为 ≤ `max(60s, RefreshIntervalSeconds)`（不比现状更晚）。不加刷新间隔上限校验，不加独立扫描定时器；代码设计不变。
- R9 PARTIAL：AC-N03 只能用指标 L 判定，取不到 L 时记为“不可判定”并回报用户；备选抖动指标只用于 S4 归因。不阻塞编码。

## 10. 第 3 轮裁决变更（2026-11-24，详见 `docs/review/2026-11-24_decision_3.md`）

- R10 PARTIAL：Q2 的 60s 存在歧义，但 R >60s 时 S9 与现状等价，不阻塞。交付报告中向用户说明识别时延 ≤R；用户如需绝对的 60s，另行决定是否缩短刷新间隔（待确认）。
- R11 ACCEPT：S1 增加 `PerformanceSettings.Normalize()`（`ProgressTextIntervalMs < 250` → 250，`NewFileMaxLatencySeconds ≤ 0` → 60），使用处再 `Math.Max` 兜底；新增 AC-R08。

## 11. 第 4 轮裁决变更（2026-11-24，详见 `docs/review/2026-11-24_decision_4.md`）

- R12 ACCEPT：perf 日志每行以 `type=refresh` 或 `type=config` 开头。
  - config 行只在构造函数中 `Settings = _settingsService.Load()` 之后写一次，包含 Performance 生效值和 `NormalizedFields`（为空时写 `normalized=none`）；取消设置面板重新 Load 时只输出 `Debug.WriteLine`。
  - `EnablePerfLog=false` 时，如果有字段被修正，用 `WriteLogToFile` 在 `plc_*.log` 写一行 `[配置] Performance 字段已修正: ...`。
  - AC-N01 只数 `type=refresh` 行；AC-R08 按 `EnablePerfLog` 的开/关分别判定。
