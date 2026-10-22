# 变更记录 2026-10-22

> 范围：PRJ_04_PressHeightAutoAdjustment（CrimpHeight.Core + CrimpHeightAnalyzer）
> 日期：2026-10-22

---

## 1. CSV 读取性能优化 — 句柄常驻 + Win32 API

### 问题

相机软件每 0.1s 写入 CSV，.NET `FileStream` 构造会触发 Windows oplock（机会锁）协商，导致每次打开文件阻塞 2~16 秒。尝试过的方案及失败原因：

| 方案 | 失败原因 |
|---|---|
| `FileStream` + `FileShare.ReadWrite` | oplock 协商阻塞 3~12s |
| Win32 `CreateFile` | `CreateFile` 本身仍被内核阻塞 |
| `File.Copy` 复制副本再读 | 复制 6MB 文件本身慢，且同样受锁影响 |
| 每次打开/读取/关闭 | `CloseHandle` 因 oplock break 阻塞数秒 |

### 解决方案

**句柄常驻 + Win32 CreateFile + RandomAccess API**

- 用 Win32 `CreateFile` 以 `GENERIC_READ` + `FILE_SHARE_READ|WRITE|DELETE` + `FILE_FLAG_RANDOM_ACCESS` 打开文件
- 句柄缓存在 `_cachedHandle`，只在首次或文件路径变化时打开，后续刷新直接复用
- 每次刷新只做 `GetFileAttributesEx`（获取实时文件大小）+ `RandomAccess.Read`（读尾部字节），毫秒级完成
- `CloseHandle` 放到后台线程（`CloseHandleInBackground`），不阻塞主流程
- `_pendingCloses` 计数器防止句柄积压，超过 5 个时同步关闭

### 文件大小获取

`GetFileSizeEx` 通过缓存句柄查询文件大小，可能返回打开时的缓存值，看不到相机最新写入。改用 `GetFileAttributesEx` 通过文件路径查询，每次都是实时值。

### 涉及文件

- `CrimpHeightAnalyzer/Services/CsvDataProvider.cs` — 完全重写

### 架构对比

```
旧架构（每次刷新）：
  CreateFile(等12秒) → Read → Close(等6秒) → 下次刷新 → CreateFile(又等12秒)

新架构：
  首次刷新：CreateFile(等一次) → Read → 句柄缓存，不关闭
  后续刷新：GetFileAttributesEx(0ms) → RandomAccess.Read(0ms) → 完成
  文件切换：关闭旧句柄(后台) → CreateFile(等一次) → 缓存新句柄
```

---

## 2. 漂移检测修复 — 窗口填满后停住 + 下发后计数不清零

### 问题 H1：窗口填满后漂移拍永远不推进

漂移拍推进用"窗口内总条数差值"判断新增量。窗口满后（如 WindowSize=50），每次 `head1Values.Count` 都是 50，差值永远是 0，达不到推进门限（25），漂移检测在稳态运行时实际不工作。

### 问题 H2（附带）：自动/手动下发后漂移拍计数不清零

下发后 bypass 旧数据，`head1Values.Count` 从 0 开始涨，但 `_headALastDriftSampleCount` 还是旧值（如 50），`0 - 50 = -50`，永远追不上。

### 解决方案

**改用时间戳统计新增量**，彻底替代条数差值方式。

- 新增字段 `_headALastDriftTimestamp` / `_headBLastDriftTimestamp`，记录上一拍最后一条数据的时间戳
- 新增方法 `CountNewSamplesSince(data, sinceTimestamp)`，统计指定时间戳之后的新增样本数
- 窗口大小不再影响漂移拍推进
- 所有重置点（手动调整、自动下发、ResetAndRefresh、InitDataProvider）同步清空时间戳

### 涉及文件

- `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`
  - `_headALastDriftSampleCount` / `_headBLastDriftSampleCount` → `_headALastDriftTimestamp` / `_headBLastDriftTimestamp`
  - 新增 `CountNewSamplesSince()` 辅助方法
  - 所有引用点更新

---

## 3. 漂移确认时绕过死区 — 提前补偿

### 问题 H2：偏差在死区内时漂移确认产出 Δ=0

`ArbitrateSingle` 中漂移确认后调用 `ComputeAdjustment`，但该方法内部有死区判断，偏差在 `±Tolerance` 内直接返回 0。导致 `State=Drift, Δ=0`，`RequiresAction=false`（要求 `|Δ| > 1e-10`），漂移检测变成纯报警，无法提前补偿，违背"抓渐变"的设计目标。

### 解决方案

新增 `R2RController.ComputeAdjustmentBypassDeadband()` 方法，只做 `K × deviation` + 步长限幅，不做死区判断。

- `ArbitrateSingle` 和 `Arbitrate` 中：
  - 越界触发 → 走 `ComputeAdjustment`（含死区）
  - 漂移确认触发 → 走 `ComputeAdjustmentBypassDeadband`（绕过死区）

### 涉及文件

- `CrimpHeight.Core/Controller/R2RController.cs` — 新增 `ComputeAdjustmentBypassDeadband()`
- `CrimpHeight.Core/Controller/DecisionArbiter.cs` — `ArbitrateSingle` 和 `Arbitrate` 中区分越界/漂移触发

---

## 4. 自适应增 K — 突破死区边缘停滞

### 问题

K=0.5 意味着每次只修正偏差的一半。数学上：`0.05 → 0.025 → 0.0125 → ...` 无限趋近但永远到不了目标。偏差一旦降到死区边缘（±0.02）就停止调整，实测永远差 0.02 以内。

### 解决方案

在 `R2RController` 中加入**自适应增 K** 机制：

**停滞检测条件**（同时满足）：
- 偏差方向与上次一致
- 偏差幅度变化率 < 30%（收敛停滞）
- 连续 ≥ 3 次

**增 K 策略**：
- 每次停滞确认后 K += 0.1
- K 绝对上限 0.8（不可超过）
- 逐步升档：`0.5 → 0.6 → 0.7 → 0.8`

**恢复/回退 K**：

| 情况 | 行为 | K 变化 |
|---|---|---|
| 方向一致 + 幅度变化小 | 收敛停滞，加力 | K +0.1（最高 0.8） |
| 方向反转 | 过调了，回退一档 | K -0.1（最低 0.5） |
| 方向一致 + 幅度变化大 | 调整在起效，保持 | K 不变，停滞计数清零 |
| 偏差进入死区 | 调整到位 | K 恢复 0.5 |
| 外部重置（下发成功/手动调整） | epoch 切换 | K 恢复 0.5 |

### 参数

| 参数 | 值 | 含义 |
|---|---|---|
| `StallThreshold` | 3 | 连续 3 次停滞后开始增 K |
| `KBoostStep` | 0.1 | 每次增/减 K 的步进 |
| `KMaxLimit` | 0.8 | K 的绝对上限 |
| `StallChangeRatio` | 0.3 | 偏差幅度变化率 < 30% 视为停滞 |

### 涉及文件

- `CrimpHeight.Core/Controller/R2RController.cs` — 新增停滞检测状态、`UpdateStallDetection()`、`ResetKBoost()`，`ComputeAdjustment` 和 `ComputeAdjustmentBypassDeadband` 中使用自适应 K
- `CrimpHeight.Core/Controller/DecisionArbiter.cs` — `Reset()` 中调用 `_r2r.ResetKBoost()`

---

## 5. WarmUp 时图表偏差值修复

### 问题

WarmUp 状态下截尾均值为 0，偏差 = `0 - TargetMidpoint`（如 -12.2），是假值。补偿图表和累计补偿显示会出现突变尖刺。

### 解决方案

新增 `_lastValidHeadADeviation` / `_lastValidHeadBDeviation` 字段：
- 非 WarmUp 状态：正常使用偏差值，同时记住
- WarmUp 状态：沿用上次记住的有效偏差值，保持折线连续

### 涉及文件

- `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs` — UI 更新部分

---

## 6. 诊断信息增强

### 改动

- `CsvDataProvider`：诊断输出拆分为 `打开=Xms 读取=Xms 解析=Xms`，外层检测调度延迟
- `MainViewModel`：`PerformanceText` 中增加首尾数据时间戳（`首=X 尾=X`），用于确认每次读到的数据是否不同
