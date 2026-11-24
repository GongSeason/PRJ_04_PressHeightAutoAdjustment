# ACCEPTANCE：刷新数据影响相机检测软件

- 日期：2026-11-24
- 对应计划：`docs/PLAN.md`（步骤编号 S0～S13）
- 约定：每条 AC 写明“判定方式”。用户已确认（2026-11-24）：Q6=A（p99 增量 ≤20ms、2h 内无可感知冻结）；PLC 程序可以为相机信号打时间戳。
- 2026-11-24 按 `docs/review/2026-11-24_decision.md` 修订：AC-N01、N03、B02、E04、R02、D01、D02、D04、D05，以及 §2 第 5 条、§3。
- 2026-11-24 按 `docs/review/2026-11-24_decision_2.md` 修订：AC-N03（只用指标 L 判定）、AC-B02（时限 ≤ max(60s, 刷新间隔)）、§3 PLC 采集项。
- 2026-11-24 按 `docs/review/2026-11-24_decision_3.md` 修订：AC-B02 的 90s 用例改为“与现状等价”判定；新增 AC-R08（Performance 配置非法值）。
- 2026-11-24 按 `docs/review/2026-11-24_decision_4.md` 修订：AC-N01 只统计 `type=refresh` 行；AC-R08 按 `EnablePerfLog` 的开/关分别判定。

---

## 1. 验收标准（Given / When / Then）

### 正常（N）

- **AC-N01 perf 日志完整**（S3）
  Given `Performance.EnablePerfLog=true`，程序处于自动运行
  When 连续发生 10 次**通过入口守卫**的刷新（守卫条件：设置面板未打开、不在加载中、provider 非空；10 次中成功、无数据、异常各至少 1 次，可以通过改路径或删文件制造）
  Then `log/perf_yyyyMMdd.log` 恰好新增 10 行 `type=refresh` 记录（被守卫直接返回的调用、因 `IsLoading` 而落空的 Tick 都不计行；启动时的 `type=config` 记录不计入，R12），每条 refresh 记录都包含刷新序号、开始时间（毫秒精度）、locateDirMs、enumCsvMs、csvCount、openMs、sizeMs、readMs、parseMs、analysisMs、plcMs、plcTxn（是/否）、chartPrepareMs、uiMs、totalMs、触发来源（auto/manual）。
  判定：逐行检查字段数和非空。

- **AC-N02 刷新功能不变**（全部步骤）
  Given Mock PLC、同一份 CSV 快照
  When 执行一次刷新
  Then 界面上两个压头的状态、偏差、截尾均值、标准差、调整量文本，以及状态栏的记录条数，与基线版本完全一致。
  判定：两版程序对同一快照截图或记录的数值逐项对比。

- **AC-N03 不影响相机（最终目标）**（阶段 1/2 部署后）
  Given 产线机器，相机正常生产，本软件自动运行（真实 PLC 配置与产线一致）
  When 连续运行 ≥2h（全部开关为默认值）
  Then 指标 L（定义见 §3：PLC 在自身时钟上测得的“触发 → 收到相机信号”间隔，或者相机源端提供的序号/时间标记；L 的可测性待确认）的 p99 相对“本软件停止运行”的 2h 基线增量 ≤ 20ms。**AC-N03 只能用 L 判定**（R9 裁决）：现场无法取得 L 时，本条记为“不可判定（BLOCKED-验收）”并回报用户，**不得**用备选抖动指标判为通过。另外还要求相机画面没有可感知的冻结（判定：操作员记录；如果相机软件有帧时间日志就一并核对，有没有这种日志待确认）；与本软件刷新时刻对齐后，没有周期性的延迟尖峰。
  判定：对齐 perf 日志时间戳与 PLC 时间戳，统计刷新时刻 ±1s 窗口内与其他时段的延迟分布。

- **AC-N04 进程优先级**（S13）
  Given `ProcessPriorityBelowNormal=true`
  When 启动程序
  Then 任务管理器或 Process Explorer 显示本进程优先级为“低于正常”；开关为 false 并重启后显示“正常”。

- **AC-N05 自动刷新不显示遮罩**（S11）
  Given `ShowOverlayOnAutoRefresh=false`，处于自动运行
  When 定时刷新
  Then 不出现全屏“正在加载...”遮罩，底部状态栏显示“正在加载数据...”；点击“重置并刷新”时遮罩照常显示。

### 边界（B）

- **AC-B01 跨天切换**（S9 启用时必测；未启用时作为回归）
  Given 当前读取 `D1/Data_F2_D1.csv`，模拟相机在新日期目录 `D2/` 中创建 `Data_F2_D2.csv` 并开始追加
  When 跨过本地 0 点后的第一次刷新（S9 启用时：不晚于第一次刷新）
  Then 读取的文件切换为 `D2` 的 CSV；切换后数据只来自新文件；旧句柄在后台关闭，没有异常。
  判定：perf 日志里的 `fileChanged=true`，以及 `LastUsedFilePath`。

- **AC-B02 同日新建 CSV**（S9，R2 修订）
  Given `CachedFileLocate=true`、`NewFileMaxLatencySeconds=60`，刷新间隔分别取 20s、60s 和 90s，各测一次（R8 修订：判定上限 = max(60s, 刷新间隔)，即 20s/60s 时 ≤60s、90s 时 ≤90s；90s 用例按“与现状等价”判定：时延 ≤90s，并且每次刷新都重扫。它不代表满足 Q2 的绝对 60s 解释，交付报告中要向用户说明（R10 裁决））
  When 同一日期目录下出现一个更新的 CSV，分两种情况：(a) 旧文件停止增长；(b) 旧文件**仍在增长**
  Then 两种情况下，从新文件创建到 `LastUsedFilePath` 切换的耗时都不超过上述判定上限；如果期间有 Tick 因 `IsLoading` 落空，perf 日志中要能看出来，并据此单独判定。切换之前读旧文件，没有报错。
  判定：测试脚本打印新文件的创建时间，对照 perf 日志中 `fileChanged=true` 那一行的开始时间。

- **AC-B03 缓存文件被删除或改名**（S9 / 回归）
  Given 正在读取的 CSV 被删除或改名
  When 下一次刷新
  Then 不崩溃；本次显示“未找到数据”或者已经切换到其他文件；再下一次刷新能读到新文件（如果存在）。
  判定：手工操作 + 观察状态栏和日志。

- **AC-B04 CSV 为空或末行不完整**（回归）
  Given CSV 只有表头 / 末行正在写一半
  When 刷新
  Then 行为与基线一致（空文件提示“未找到压接数据”；半行被跳过）。
  判定：与基线版本对比。

- **AC-B05 后台 I/O 模式的耗时上限**（S8）
  Given `CsvBackgroundIoMode=true`，相机正在写入
  When 连续 100 次刷新
  Then perf 日志中 csv 总耗时 p95 ≤ 500ms 且 max ≤ 2000ms（计划默认值，用户未单独规定）；如果超出，关闭开关后重测，并记录对比数据。
  判定：统计 perf 日志。

- **AC-B06 WindowSize 变化**（回归）
  Given 把 WindowSize 从 150 改为 50 和 200
  When 保存设置并刷新
  Then 读到的条数不超过 WindowSize，结果与基线一致。

### 异常（E）

- **AC-E01 监视目录不存在**：Given MonitorPath 指向不存在的目录 When 刷新 Then 状态栏显示“未找到数据 | 路径: …”，`IsLoading` 复位，下一次定时刷新照常执行，perf 日志记一行。
- **AC-E02 PLC 断开（真实 PLC 测试环境，或者把 IP 改成不可达）**：行为与基线一致（跳过写入、记日志、按 ReconnectIntervalMs 重连），刷新不会卡住；perf 日志中 plcMs 有记录。
- **AC-E03 日志目录不可写**：Given `log/` 无写权限 When 刷新 Then 程序不崩溃，刷新正常完成（与基线“日志失败不影响主流程”一致）。
- **AC-E04 关闭时 flush**（S5，R5 修订）：
  (a) 磁盘正常：Given 刚刚产生了日志 When 点击关闭 Then 关闭时的日志等待 ≤1s，`plc_*.log` 和 `perf_*.log` 的最后一行是关闭前最后一条消息。
  (b) 磁盘写入受阻（例如把日志文件设为只读，或用其他进程独占打开来模拟）：日志等待 ≤1s 后放弃剩余条目，程序正常退出，不崩溃。
  (c) `Cleanup` 被调用两次（`Closing` + `App.OnExit`）：第二次不抛异常，也不再等待。
- **AC-E05 SetThreadPriority 失败**（S8）：模拟或强制调用失败时，读取结果不受影响，只记一条日志，不抛异常。

### 回归（R）

- **AC-R01 PLC 事务序列不变**：Given Mock PLC 与同一组触发条件（包括 Drift 单头、Drift 双头、手动写入）When 执行 Then `log/plc_*.log` 中事务的类型和顺序与基线完全一致（不比较时间戳）。真实 PLC 环境（如果有）：握手读 → 写 → 回读 → 置标志的顺序不变。
- **AC-R02 心跳暂停与恢复**（R6 修订）：刷新期间**不启动新的**心跳 Tick；刷新开始前已经在执行的心跳写入允许完成（这是现有行为，不做改动）；刷新结束后恢复（成功、无数据、异常三条路径）。判定：在 Mock 模式下看 plc 日志里“写入 D…”的时间线，行为模式与基线版本一致。
- **AC-R03 日志内容等价**（S5）：同一操作序列下，改动前后 `plc_*.log` 去掉时间戳后逐行一致。
- **AC-R04 IsLoading 语义不变**（S11）：自动刷新期间，手动按钮（推荐/写入/重置）仍然禁用；设置面板在刷新期间仍然拒绝保存；S11 启用后自动刷新不显示遮罩，手动重置时显示（Q4a=A）。
- **AC-R05 旧配置兼容**：Given 一个没有 `Performance` 段的 settings.json When 启动 Then 正常加载并使用默认值；点击“保存设置”后文件里新增 `Performance` 段，其他键的值不变。
- **AC-R06 配方切换**：程序号变化 → 路径切换 → 新 `CsvDataProvider` 读到新路径的数据（S9 的缓存随实例重建而清空）。
- **AC-R07 散点图功能**：现有未提交的“散点显示”功能在改动后仍然可以正常切换。
- **AC-R08 Performance 配置非法值**（S1/S7/S9，R11）：Given settings.json 的 `Performance` 段分别写入 (a) `ProgressTextIntervalMs=0`、(b) `ProgressTextIntervalMs=-5`、(c) `NewFileMaxLatencySeconds=0`、(d) `NewFileMaxLatencySeconds=-1` When 启动并连续自动刷新 3 次 Then 程序不崩溃；每次刷新都正常结束（`IsLoading` 复位，心跳恢复）；生效值分别为 250、250、60、60；被修正的字段名按 `EnablePerfLog` 分两种记录（R12）：`EnablePerfLog=true` 时，perf 日志的 `type=config` 行列出被修正的字段名（没有修正时为 `normalized=none`）；`EnablePerfLog=false` 时，不产生 perf 日志，`plc_*.log` 中有一行 `[配置] Performance 字段已修正: ...`。判定：观察界面状态、perf 日志和 plc 日志；两种开关状态都要测。

### 数据完整性（D）

- **AC-D01 读取结果逐条一致**（S2/S8/S9，R4 修订）：Given 同一份 CSV 快照（复制到独立目录，不追加）When 用**仓库外的临时 harness**（§2 第 5 条）分别对基线版和新版 `CsvDataProvider` 调用 `GetLatestDataAsync(WindowSize)`，逐条导出 Timestamp、RecordedAt、Head1Height、Head2Height Then 两份导出逐行完全一致；`CsvBackgroundIoMode` 与 `CachedFileLocate` 的开/关组合都要比对。
- **AC-D02 图表数据一致**（S6，R4 修订）：(a) 代码差异审查：`PrepareChartData` 和 `ComputeRobustTrendRange` 的 diff 中，除了把 `Settings.Controller.X` 换成快照字段、以及调用点改为 `await Task.Run(...)` 之外，没有任何其他改动；(b) 同一份快照下，界面上的趋势图标题条数、Y 轴范围和直方图形状与基线一致（截图对比）。
- **AC-D03 回退时间戳一致**（S10）：时间列不可解析时，`RecordedAt` 与基线相差 ≤1s，并且是本地时间。
- **AC-D04 对相机目录只读**：Given ProcMon 过滤条件为 Process=CrimpHeightAnalyzer.exe 且 Path 以相机目录开头 When 自动运行 30 分钟 Then 只出现 CreateFile(Read)、QueryDirectory、QueryBasicInformation / QueryNetworkOpen、ReadFile、CloseFile 这类读操作，**没有任何** WriteFile、SetInformation（重命名/删除）或带写权限的 CreateFile。事件名称和 Desired Access 的显示方式以实际使用的 ProcMon 版本为准。
- **AC-D05 访问次数下降**（S9 启用时，R7 修订）：先在构建 A（或 `CachedFileLocate=false`）下按 AC-D04 的过滤条件采集基线，统计每次刷新窗口内本进程在相机目录下 QueryDirectory 类和 QueryInformation 类事件的次数与总 Duration；再在 `CachedFileLocate=true` 下统计同样的数据。Then 未触发重扫的刷新中 QueryDirectory 类事件为 0，整体次数和总 Duration 都低于基线。如果这些事件在基线中根本不可见，记为“无法判定”，并在报告中说明原因。

## 2. 编译与离线验证

1. 编译：项目根目录执行 `dotnet build CrimpHeightAutoAdjustment.slnx -c Debug`，要求 0 错误，且相对 S0 基线不新增警告。**禁止执行 `dotnet clean`**（`bin\Debug\net8.0-windows\HslCommunication.dll` 是未纳管的编译依赖）。
2. 准备快照：把一份真实 CSV 连同目录结构（`<根>/F2/<yyyyMMdd>/Data_F2_<yyyyMMdd>.csv`）复制到独立目录，用于 AC-D01/D02/N02。
3. 模拟相机持续写入（用于 AC-B01/B02/B05/D04，在独立测试目录中执行，**不要指向产线目录**），例如在 PowerShell 中执行：
   `while($true){ Add-Content -Path <测试CSV> -Value "<时间>,12.20,0,12.21,0,0,0"; Start-Sleep -Milliseconds 100 }`
   跨天测试可以手工新建下一日期目录和文件来模拟。
4. Mock PLC 回归：`UseRealPlc=false`；通过调整上下限让状态进入 Drift，触发自动下发；记录 `log/plc_*.log`。
5. 数据对比 harness（AC-D01，R4 裁决）：
   - 在**仓库之外**的临时目录建一个控制台工程（`net8.0-windows`，`<UseWPF>true</UseWPF>`），用 ProjectReference 引用 `CrimpHeightAnalyzer.csproj`，调用 `new CsvDataProvider(path, 0).GetLatestDataAsync(n)` 并逐行输出。
   - 基线版本：`git worktree add <仓库外目录> HEAD`。`CsvDataProvider.cs` 不在散点图未提交改动的范围内，所以 HEAD 版本就是基线。
   - worktree 中没有 `bin\Debug\net8.0-windows\HslCommunication.dll`，需要先把它复制到相同的相对路径，否则基线工程无法编译。
   - harness 分别引用两份工程，输出结果后逐行 diff。harness 和 worktree 都不提交，用完删除（`git worktree remove`）。

## 3. 现场测量方案（S4，阶段 0 的核心）

目标：判断卡顿与本软件刷新的时间相关性，并归因到 H1（文件系统）、H2（渲染）或 H3（PLC）。

| 轮次 | 配置 | 时长 | 目的 |
|---|---|---|---|
| T0 | 本软件**不运行** | ≥2h | 基线：指标 L 的分布（AC-N03 需要 2h 基线） |
| T1 | **构建 A**（S0～S3，行为与现状一致，只多 perf 日志），产线原配置 | ≥30min | 确认相关性 |
| T2（可选） | 构建 A，临时 `UseRealPlc=false`（**需产线负责人同意**：期间没有自动补偿） | ≥30min | 隔离 H3（Q3=每次都卡，所以列为可选） |
| T3 | 构建 A，把本软件窗口最小化 | ≥30min | 隔离 H2，作为 **S12 决策门**的依据 |
| — | **测量门**：用户审阅 T0/T1/T3 的结论之后，才能部署构建 B | — | PLAN §3、§6 |
| T4 | **构建 B**，`CsvBackgroundIoMode=true`、`CachedFileLocate=true`；其余新开关设为旧行为（`ShowOverlayOnAutoRefresh=true`、`ProcessPriorityBelowNormal=false`、`ProgressTextIntervalMs=250`） | ≥30min | 评估 H1 的对策 |
| T5 | 构建 B，全部开关取默认值（PLAN §5） | ≥2h | 判定 AC-N03 |

每轮之间都要**重启本软件**，因为进程优先级开关需要重启才生效。

采集项：

- PLC 侧（R3 裁决）：主指标 **L** = PLC 在自身时钟上测得的“PLC 发出触发 → 收到相机结果信号”间隔。起点和终点都由 PLC 打时间戳，不受 PC 时钟影响；用户说的“约 200ms”推测就是这个量。**待确认**：测量开始前在现场确认 PLC 能同时记录这两个时刻。如果 PLC 只能记录“收到信号”的时刻，改用备选指标：相邻两次收到信号的间隔相对节拍周期的偏差（抖动）。**备选指标只用于 S4 的相关性判断和 H1/H2/H3 归因**（R9 裁决）：瞬时推迟会表现为“一个长间隔 + 一个短间隔”的成对偏差，可以捕捉；但它测不出恒定偏移，因此不能用于 AC-N03 的通过判定。PLC 与 PC 的时钟偏差只用于把 L 和刷新时刻对齐到 ±1s 窗口内；测量开始和结束时各记录一次，精度 ≤100ms 即可。
- 本软件：`log/perf_*.log`。
- 系统：PerfMon（`% Processor Time`（总体、本进程、相机进程）、`PhysicalDisk\Avg. Disk sec/Transfer` 与 `Current Disk Queue Length`（相机目录所在盘）、`GPU Engine\Utilization Percentage`）；出现卡顿时用 WPR 抓取 CPU + Disk I/O + GPU 的 trace，时长约 60s。
- ProcMon：过滤相机目录路径，同时勾选本进程和相机进程，观察相机进程的 WriteFile / CreateFile 的 Duration 是否在本软件刷新时刻显著变长（直接验证 H1）。

判定规则：

- 如果 T1 在刷新时刻出现尖峰、T0 没有 → 相关性成立；否则触发 PLAN 停止条件 3。
- 如果 T2 的尖峰消失 → H3 成立，触发停止条件 4，另立计划。
- 以上判定都要用指标 L（或备选指标）在刷新时刻 ±1s 窗口内的分布，与窗口外的分布对比。
- 如果 T3 的尖峰消失或显著减弱 → H2 成立，带 T1/T3 对比数据请用户确认是否实施 S12。
- 如果 ProcMon 显示相机 I/O 的 Duration 在本软件目录枚举或属性查询期间变长，或 perf 日志的 locate/size 耗时与尖峰同步 → H1 成立，推进 S9/S10。

## 4. 停止与回滚条件

参见 PLAN §6。另外：AC-D* 任一项不通过时，**禁止部署**；AC-N03 在部署后不达标时，可以先通过配置关闭对应开关回滚，如果仍不达标，再部署上一版程序。

## 5. 实施结果（2026-11-24）

> 状态按本次真实执行证据填写。未在本次执行的项保持 NOT TESTED；离线 harness 子集结果不能替代完整 AC。

| AC | 结果 | 证据 / 未验证原因 |
|---|---|---|
| AC-N01 | NOT TESTED | 已实现 `type=config`/`type=refresh` 和 finally 记录；未启动 WPF app 统计 10 次有效刷新及全部字段。 |
| AC-N02 | NOT TESTED | 未执行完整 WPF/Mock PLC 刷新与基线 UI 对比。 |
| AC-N03 | NOT TESTED | 未进行产线 T0/T1/T4/T5、PLC L 和 2h 画面冻结测量。 |
| AC-N04 | NOT TESTED | 未启动 app 在任务管理器核对 Below Normal/Normal。 |
| AC-N05 | NOT TESTED | 未运行 UI 核验自动/手动 overlay。 |
| AC-B01 | NOT TESTED | 未做跨日目录测试。 |
| AC-B02 | NOT TESTED | 临时 harness 只用缩放参数 latency=2s、refresh=1s 测旧文件停止增长分支；未执行要求的 20/60/90s 或旧文件仍增长分支。 |
| AC-B03 | NOT TESTED | 临时 harness 删除缓存 CSV 并放入新 CSV，观察到同次重扫与路径切换；未完成 UI/下一周期检查。 |
| AC-B04 | NOT TESTED | 未测空 CSV/不完整末行。 |
| AC-B05 | NOT TESTED | 未连续运行 100 次并统计 p95/max。 |
| AC-B06 | NOT TESTED | 未在 UI 验证 WindowSize=50/200。 |
| AC-E01～AC-E05 | NOT TESTED | 未运行目录异常、真实 PLC 断连、日志权限/独占、关闭及 P/Invoke 故障测试。 |
| AC-R01～AC-R04 | NOT TESTED | 未运行 Mock PLC 事务顺序、心跳和 UI 状态回归。 |
| AC-R05 | NOT TESTED | 临时 harness 验证旧 JSON 加载保存成功、MonitorPath 与 ScanDays 保留且新增 Performance；未比较全部设置键。 |
| AC-R06～AC-R07 | NOT TESTED | 未做配方切换或散点图 UI 测试；散点图既存代码保留。 |
| AC-R08 | NOT TESTED | 临时 harness 验证 Load 将间隔 0/时限 -1 归一化为 250/60，NormalizedFields 正确且不序列化；未验证 perf 开关的日志分支、WPF 状态、心跳和三次连续刷新。 |
| AC-D01 | NOT TESTED | 基线 HEAD 与当前默认开关配置各返回 150 条，逐条 Timestamp/RecordedAt/双头高度输出完全相同；未完成开关组合矩阵。 |
| AC-D02 | NOT TESTED | 代码已使用配置快照；未进行 UI 图表数据/截图对比。 |
| AC-D03 | PASS | 临时 harness 对坏时间戳数据断言 `UsedFallbackTs=true`，RecordedAt 与 `File.GetLastWriteTime` 完全相等：`2026-11-24T10:15:30.0000000+08:00`。 |
| AC-D04～AC-D05 | NOT TESTED | 未用 ProcMon 对相机目录进行 30 分钟只读及访问次数/Duration 对比。 |

逐步构建检查结果与完整手动测试步骤见 `docs/handoff/2026-11-24_implementation-report.md`。

## 5. 实施结果（2026-11-24）

> 状态按本次真实执行证据填写。未在本次执行的项保持 NOT TESTED；离线 harness 子集结果不能替代完整 AC。

| AC | 结果 | 证据 / 未验证原因 |
|---|---|---|
| AC-N01 | NOT TESTED | 已实现日志类型和 finally 记录；未启动 WPF app 统计 10 次刷新并核验字段。 |
| AC-N02 | NOT TESTED | 未运行完整 UI/Mock PLC 刷新对比。 |
| AC-N03 | NOT TESTED | 未进行产线 T0/T1/T4/T5、PLC L 和 2h 画面冻结测量。 |
| AC-N04 | NOT TESTED | 未启动 app 在任务管理器检查优先级。 |
| AC-N05 | NOT TESTED | 未启动 UI 核验自动/手动遮罩。 |
| AC-B01 | NOT TESTED | 未做跨日目录切换。 |
| AC-B02 | NOT TESTED | 临时 harness 只用缩放参数 latency=2s、refresh=1s 测了停止增长分支；未覆盖 20/60/90s 实际间隔及旧文件仍增长分支。 |
| AC-B03 | NOT TESTED | 临时 harness 删除缓存 CSV 并放入新文件，观察到同次刷新 `rescan=true`、`fileChanged=true` 和新路径；未完整验证 UI/下一周期。 |
| AC-B04 | NOT TESTED | 未测空 CSV/不完整行。 |
| AC-B05 | NOT TESTED | 未连续 100 次刷新统计 CSV p95/max。 |
| AC-B06 | NOT TESTED | 未在 UI 改 WindowSize=50/200 回归。 |
| AC-E01～AC-E05 | NOT TESTED | 未进行目录异常、PLC 断开、日志不可写/锁定、重复 Cleanup、P/Invoke 失败等运行测试。 |
| AC-R01～AC-R04 | NOT TESTED | 未运行 Mock PLC 事务、心跳及 UI 状态对比。 |
| AC-R05 | NOT TESTED | 临时 harness 验证旧 JSON 加载/保存成功，MonitorPath 与 ScanDays 保留且新增 Performance；未逐项比较全部设置字段。 |
| AC-R06～AC-R07 | NOT TESTED | 未运行配方切换或散点图 UI 测试；散点图既存源代码改动已保留。 |
| AC-R08 | NOT TESTED | 临时 harness 验证 Load 将间隔 0/时限 -1 修正为 250/60，NormalizedFields 正确且不序列化；未验证 perf 开关下日志落点、WPF 状态、心跳及 3 次刷新。 |
| AC-D01 | NOT TESTED | 临时基线/当前 harness 各返回 150 条，逐行 Timestamp/RecordedAt/双高度完全相同；仅测当前默认开关组合，未覆盖 AC 要求的所有组合。 |
| AC-D02 | NOT TESTED | 代码已把设置快照传入两个计算函数；未做图表截图/数值范围现场对照。 |
| AC-D03 | PASS | 临时 harness 对坏时间戳数据断言 `UsedFallbackTs=true`，RecordedAt 与 `File.GetLastWriteTime` 完全相等：`2026-11-24T10:15:30.0000000+08:00`。 |
| AC-D04～AC-D05 | NOT TESTED | 未用 ProcMon 对相机目录执行 30 分钟只读/访问次数对比。 |

构建检查点：见 `docs/handoff/2026-11-24_implementation-report.md`（S0 基线及 S1～S13 各阶段编译结果）。
