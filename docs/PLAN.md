# PLAN：刷新数据时影响同机相机检测软件（卡顿 / PLC 信号延迟约 200ms）

- 日期：2026-11-24
- 阶段：设计计划（P2），基于 `docs/analysis/2026-11-24_analysis.md` 复核代码后编写
- 状态：**PLAN UPDATED**（2026-11-24 按 `docs/review/2026-11-24_decision.md` 裁决 R1～R7、按 `docs/review/2026-11-24_decision_2.md` 裁决 R8～R9、按 `docs/review/2026-11-24_decision_3.md` 裁决 R10～R11、按 `docs/review/2026-11-24_decision_4.md` 裁决 R12 修订）；S12 保留测量后决策门；构建 B 的部署须先过现场测量门（§3）
- 关联：`docs/ACCEPTANCE.md`（AC 与验证）、`docs/handoff/HANDOFF_latest.md`（开发交接）

---

## 0. 复核结论（对分析文档的修正与补充）

分析文档的主体结论已经过代码复核，下面是**复核后新增或修正的事实**：

| # | 结论 | 依据 | 性质 |
|---|---|---|---|
| R1 | 主窗口为 `WindowStyle=None` + **`AllowsTransparency=True`**（分层窗口，WPF 对整窗走软件渲染 + `UpdateLayeredWindow`）。每次刷新都会显示一层覆盖整个内容区的半透明加载遮罩（`#CC000000`，绑定 `IsLoading`），另有一个 250ms 的 `progressTimer` 反复改写 `PerformanceText`（该文本同时显示在遮罩和底部卡片上）。所以**每次刷新都会触发至少一次整窗重绘，以及 250ms 一次的周期性重绘**。分析文档没有覆盖这条路径。 | `MainWindow.xaml` 第 1～20 行窗口属性，以及 Grid.Row=1 的遮罩 Border；`MainViewModel.RefreshDataAsync` 中的 `progressTimer` | 事实（代码）；它对相机的影响属于推测 |
| R2 | 历史上对**同一个相机 CSV** 执行 `CreateFile` 会被阻塞 2～16 秒，`CloseHandle` 也会阻塞数秒；`GetFileSizeEx` 曾返回旧值。这说明目标文件所在的文件系统路径上存在 oplock / 过滤驱动 / 非纯本地文件系统语义（例如杀毒/EDR、OneDrive 云文件过滤或网络重定向）。**本软件对该路径的文件系统访问会与相机写入交互**，这一点已有历史证据。 | `docs/ChangeLog_2026-10-22.md` §1 | 事实（文档记载）；与本次卡顿的因果关系属于推测 |
| R3 | 即使启用了句柄常驻，每次刷新仍会对相机的**活动输出目录**做路径级访问：`Directory.Exists`、枚举日期目录、在最新日期目录里 `EnumerateFiles("*.csv")`（该目录同时存放大量 `*_NG.jpg`。.NET 在托管代码中做通配符匹配，系统调用层面会读出全部目录项，但不会对 JPG 逐个打开或查询属性；此点基于 .NET 运行时实现，待 ProcMon 证实），以及对每个 **CSV 候选**调用 `File.GetLastWriteTimeUtc` 和 `GetFileAttributesEx(path)`。时间戳解析失败时还会额外调用 `File.GetLastWriteTime(path)`；历史数据的时间列是科学计数法，确实会走这条回退路径。 | `CsvDataProvider.GetLatestCsvFile / FindCsvInDirectory / GetRealFileSize / ParseLine`；`rsc/F2/<日期>/` 目录结构；`docs/数据处理与分析流程_实跑说明.md` §四.2 | 事实 |
| R4 | 开发机 `bin/Debug/.../settings.json` 中的 `MonitorPath` 位于 **OneDrive 目录**，`UseRealPlc=false`、`EnergySavingMode=true`、`BypassHandshake=true`、刷新间隔 20s、`WindowSize=150`。`recipe_config.json` 配置了程序号轮询（3s）和心跳（2s）。**产线配置未知。** | 开发机 bin 目录下的配置文件 | 事实（开发机）；产线配置待确认 |
| R5 | `EnergySavingMode=true` 时只有 Drift 才写 PLC；刷新期间心跳会暂停；配方轮询（3s）不跟随刷新。所以如果卡顿发生在**每一次**刷新，PLC 通信就不是每次刷新都必然发生的动作，作为主因的可能性下降。 | `ComputeHeadPlcValue`、`RefreshDataAsync`、`SetupRecipePolling` | 事实（代码）+ 推测（因果） |
| R6 | `PrepareChartData` 的注释写的是“后台线程”，但实际在 UI 线程同步执行；`WriteLogToFile` 在 UI 线程上同步执行 `File.AppendAllText`。 | `RefreshDataAsync`、`WriteLogToFile` | 事实 |
| R7 | 工作树中已有未提交改动（趋势图“散点显示”，`MainWindow.xaml` +3 行、`MainViewModel.cs` +126 行），与本问题无关。HslCommunication 的引用路径是 `bin\Debug\net8.0-windows\HslCommunication.dll`，该文件不受 git 管理（`*.dll` 已被忽略）。**执行 `dotnet clean` 或删除 bin 都会导致无法编译。** | `git diff`、`CrimpHeightAnalyzer.csproj` | 事实 |

### 0.1 用户确认结果（2026-11-24）

| Q | 答复 | 对计划的影响 |
|---|---|---|
| Q1 | **B**：产线相机目录在本地 HDD（未标注 +AV，按“无实时杀毒扫描”处理，现场测量时顺带核实） | 机械盘对随机 I/O 敏感：本软件对含大量 NG 图片的目录做枚举和属性查询，会与相机顺序写图片/CSV 争用磁头。H1 权重上调，S8 和 S9 是主要手段。OneDrive/网络盘这类成因不适用于产线；R2 中 oplock 阻塞的来源需由测量确认（推测） |
| Q2 | **B**：同一天可能新建 CSV，60s 内识别即可接受 | S9 启用：空闲 60s 重扫、强制 300s 重扫 |
| Q3 | **A**：每次刷新都卡 | H3 降为最低；H1/H2 为主假设 |
| Q4a | **A**：自动刷新隐藏遮罩，手动重置保留 | S11 启用 |
| Q4b | **C**：测量后再定 | S12 设决策门：T3（窗口最小化）测量证实 H2 后，带数据再请用户确认 |
| Q5 | **A**：进程优先级 BelowNormal | S13 启用 |
| Q6 | **A**：p99 增量 ≤20ms，2h 内无可感知冻结 | AC-N03 阈值确认 |
| Q6-measure | **A**：PLC 程序可为相机信号打时间戳 | ACCEPTANCE §3 使用 PLC 时间戳 |
| Q7 | **A**：散点图改动保留为基线，不混入本次提交 | 提交时用 `git add -p` 拆分 |

仍待确认（不阻塞开工）：产线的 `UseRealPlc`、心跳、轮询配置。T1 测量按产线原配置运行即可。

## 1. 目标与范围

### 1.1 Bug 根因（当前判断）

**根因尚未实测确认**，不能写成定论。按证据强度排序的工作假设如下：

1. **H1（主假设，推测）**：刷新时本进程对相机**活动输出目录和活动 CSV** 发起一组同步的路径级文件系统访问（目录枚举、属性/时间戳查询；文件切换时还有 open/close）。这些访问经过 R2 所示的过滤驱动或 oplock 路径，会与相机写 CSV / 写 NG 图片的 I/O 串行化，相机 I/O 线程因此被阻塞约百毫秒级，表现为画面冻结和发给 PLC 的信号延迟。产线是本地 HDD（Q1）且每次刷新都卡（Q3），与这一假设一致：机械盘上目录枚举带来的随机寻道可达数十到数百毫秒（推测，待 ProcMon/PerfMon 证实）。
2. **H2（次假设，推测）**：分层窗口（R1）在刷新时产生 CPU/DWM 渲染尖峰（整窗软件重绘、250ms 周期重绘、LiveCharts 重绘），与相机软件争用 CPU、GPU 或合成器资源。
3. **H3（低优先级，推测）**：与 PLC 的通信（真实 PLC 模式下 Drift 时产生 3～4 次 MC 事务）挤占了相机与 PLC 的共享通信资源。R5 已削弱这一假设；Q3 回答“每次刷新都卡”，而 PLC 写入只在 Drift 时发生，进一步削弱。阶段 0 只做测量，不做改动。

### 1.2 目标

- G1：用测量数据确认或排除 H1/H2/H3，把“刷新”拆成可归因的子阶段（阶段 0）。
- G2：在**不改变控制结果和 PLC 交互**的前提下，降低本软件刷新时对共享资源的瞬时压力（阶段 1）。
- G3：减少对相机目录的路径级访问和刷新时的渲染压力，并让本进程整体让出资源（阶段 2：用户已确认 S9/S10/S11/S13；S12 待测量后决策）。
- 最终目标：本软件运行时，相机信号到 PLC 的延迟与画面流畅度不劣于“本软件停止运行”时的基线（阈值见 ACCEPTANCE，待确认）。

### 1.3 不做什么

- 不改控制算法：`CrimpHeight.Core` 下全部文件，包括仲裁、R2R、漂移、统计。
- 不改 PLC 协议与握手：寄存器语义、字序、写入→回读校验→置标志的顺序、信号量串行化、重连逻辑。阶段 0 只做测量。
- 不改 CSV 解析语义：`ParseLine`、`ParseTimestamp`、`ExtractLinesFromTail`、列映射、尾部读取策略、句柄打开标志。
- 不对相机目录做任何写入、改名或删除，不引入 FileSystemWatcher（它本身会持有目录句柄并产生通知，属于新的交互面，需单独评估）。
- 不做与本问题无关的重构，不改动现有未提交的散点图改动。
- 不改变刷新间隔、窗口大小等现有配置的默认值和语义。

## 2. 修改文件 / 函数

| 文件 | 函数 / 位置 | 阶段 | 改动要点 |
|---|---|---|---|
| `CrimpHeightAnalyzer/Models/AppSettings.cs` | 新增 `PerformanceSettings` 类，`AppSettings.Performance` 属性 | 0/1/2 | 所有新开关集中在这里；缺省值等于“当前行为”或已确认的默认值（见 §5） |
| `CrimpHeightAnalyzer/Services/SettingsService.cs` | `Load` | 0 | 追加 `settings.Performance ??= new();`，兼容旧版 settings.json |
| `CrimpHeightAnalyzer/Services/CsvDataProvider.cs` | `GetLatestDataAsync`、`GetLatestCsvFile`、`FindCsvInDirectory` | 0/1/2 | 阶段 0：补充细分计时（目录枚举耗时、枚举到的 CSV 个数、size 查询耗时）并通过属性暴露；阶段 1：工作线程进入 / 退出 I/O 后台模式；阶段 2：缓存最新 CSV 路径，按条件重新扫描 |
| `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs` | `RefreshDataAsync` | 0/1/2 | 阶段 0：每次刷新写一行 perf 日志；阶段 1：`PrepareChartData` 移到后台，`progressTimer` 降频；阶段 2：自动刷新不显示加载遮罩 |
| 同上 | `WriteLogToFile`、`Cleanup` | 1 | 改为单写者后台队列，`Cleanup` 时 flush；行格式不变 |
| 同上 | 构造函数（`Settings = _settingsService.Load()` 之后） | 2 | S13：按 `Performance.ProcessPriorityBelowNormal` 设置 `Process.GetCurrentProcess().PriorityClass = BelowNormal`；失败只记日志 |
| `CrimpHeightAnalyzer/MainWindow.xaml` | 加载遮罩的 `Visibility` 绑定 | 2 | 绑定到新属性 `IsLoadingOverlayVisible`，而不是 `IsLoading`（Q4a 已确认） |

- **新增文件：无。** perf 日志写到现有 `log/` 目录（`perf_yyyyMMdd.log`），由程序运行时生成，不是源码文件。
- 后台 I/O 模式需要的 Win32 P/Invoke（`SetThreadPriority` + `THREAD_MODE_BACKGROUND_BEGIN/END`、`GetCurrentThread`）放在 `CsvDataProvider` 现有的 `#region Win32` 内，不新建文件。

### 禁止修改项

- `CrimpHeight.Core/**`
- `CrimpHeightAnalyzer/Services/KeyencePlcAdapter.cs`、`MockPlcAdapter.cs`、`CrimpHeight.Core/Interfaces/*`
- `CsvDataProvider` 中的 `OpenFileNoOplock`（打开标志）、`ReadTailAndParse` 的读取与解析逻辑、`ParseLine`、`ParseTimestamp`、`ExtractLinesFromTail`、`CloseHandleInBackground` 的语义（S10 例外：只允许把 `ParseLine` 回退时间的来源改为已经取到的 `ftLastWriteTime`，解析逻辑不变）
- `MainViewModel` 中的控制和下发链路：`SendPlcCommandsAsync`、`ComputeHeadPlcValue`、`OnHeadAdjustmentSent`、`ManualAdjustAsync`、`SendHeartbeatAsync`、`PollRecipeAsync`、`SwitchRecipeAsync` 的业务语义
- 现有 settings 键的名称和语义；`recipe_config.json` 的结构
- 现有未提交的散点图改动；`bin/` 下的 `HslCommunication.dll`。**禁止执行 `dotnet clean` 或删除 bin/obj。**
- `MainWindow.xaml` 中 `AllowsTransparency` 和窗口外观（S12 决策门：T3 测量证实 H2 且用户再次确认之前不得修改）

## 3. 实施步骤（每步都有编译检查点）

> 编译命令（项目根目录执行）：`dotnet build CrimpHeightAutoAdjustment.slnx -c Debug`
> 本机已有 .NET SDK 10.0.400，项目目标框架为 net8.0-windows。**基线是否可编译尚未验证**，见 S0。

> **构建与部署门（R1 裁决）**
> - **构建 A（诊断构建）** = S0～S3：行为与现状一致，只多 perf 日志。交付现场跑 T0/T1/T3（T2 可选）。
> - **构建 B** = 构建 A + S5～S11、S13：**只有在用户审阅构建 A 的测量结论之后才能部署**。S4 期间开发可以继续写 S5 之后的代码（测量门是部署门，不是编码门）。
> - 如果触发 §6 的停止条件 3 或 4：不部署构建 B，回报用户。

### 阶段 0：诊断（不改变行为，可直接开工）

- **S0 基线**：在改动前编译一次。记录编译结果和现有警告数，作为对照。
  - ✅ 检查点 C0：编译成功。如果基线就编译失败，**停止**并报告，不要“顺手修复”。
- **S1 配置模型**：新增 `PerformanceSettings`（字段见 §5），在 `AppSettings` 中挂载，`SettingsService.Load` 补 `??= new()`。
  - **无效值归一化**（R11 裁决）：新增 `PerformanceSettings.Normalize()`，在 `Load` 中 `??= new()` 之后调用。规则：`ProgressTextIntervalMs < 250` → 250；`NewFileMaxLatencySeconds ≤ 0` → 60；被修正的字段名记入 `[JsonIgnore]` 列表，同时输出 `Debug.WriteLine`。`Settings` 只通过 `Load` 赋值（构造函数、取消设置面板），所以在这里归一化可以覆盖所有入口。使用这些值的地方仍然要兜底：S7 写 `Math.Max(250, ...)`，S9 的阈值按 `max(0, ...)` 计算。
  - ✅ C1：编译通过。删掉 settings.json 中的 `Performance` 段后启动能正常加载，且使用默认值（AC-R05）；写入非法值后启动，按上述规则修正（AC-R08）。
- **S2 CSV 细分计时**：在 `GetLatestDataAsync` 内对 `GetLatestCsvFile` 拆出两段计时：目录定位和 CSV 枚举/排序，并记录本次枚举到的 CSV 个数；在 `ReadTailAndParse` 中把 `GetRealFileSize` 单独计时。新增只读属性 `LastReadMetrics`（结构化：locateDirMs、enumCsvMs、csvCount、openMs、sizeMs、readMs、parseMs、bytesRead、fileChanged、usedFallbackTs），**原有 `LastReadDiagnostics` 字符串保持不变**。
  - ✅ C2：编译通过；对相同 CSV 的读取结果与基线一致（AC-D01）。
- **S3 每次刷新写 perf 日志**：`RefreshDataAsync` 每次刷新结束时（包括无数据和异常路径）追加一行 `log/perf_yyyyMMdd.log`，字段为：刷新序号、开始时间（HH:mm:ss.fff）、各阶段耗时（csv 各子项、analysis、plcMs、本次 PLC 事务是否发生、chartPrepareMs、uiMs、totalMs）、`IsRunning` 和自动/手动来源。写入走 S3 新建的静态后台队列 `Channel<LogEntry>`（`LogEntry(FilePath, Line)`，见 S5）；构建 A 中只有 perf 日志使用这个队列，`WriteLogToFile` 到 S5 才接入。只统计通过入口守卫的刷新，统一在 `finally` 中写入。`Performance.EnablePerfLog` 默认 `true`。
  - **记录类型**（R12 裁决）：每行第一个字段是 `type=refresh` 或 `type=config`。`type=config` 只在 `MainViewModel` 构造函数中 `Settings = _settingsService.Load()` 之后写一次，内容为全部 Performance 生效值和 `NormalizedFields`（为空时写 `normalized=none`）。取消设置面板重新 Load 时不再写 config 行，只输出 `Debug.WriteLine`。`EnablePerfLog=false` 时不写 perf 日志；如果有字段被修正，通过 `WriteLogToFile` 在 `plc_*.log` 写一行 `[配置] Performance 字段已修正: <字段列表>`。
  - ✅ C3：编译通过；每次刷新恰好一行 `type=refresh`，字段完整；启动时有一行 `type=config`（AC-N01、AC-R08）。
- **S4 现场测量**（不改代码）：用构建 A 按 ACCEPTANCE §3 采集 T0/T1/T3（T2 可选），判断 H1/H2/H3。**这是构建 B 部署的前置条件**；S4 期间可以继续开发，但不得部署构建 B。

### 阶段 1：不改变功能结果的减压（默认开启，可直接开工）

- **S5 日志后台化**（R5 裁决后修订）：`WriteLogToFile` 改为向 S3 建立的同一个 `Channel<LogEntry>`（`readonly record struct LogEntry(string FilePath, string Line)`，无界、单读者）投递；后台任务按批、按目标文件分组追加写入；行格式、文件名和目录不变。关闭语义：`Cleanup` 必须幂等（`MainWindow.Closing` 和 `App.OnExit` 都会调用，第二次直接返回）；完成写端后最多等待 1s，超时就放弃剩余条目并退出，不阻塞关闭。
  - ✅ C5：编译通过；日志行的顺序和内容与改动前一致（AC-R03）；关闭程序后日志末行齐全（AC-E04）。
- **S6 图表预计算移出 UI 线程**：在 UI 线程上先拍下 `PrepareChartData` 所需的配置快照（`WindowSize`、`UpperLimit/LowerLimit`），再 `await Task.Run(() => PrepareChartData(snapshotData, snapshot))`；`ApplyChartData` 仍在 UI 线程执行。`_currentData` 需要传入副本或不可变引用，避免与后续刷新并发修改。`PrepareChartData` 内部调用的 `ComputeRobustTrendRange` 也读取 `Settings.Controller`，快照必须一并传入；两个函数体除了把 `Settings.Controller.X` 换成快照字段之外，不得有其他改动（R4 裁决：AC-D02 改为差异审查 + UI 可观察量对比）。
  - ✅ C6：编译通过；同一份数据前后计算出的 ChartData 数值逐项一致（AC-D02）。
- **S7 进度文本降频**：`progressTimer` 间隔由 250ms 改为 `Performance.ProgressTextIntervalMs`，默认 1000ms（仅影响刷新期间“已耗时 x 秒”文字的更新频率）。
  - ✅ C7：编译通过。
- **S8 CSV 工作线程 I/O 后台模式**：在 `GetLatestDataAsync` 的 `Task.Run` 内部，`try` 起始处调用 `SetThreadPriority(GetCurrentThread(), THREAD_MODE_BACKGROUND_BEGIN)`，`finally` 中**必须**调用 `THREAD_MODE_BACKGROUND_END`，避免把降级状态的线程还给线程池。调用失败只记日志，不抛异常。由 `Performance.CsvBackgroundIoMode` 控制，默认 `true`。效果是降低本进程 CSV / 目录 I/O 的 I/O 优先级和内存优先级，不改变读取结果。
  - ✅ C8：编译通过；开关开/关两种情况下读取结果一致（AC-D01）；perf 日志的 csv 耗时在可接受范围（AC-B05）。

### 阶段 2：会改变行为（用户已确认 S9/S10/S11/S13；S12 待测量后决策）

> 阶段 2 的每一项都有开关，同一个构建可以通过修改 settings.json 做 A/B 测量，见 ACCEPTANCE §3。

- **S9（对应 H1）缓存最新 CSV 路径**（R2 裁决后修订）：`CsvDataProvider` 记住上次选中的 CSV 路径。只有满足以下任一条件才重新扫描目录：
  1. 本地日期变化（跨天）；
  2. 缓存文件不存在（`GetRealFileSize` 返回 -1），此时在**同一次刷新内**立即重扫；
  3. 距上次扫描的时间 ≥ `max(0, Performance.NewFileMaxLatencySeconds − RefreshIntervalSeconds)`（`NewFileMaxLatencySeconds` 默认 60，Q2=B）；
  4. 实例新建（路径或配方切换时已经会 new，天然满足）。

  原来的“空闲 60s 不增长”和“强制 300s”两个条件**作废**：前者在旧文件持续增长时发现不了新文件，两者都无法保证 60s。
  时限定义（R8 裁决后修订）：从新 CSV 出现在目录中，到 `LastUsedFilePath` 切换，≤ `max(NewFileMaxLatencySeconds, RefreshIntervalSeconds)`，即 **S9 不得比现状更晚**。刷新间隔 ≤60s 时保证 ≤60s（Q2=B）；刷新间隔 >60s 时阈值为 0，每次刷新都扫描，与现状一致。**不给 `RefreshIntervalSeconds` 增加上限校验**（那样会改变现有配置语义），**也不增加独立的扫描定时器**（会增加对相机目录的访问）。前提是每次刷新都在一个刷新间隔内完成；如果某次 Tick 因 `IsLoading` 落空，最坏会多一个间隔，perf 日志中要能看出来。
  Q2 的“60s”是绝对上限还是相对现状，存在歧义（R10 裁决）：在 R >60s 时，现状本来就做不到 60s，S9 在该区间与现状等价，不引入退化，因此**不阻塞**构建 B 部署或 S9 验收。如果产线 R >60s，要在交付报告中向用户说明“识别时延 ≤R，与现状相同”；用户如需绝对的 60s，需另行决定是否缩短刷新间隔（**待确认**，超出本计划范围）。
  收益取决于产线刷新间隔（**待确认**）：间隔 20s 时扫描次数约减半；间隔 ≥60s 时每次刷新都会扫描，S9 没有收益，H1 主要靠 S8。如果需要更大收益，可以请用户放宽 60s 时限（不阻塞）。
  新参数通过构造函数的可选参数传入，`MainViewModel` 中三处构造调用都要更新。按 Q2=B：`Performance.CachedFileLocate` 默认 `true`。
  - ✅ C9：编译通过；跨天、轮转、删除、新增文件各场景通过（AC-B01～B04）。
- **S10（对应 H1，次要）时间戳回退缓存**：`ParseLine` 回退用的 `File.GetLastWriteTime(path)` 改为同一文件每次刷新最多取一次（当前已经通过 `ref` 实现每次刷新一次），或者直接复用 `GetRealFileSize` 时已经拿到的 `ftLastWriteTime`，省掉一次路径访问。注意 `GetFileAttributesEx` 返回的是 UTC FILETIME，需要换算成本地时间，保持与 `File.GetLastWriteTime` 相同的语义。
  - ✅ C10：编译通过；回退时间戳与改动前一致（误差 ≤1s，AC-D03）。
- **S11（对应 H2）自动刷新不显示遮罩**：新增 `IsLoadingOverlayVisible`。定时器触发的刷新为 `false`，手动“重置并刷新”为 `true`（Q4a 已确认；开关 `ShowOverlayOnAutoRefresh` 默认 `false`）；`IsLoading` 自身的语义（防重入、禁用手动按钮）保持不变。
  - ✅ C11：编译通过；自动刷新时不出现遮罩，按钮禁用逻辑不变（AC-R04）。
- **S12（对应 H2，仅在 H2 被证实时做）关闭分层窗口**：`AllowsTransparency=False`，用 `WindowChrome` 保留自绘标题栏和拖拽/缩放。这会改变外观（圆角透明边）。**决策门**：只有当 T3（窗口最小化）测量显示尖峰消失或显著减弱时，才带数据请用户再次确认；否则不做。
- **S13 进程优先级 BelowNormal**（Q5 已确认）：在 `MainViewModel` 构造函数加载配置之后，按 `Performance.ProcessPriorityBelowNormal`（默认 true）设置进程优先级。开关为 false 时不主动修改（保持系统默认的 Normal）；开关变更需要重启程序才生效。
  - ✅ C13：编译通过；任务管理器显示“低于正常”；AC-R01、AC-R02 通过；刷新耗时满足 AC-B05。

### 提交粒度（建议，由用户决定是否提交）

阶段 0 一个提交，阶段 1 每个 S5～S8 各一个提交，阶段 2 每项各一个提交。每个提交都要能独立回滚。现有的散点图未提交改动**不要混进这些提交**，按路径分块暂存（`git add -p`），或者先让用户单独处理。

## 4. 边界情况与风险

| # | 场景 / 风险 | 影响 | 应对 |
|---|---|---|---|
| E1 | 午夜跨天：相机创建新日期目录和新 CSV | S9 如果没识别到，会继续读旧文件，导致数据陈旧、控制失效 | S9 条件 1 和条件 3 双保险；AC-B01 |
| E2 | 相机同一天内轮转或新建 CSV（Q2=B：会发生，60s 内识别可接受） | 同上 | S9 的条件 3（按刷新间隔折算、保证 ≤60s 的周期重扫）；AC-B02 的 (a)(b) 两种情况 |
| E3 | 缓存的 CSV 被删除或改名 | 缓存句柄仍然有效（共享了 DELETE），但路径大小返回 -1 | 现有逻辑会 Invalidate；S9 把 -1 视为需要重新扫描；AC-B03 |
| E4 | I/O 后台模式使读取变慢（相机持续写入时被降级排队） | 刷新耗时变长，但不影响结果 | perf 日志监控；AC-B05 设上限；超限则关闭开关 |
| E5 | `THREAD_MODE_BACKGROUND_END` 未执行 | 线程池线程永久降级 | `finally` 中执行；END 失败要记日志；代码评审必须检查 |
| E6 | `Task.Run` 计算图表时 `_currentData` 被下一轮刷新替换 | 数据竞争 | 防重入（`IsLoading`）已经保证串行；仍然传入不可变快照 |
| E7 | 日志队列积压、程序崩溃或关闭等待超时 | 末尾日志丢失（以前的同步写不会丢） | 每批写盘；`Cleanup` 幂等，最多等 1s，超时放弃剩余条目（R5 裁决）；属于可接受的行为差异（已在 §5 默认值注明） |
| E8 | 旧版 settings.json 缺少 `Performance` 段 | 空引用 | `??= new()`；AC-R05 |
| E9 | `SaveSettings` 会把新段写回 settings.json | 配置文件多出一段 | 预期行为；旧版程序读取时会忽略未知字段（System.Text.Json 默认行为） |
| E10 | 关闭遮罩后用户看不出刷新在进行 | 体验变化 | 底部状态栏 `StatusText` 仍然显示“正在加载数据...” |
| E11 | 阶段 0 测量本身（perf 日志）增加写盘 | 每 20s 写一行，影响可以忽略 | 日志写在程序目录下；可通过 `EnablePerfLog=false` 关闭 |
| E12 | 卡顿根因不在本软件（例如杀毒定时扫描、相机自身问题） | 优化无效 | 阶段 0 要做“本软件停止运行”的基线对照；如果相关性不成立，触发停止条件 |
| E13 | `dotnet clean` 删除未纳管的 HslCommunication.dll | 无法编译 | 禁止 clean；在交接文档中写明 |
| E14 | 产线相机目录位于本地 HDD（Q1=B） | 随机寻道放大 H1 | S8 和 S9 是主要手段；如果阶段 2 之后仍不达标，可考虑在部署层面把相机输出改到 SSD（不在本代码计划范围，需用户决策） |
| E15 | 进程设为 BelowNormal 后，CPU 繁忙时本软件的 UI 和定时器被推迟 | 心跳（2s）和轮询（3s）抖动，刷新变慢 | 心跳间隔远大于可能的推迟；用 AC-R02 和 perf 日志监控；不达标时通过配置关闭（需重启） |
| E16 | 手工编辑 JSON，在 `Performance` 中写入非法数值（0、负数） | `progressTimer` 在 `try` 之前创建，负值会抛异常，导致 `IsLoading` 卡死、心跳不恢复；值为 0 时定时器连续触发，造成 UI 繁忙 | `Load` 中统一 `Normalize()`，使用处再 `Math.Max` 兜底（R11）；AC-R08。JSON 类型错误导致整份配置回到默认值，属于现有行为，不改 |

## 5. 默认值（已按用户答复更新）

| 开关（`Performance.*`） | 默认值 | 是否改变行为 | 说明 |
|---|---|---|---|
| `EnablePerfLog` | true | 否（只新增一个日志文件） | 阶段 0 |
| `ProgressTextIntervalMs` | 1000 | 仅影响刷新期间进度文字的更新频率 | 视为不影响功能的 UI 细节，采用默认值 |
| `CsvBackgroundIoMode` | true | 否（只影响调度，不影响结果） | 阶段 1 |
| 日志后台化 | 启用（不设开关） | 崩溃或关闭等待超时（1s）时，尚未写盘的条目可能丢失 | 采用默认值并在此注明（R5） |
| `CachedFileLocate` | **true**（Q2=B） | 是 | 阶段 2 S9 |
| `NewFileMaxLatencySeconds` | 60（Q2=B） | 是 | 只在 `CachedFileLocate=true` 时生效；原 `RescanWhenIdleSeconds` / `ForceRescanSeconds` 已作废（R2） |
| `ShowOverlayOnAutoRefresh` | **false**（Q4a=A） | 是 | 阶段 2 S11；手动重置仍显示遮罩 |
| `ProcessPriorityBelowNormal` | **true**（Q5=A） | 是（只影响调度） | 阶段 2 S13，需重启生效 |
| AllowsTransparency | 不改 | — | S12 决策门（Q4b=测量后再定） |

## 6. 验证、停止与回滚

完整 AC 与验证步骤见 `docs/ACCEPTANCE.md`。要点如下：

- 编译：每个检查点都执行 `dotnet build CrimpHeightAutoAdjustment.slnx`。
- 项目**没有自动化测试工程**（已检索确认）。数据完整性靠“同一份 CSV 快照，改动前后的 perf 日志 / 诊断输出对比”验证；控制链路回归在 Mock PLC 模式下对比 `log/plc_*.log` 的事务序列。
- 现场：用“本软件停止 / 本软件运行 / 运行且各开关逐项开启”做 A/B 对照，采集 PLC 程序记录的相机信号时间戳（Q6-measure=A）、ProcMon、PerfMon/WPR。

**停止条件**：

1. 基线编译失败（C0）；
2. 任一步骤导致数据完整性 AC（AC-D*）不一致；
3. 构建 A 的测量（T0/T1）显示相机卡顿与本软件刷新**没有时间相关性**（例如本软件停止后卡顿依旧）。这时不部署构建 B，已写的 S5～S13 代码保持未合并，回报用户重新分析；
4. 阶段 0 显示主因是 PLC 通信（H3）。这时停止，另立计划，因为改 PLC 时序超出本计划范围。

**回滚**：每个改动都有对应提交和开关。行为类改动优先通过配置关闭开关回滚（无需重新部署代码），其次 `git revert` 对应提交。部署前保留上一版可执行文件和 settings.json 的备份。

## 7. 影响范围

- 代码：`CsvDataProvider`、`MainViewModel`（刷新编排、日志、图表预计算）、`AppSettings`/`SettingsService`、`MainWindow.xaml`（仅 S11）。
- 运行时：新增 `log/perf_*.log`；settings.json 新增 `Performance` 段。
- 不影响：控制算法、PLC 协议与握手、CSV 解析结果、配方结构。

## 8. 用户确认记录

全部问题已于 2026-11-24 答复，结论见 §0.1。下表保留原问题，便于追溯。

| # | 问题 | 影响的步骤 | 原默认（已被答复取代） |
|---|---|---|---|
| Q1 | 产线的相机 CSV / NG 图片目录在哪个盘：本地 SSD、本地 HDD、网络盘，还是 OneDrive/同步目录？是否有杀毒/EDR 实时扫描该目录？ | 判断 H1 的权重；是否需要部署层面调整 | 按 H1 为主假设推进阶段 0 |
| Q2 | 相机生成 CSV 的规则：是否每天一个 `Data_F2_yyyyMMdd.csv`？同一天会不会新建或轮转？换型或程序号变化时会不会新建文件？新文件生效的最大可接受延迟是多少？ | S9 能否启用，以及 60s/300s 的取值 | S9 不启用 |
| Q3 | 产线配置：`UseRealPlc`、`EnergySavingMode`、心跳/轮询是否启用？卡顿是**每次刷新都发生**，还是只在发生 PLC 写入（Drift）时发生？ | 判断 H3 的权重 | 阶段 0 测量后判断 |
| Q4a | 自动刷新时不再显示全屏“正在加载...”遮罩（手动重置时仍显示），是否接受？ | S11 | 不改 |
| Q4b | 如果证实是渲染问题，是否接受去掉窗口透明圆角（`AllowsTransparency=False`，外观略有变化）？ | S12 | 不改 |
| Q5 | 是否接受把本软件进程优先级设为“低于正常”？ | S13 | 不改 |
| Q6 | 验收阈值与测量方式：PLC 侧如何记录相机信号时间戳？可接受的增量是多少（默认：本软件运行时，相机信号延迟 p99 相对“本软件停止”的基线增加 ≤20ms，且 2h 内无可感知的画面冻结）？ | AC-N03 | 采用默认值 |
| Q7 | 工作树里未提交的“散点显示”改动是否保留为本次开发的基线？ | 提交组织 | 保留，不混入本次提交 |

PLAN 状态：**PLAN UPDATED**（R1～R7 裁决见 `docs/review/2026-11-24_decision.md`，R8～R9 裁决见 `docs/review/2026-11-24_decision_2.md`，R10～R11 裁决见 `docs/review/2026-11-24_decision_3.md`，R12 裁决见 `docs/review/2026-11-24_decision_4.md`；S12 保留测量后决策门）
