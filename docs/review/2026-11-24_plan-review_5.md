# Developer Handoff 复核（第 5 轮）

- 日期：2026-11-24
- 复核者：Claude Sonnet5.5
- 状态：**PLAN STATUS: READY TO IMPLEMENT**
- 输入：最新 `docs/handoff/HANDOFF_latest.md`、`docs/review/2026-11-24_decision_4.md`、`docs/PLAN.md`、`docs/ACCEPTANCE.md` 和目标项目当前工作树代码。
- 约束：只读代码复核；不修改计划或代码。

## 1. 先前阶段待落盘文件

之前要求的 analysis、PLAN、ACCEPTANCE、Handoff 和裁决文件均存在；四轮 review 文件也已存在。本次按日期重名规则新增 `_5.md`，没有待补写的上一阶段文件。

## 2. 实际代码复核

- **现有 API 和调用关系**：`MainViewModel.RefreshDataAsync`、`ToggleRun`、`ResetAndRefreshAsync`、`SetupTimer`、`WriteLogToFile`、`Cleanup`、`CsvDataProvider.GetLatestDataAsync`、`GetLatestCsvFile`、`FindCsvInDirectory` 均存在。运行按钮、定时器、手动重置和配方切换的刷新调用路径与 Handoff 一致；`RefreshDataAsync` 的 `[RelayCommand]` 生成命令，可按 Handoff 保留兼容入口。
- **S1/R11 配置入口**：`Settings` 目前通过 `SettingsService.Load()` 在构造函数及取消设置面板时加载；`Load` 当前对 `Controller`、`Plc` 作 null 归一化，`PerformanceSettings.Normalize()` 是新增 API。在 `Performance ??= new()` 后调用 Normalize 能覆盖现有 Settings 加载入口。非法间隔使用点的兜底与 `RefreshDataAsync` 先设 `IsLoading`、停心跳、后在 try 之前创建 `progressTimer` 的实际顺序相符。
- **S3/R12 perf 记录**：最新 Handoff 已定义 `type=refresh` / `type=config`，config 行只在主 VM 构造函数 Load 后写一次；AC-N01 只统计 refresh 行；AC-R08 对 `EnablePerfLog` 开/关分别规定修正字段记录位置。启动 config 行与“每次刷新一行”的计数冲突已由记录类型及明确的统计范围消除。
- **队列与生命周期**：`WriteLogToFile` 当前是静态同步文件追加，`Cleanup` 从 Window Closing 和 App.OnExit 两处调用。S3 队列只服务 perf、S5 再接管常规日志、幂等关闭与有界等待的范围和调用点一致。
- **S6**：`PrepareChartData` 调用 `ComputeRobustTrendRange`，两者都读取 `Settings.Controller`；Handoff 指定将相同快照传入二者，且 `ApplyChartData` 保留 UI 线程执行，与实际依赖关系一致。
- **S9/S10**：`CsvDataProvider` 现有构造函数接受路径和 scanDays；`MainViewModel` 有三处创建点（初始化、配方轮询回退、配方切换）。可选参数保持现有构造兼容，并更新三个调用，接入范围完整。日期切换、文件消失同刷重扫、周期扫描与 AC-B01～B03 对应。R8 对刷新间隔大于 60 秒时“与现状等价”的说明在 PLAN/AC/Handoff 中一致；R10 已明确交付报告要告知用户。
- **S7/S8/S11/S13 及依赖**：WPF DispatcherTimer、Win32 P/Invoke、`System.Threading.Channels`、`System.Diagnostics.Process` 均可在现有 net8.0-windows 项目内使用；不需新增包。定时器最小间隔、线程背景模式 BEGIN/END、自动遮罩与 IsLoading 分离、进程 BelowNormal 的接入文件/函数均已明确。
- **AC 可测性**：AC-N01 可按 `type=refresh` 过滤；AC-R08 对应 Normalize 字段；AC-D01 外部 harness 可调用公开的 CsvDataProvider；AC-D02 使用指定方法差异审查与 UI 对比；现场 AC-N03 明确只用指标 L，无法取得 L 时记 BLOCKED-验收，抖动指标只用于阶段 0 归因。无自动化测试工程已如实注明。测试路径、既有未提交散点图及 HslCommunication 本地 DLL 的边界也已说明。

未发现新的设计级冲突。构建 A 仍应先按 S0 实际编译；编译是否成功、AC 是否通过均属实施阶段验证，本次未运行构建。

## 3. 简述实施步骤与 AC

- **构建 A：S0～S3**：基线编译；添加 Performance 配置和归一化；增加 CSV 阶段计时；perf 日志以 `type=config` 写启动配置、每次通过守卫的刷新写 `type=refresh`。交付构建 A 做 T0/T1/T3（T2 可选），通过现场测量门后再部署构建 B。
- **构建 B：S5～S11、S13**：常规日志转后台队列并有界 flush；图表预计算使用配置快照移出 UI 线程；进度文本降频；CSV 工作线程后台 I/O；缓存最新路径并按刷新周期重扫；复用文件属性时间戳；自动刷新不显示遮罩；进程设 BelowNormal。S12 继续不实施。
- **关键 AC**：AC-N01 统计 refresh 类型日志；AC-N03 使用端到端指标 L 做 2h p99 验收；AC-B01～B03 覆盖日切、同日新文件和文件删除；AC-E04 验证日志关闭 flush/超时/重复 Cleanup；AC-R05/08 验证旧配置及 Performance 非法值；AC-D01～D05 验证 CSV/图表结果和相机目录只读及扫描次数。

previous review 文件均已存在；本次只写本文件，没有改其他文档或代码。

**PLAN STATUS: READY TO IMPLEMENT**
