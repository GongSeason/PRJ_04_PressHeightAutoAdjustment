# Developer Handoff 复核（第 4 轮）

- 日期：2026-11-24
- 复核者：Claude Sonnet5.5
- 状态：**PLAN STATUS: REVIEW REQUIRED**
- 输入：最新 `docs/handoff/HANDOFF_latest.md`、`docs/review/2026-11-24_decision_3.md`、`docs/PLAN.md`、`docs/ACCEPTANCE.md` 和当前工作树代码。
- 约束：只读代码复核；不自行修订计划。

## 1. 先前阶段待落盘文件

前序要求的分析、PLAN、ACCEPTANCE、Handoff 和前三轮裁决/复核文件均已存在，无待补写文件。已有 review 文件名为 `2026-11-24_plan-review.md`、`_2.md`、`_3.md`，所以本轮按日期重名规则写入 `_4.md`。

## 2. 实际代码复核摘要

- `MainViewModel` 的 `Settings` 在构造函数中通过 `_settingsService.Load()` 赋值；取消设置面板时也通过同一 `Load()` 赋值。当前 `SettingsService.Load()` 在反序列化后为 `Controller`、`Plc` 补默认对象，但没有 `Performance` 对象/数值校验。把 `PerformanceSettings.Normalize()` 接在 `settings.Performance ??= new()` 后，能够覆盖目前这两处加载入口。
- 当前 `RefreshDataAsync` 在进入 `try` 之前先设置 `IsLoading=true`、停心跳，再建立 `progressTimer`。所以读取未归一化的非法间隔确实可能在 try/catch/finally 保护之外失败；R11 增加 Normalize 和使用点兜底是有依据的。
- `PerformanceSettings`、`Normalize()`、`NormalizedFields`、perf 启动配置记录都是计划新增项。框架、调用关系与项目结构没有新增阻碍；此前核对过的 Channel、Win32、WPF、CsvDataProvider 调用点不变。
- **发现的未决冲突：** R11 决定要求 perf 日志记一行“启动配置及被修正字段”；但 S3 的交付项只定义每次通过守卫的刷新写一行，AC-N01 又要求 10 次刷新“恰好新增 10 行”。AC-R08 则要求 perf 日志存在启动行。当前 AC 没说明启动记录是否计入 AC-N01 的行数，也没定义其记录类型/文件是否相同。按最新 Handoff，实施者可能同时满足 AC-R08 而违反 AC-N01。
- 其他方面：构建 A/B 次序、构造调用点、S6 快照调用图、S11 命令保留、R8/R9 的最新裁决和 AC-R08 的归一化目标与当前代码框架相容。没有找到新的代码范围或依赖问题；本次没有运行构建。

## 3. 需要 Opus 裁决的设计问题

### R12 — perf 启动配置记录与“每次刷新一行”的验收行数冲突

- **原计划 / 裁决要求：**
  - Opus 第 3 轮 R11 要求 `PerformanceSettings.Normalize()` 记录被修正字段，并“供 perf 日志在启动时记一行有效配置和被修正字段”。
  - 最新 Handoff 的 S3 只规定“每次通过入口守卫的刷新都在 finally 写一行 `perf_yyyyMMdd.log`”。
  - AC-N01 要求 10 次通过守卫的刷新后 perf 日志“恰好新增 10 行”。
  - AC-R08 要求“perf 日志的启动行中列出被修正的字段名”。
- **代码证据：** 当前 `RefreshDataAsync` 只在 refresh 调用中有阶段数据；`MainViewModel` 构造函数在加载 Settings 后可读取 `[JsonIgnore]` 的 `NormalizedFields`，但没有现成的 perf 记录类型或启动日志 API。S3 将 `Channel<LogEntry>` 指定给 perf 日志，启动行如果也写入同一个文件，正常启动后行数将是刷新数 + 1。
- **冲突与影响：** 如果把启动行算作 perf 日志中的普通行，AC-N01 的精确行数失败；如果为了满足 N01 不写启动行，则 AC-R08 和 R11 的决定未实现。若把配置放入每一行，AC-R08 当前“启动行”的文字也不匹配。未定义 `recordType` 的话，现场统计 perf 行数也不明确。
- **需 Claude Opus5.5 决定什么：** 明确唯一方案并同步 S3、AC-N01、AC-R08 和 Handoff，例如：
  1. 同一个 `perf_*.log` 增加显式 `recordType=config` / `recordType=refresh` 字段，AC-N01 只统计 refresh 行（这样“10 次刷新恰好 10 行 refresh 记录”，另允许 1 行 config 记录）；或
  2. 将 config 行写到单独的启动配置日志/文件，而 `perf_*.log` 仍严格每次刷新一行；或
  3. 不单独写启动行，把归一化字段并入第一条刷新记录，并相应修订 AC-R08 的“启动行”要求。

  同时应定义 `EnablePerfLog=false` 时是否仍要记录配置归一化信息，避免出现 AC-R08 依赖一个被关闭的日志功能。

## 4. 结论

除 R12 的 perf 日志记录类型/行数定义外，最新 Handoff 与当前代码结构一致，S0～S13 的主要 API、依赖和调用范围已核实。R12 会直接使 AC-N01 与 AC-R08 互相冲突，属于需要 Opus 决定的设计级问题。

本次只新增本 review 文件；没有改 PLAN、ACCEPTANCE、Handoff 或代码。

**PLAN STATUS: REVIEW REQUIRED**
