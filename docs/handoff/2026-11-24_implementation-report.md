# Implementation Report：刷新时影响相机检测软件

- 日期：2026-11-24
- 状态：**IMPLEMENTATION STATUS: COMPLETED**（按最新 Handoff 完成代码实现并通过构建；现场/完整 AC 未验证，不代表可部署验收）
- 代码范围：S0～S3、S5～S11、S13；S12 未实施。构建 B 仍须先过构建 A 的现场测量门才能部署。
- 未提交、未推送、未部署。

## 改动文件与函数

- `CrimpHeightAnalyzer/Models/AppSettings.cs`：新增 `PerformanceSettings`、Performance 配置字段、`Normalize()` 和 `[JsonIgnore] NormalizedFields`。
- `CrimpHeightAnalyzer/Services/SettingsService.cs`：`Load()` 对 Performance 缺失/非法数值归一化。
- `CrimpHeightAnalyzer/Services/CsvDataProvider.cs`：新增 `CsvReadMetrics`；扩展可选构造参数；拆分路径定位、CSV 枚举、size/read/parse 指标；缓存最新 CSV 路径与按时间窗重扫、消失后同次重扫；线程后台 I/O BEGIN/END；由 `GetFileAttributesEx` FILETIME 提供本地时间 fallback。
- `CrimpHeightAnalyzer/CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`：`RefreshDataAsync` 保留 RelayCommand 入口并转发至 `RefreshCoreAsync(bool manual)`；`WritePerformanceConfigRecord`、`QueueLogEntry`、`ProcessLogQueueAsync`；刷新阶段 perf 记录和 PLC 调用尝试标志；常规日志 Channel 后台写入与幂等有界 `Cleanup`；`PrepareChartData`/`ComputeRobustTrendRange` 使用快照并在 Task.Run 执行；进度刷新间隔、overlay 状态、进程优先级；三个 `CsvDataProvider` 创建点传入性能配置。
- `CrimpHeightAnalyzer/MainWindow.xaml`：加载遮罩绑定到 `IsLoadingOverlayVisible`。
- `docs/ACCEPTANCE.md`：追加 AC 结果表，没有改动原有验收条文。
- `docs/handoff/2026-11-24_implementation-report.md`：本报告。

没有修改 `CrimpHeight.Core/**`、PLC adapters/interfaces、PLC 协议/握手语义、CSV 解析/列映射/尾部读取规则、`OpenFileNoOplock` 打开标志。既存散点图改动保留；其他既存删除未触碰。临时 harness 已删除，无新增持久测试工程。

## AC 结果

完整逐项状态表见 `docs/ACCEPTANCE.md` §5。**仅 AC-D03 记为 PASS**；其他 AC 为 NOT TESTED，或有部分 harness 证据但不满足整条 AC，因此未记 PASS。

### 离线 harness 证据

- **CSV 数据对比子集**：临时控制台 harness 从 `git show HEAD` 导出未改动前的 `CsvDataProvider`，与当前 `CsvDataProvider` 对同一生成 CSV 分别读取 WindowSize=150。输出均为 150 条（总 151 行含 COUNT 行），逐行 Timestamp、RecordedAt、Head1Height、Head2Height 完全一致。只运行当前默认 CachedFileLocate/CsvBackgroundIoMode 组合；AC-D01 的完整模式矩阵仍 NOT TESTED。
- **S9 缩放时序子集**：Latency=2s、RefreshInterval=1s：首次 `rescan=true` 选中旧文件；阈值前复用旧文件 `rescan=false`；阈值后重新扫描并切到新文件 `rescan=true,fileChanged=true`。随后删除缓存文件、创建另一 CSV，同次读取触发 rescan 并切换路径。AC-B02 指定的 20/60/90s 和旧文件仍增长场景未执行；AC-B03 的 UI/下一周期未全测。
- **S10/AC-D03**：坏时间戳 CSV 返回 1 条，`UsedFallbackTs=true`，`RecordedAt` 与 `File.GetLastWriteTime(file)` 完全相同：`2026-11-24T10:15:30.0000000+08:00`。
- **S1 配置子集**：外部 harness 通过 `SettingsService.Load()` 加载 `ProgressTextIntervalMs=0`、`NewFileMaxLatencySeconds=-1`，得到 250/60，NormalizedFields 为这两个名称，JSON 序列化不含 NormalizedFields。加载无 Performance 的旧配置并保存成功，MonitorPath=`test-root`、ScanDays=7 保留，新增 Performance。AC-R05 的全部键对比、AC-R08 的应用日志/WPF/心跳/连续刷新仍 NOT TESTED。
- harness 曾因测试代码缺少 `System.IO` imports 编译失败；补齐后编译成功。fallback 初次测试复用了有更新文件的日期目录，Provider 正确选中更新文件；将该测试隔离到独立根目录后 S10 断言通过。上述均为临时验证代码问题，未修改产品代码；harness 已删除。

## 构建命令与 warnings（对比 S0 基线）

从外层仓库根目录构建目标 solution：

```text
dotnet build PRJ_04_PressHeightAutoAdjustment/CrimpHeightAutoAdjustment.slnx -c Debug
```

- **S0 基线：** Build succeeded，0 errors，9 warnings。
- **S1、S2、S3、S5、S6、S7、S8、S9、S10、S11、S13 的逐步检查点及最终构建：** 均 Build succeeded，0 errors，9 warnings；没有新增 warning。
- 9 条均为既有 NU1701，涉及 `OpenTK 3.3.1`、`OpenTK.GLWpfControl 3.3.0`、`SkiaSharp.Views.WPF 3.116.1` 对 `net8.0-windows7.0` 的兼容性 warning（主项目/WPF 临时编译项目重复显示）。
- S0 最初从外层目录误用了未带子项目路径的命令，MSBuild 报 MSB1009；改为上面的正确命令后基线构建成功。
- 未运行 `dotnet clean`，未删除 bin/obj，未触碰本地未纳管的 `HslCommunication.dll`。

## 范围检查

- 任务代码仅改动计划指定的 `AppSettings.cs`、`SettingsService.cs`、`CsvDataProvider.cs`、`MainViewModel.cs`、`MainWindow.xaml`。
- `CrimpHeight.Core`、PLC adapter 和 interface 无改动；协议、握手顺序、寄存器语义和 CSV 数据解释无改动。
- `MainViewModel.cs`、`MainWindow.xaml` 的 diff 中仍包含开始前已经存在的散点图功能改动；保留且未纳入本任务重写。
- 工作区原有的其他 `rsc/`/文档删除未触碰；没有提交或部署。
- `git diff --check` 对任务代码范围未报告 whitespace error；有既存 LF/CRLF 转换提示。

## 未验证 AC 的手动步骤与遗留事项

1. **AC-N01/N02/N05/R04/R08**：在 Windows 启动 WPF app，检查 `type=config` 与 10 条 `type=refresh`、字段及入口守卫计数；Mock PLC 下对比界面/日志；自动刷新确认无遮罩、手动重置仍有遮罩。分别用 `EnablePerfLog=true/false` 和非法 Performance 值启动，核对 config/plc 日志、`IsLoading` 复位、心跳恢复。
2. **AC-N03 与 S4**：产线确认 PLC L 的两个时间戳端点后跑 T0/T1/T3；用户审阅测量后才能部署构建 B，再跑 T4/T5 至少 2h。未做任何现场或 2h 测试，根因及整线 CT 影响仍未验证。
3. **AC-B01/B02/B04/B06/D04/D05**：独立测试目录做跨日、20/60/90 秒新文件（旧文件停止/继续增长）、空/半行 CSV、WindowSize=50/200；ProcMon 采集只读与访问次数/Duration。
4. **AC-B05、AC-E01～E05、AC-R01～R03、AC-R06～R07**：按 `docs/ACCEPTANCE.md` 使用 100 次连续刷新、错误目录、PLC 断连、日志权限/独占、双次 Cleanup、Mock PLC、配方切换、散点图 UI 等逐项执行。
5. **AC-N04**：启动程序后用任务管理器/Process Explorer 核对 Below Normal，false 重启后核对 Normal。

仍待现场确认：产线刷新间隔、`UseRealPlc`、心跳/配方轮询配置、PLC 能否记录指标 L 的触发时刻。AC-N03 和所有现场性能 AC 目前 NOT TESTED。
