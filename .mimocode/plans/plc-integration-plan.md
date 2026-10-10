# PLC Integration Plan — KeyencePlcAdapter

## Goal
Replace `MockPlcAdapter` with a real `KeyencePlcAdapter` using `HslCommunication.Profinet.Keyence.KeyenceMcNet` for MC Qna 3E Binary protocol communication with a Keyence PLC over TCP.

---

## Architecture Summary

```
IPlcAdapter (existing interface)
├── MockPlcAdapter (existing, keep for dev/test)
└── KeyencePlcAdapter (new)
        └── uses HslCommunication.Profinet.Keyence.KeyenceMcNet

AppSettings (existing)
└── PlcSettings Plc { get; set; } (new nested config class)

MainViewModel
└── InitDataProvider() → creates KeyencePlcAdapter based on config
```

---

## Implementation Steps

### Step 1: Add PlcSettings Model

**File**: `CrimpHeightAnalyzer/Models/AppSettings.cs`

Add a new nested configuration class and property to `AppSettings`:

```csharp
public class PlcSettings
{
    /// <summary>PLC IP 地址</summary>
    public string IpAddress { get; set; } = "192.168.1.10";

    /// <summary>PLC 端口 (MC protocol default: 6000)</summary>
    public int Port { get; set; } = 6000;

    /// <summary>连接超时 (ms)</summary>
    public int ConnectTimeoutMs { get; set; } = 5000;

    /// <summary>是否启用真实 PLC（false = 使用 Mock）</summary>
    public bool UseRealPlc { get; set; } = false;

    /// <summary>压头A 调整量写入地址 (D寄存器地址，如 "D100")</summary>
    public string HeadAAdjustAddress { get; set; } = "D100";

    /// <summary>压头B 调整量写入地址</summary>
    public string HeadBAdjustAddress { get; set; } = "D102";

    /// <summary>停机信号写入地址</summary>
    public string StopSignalAddress { get; set; } = "D200";

    /// <summary>自动重连间隔 (ms)</summary>
    public int ReconnectIntervalMs { get; set; } = 10000;
}
```

In `AppSettings`, add:
```csharp
public PlcSettings Plc { get; set; } = new();
```

**Rationale**: Keeps PLC config grouped and extensible. `UseRealPlc` toggle allows easy switching between mock and real adapter during development. Register addresses are configurable because different PLC programs may use different D-register mappings.

---

### Step 2: Create KeyencePlcAdapter

**New file**: `CrimpHeightAnalyzer/Services/KeyencePlcAdapter.cs`

```csharp
using System.Windows;
using CrimpHeight.Core.Interfaces;
using CrimpHeightAnalyzer.Models;
using HslCommunication;
using HslCommunication.Profinet.Keyence;

namespace CrimpHeightAnalyzer.Services;

public class KeyencePlcAdapter : IPlcAdapter, IDisposable
{
    private readonly PlcSettings _settings;
    private KeyenceMcNet? _plc;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private bool _disposed;

    public event Action<string>? OnLog;
    public event Action<bool, string>? OnConnectionChanged; // (isConnected, message)

    public bool IsConnected { get; private set; }

    public KeyencePlcAdapter(PlcSettings settings)
    {
        _settings = settings;
    }

    public async Task<bool> ConnectAsync(CancellationToken ct = default)
    {
        await _lock.WaitAsync(ct);
        try
        {
            _plc?.Dispose();
            _plc = new KeyenceMcNet
            {
                IpAddress = _settings.IpAddress,
                Port = _settings.Port,
                ConnectTimeOut = _settings.ConnectTimeoutMs,
                ReceiveTimeout = _settings.ConnectTimeoutMs
            };

            OperateResult connect = await _plc.ConnectServerAsync();
            if (connect.IsSuccess)
            {
                IsConnected = true;
                OnLog?.Invoke($"[PLC] 已连接 {_settings.IpAddress}:{_settings.Port}");
                OnConnectionChanged?.Invoke(true, "已连接");
                return true;
            }
            else
            {
                IsConnected = false;
                string msg = $"连接失败: {connect.Message}";
                OnLog?.Invoke($"[PLC] {msg}");
                OnConnectionChanged?.Invoke(false, msg);
                return false;
            }
        }
        finally
        {
            _lock.Release();
        }
    }

    public async Task SendAdjustmentAsync(int headIndex, double deltaMm, CancellationToken ct = default)
    {
        if (!await EnsureConnectedAsync(ct)) return;

        string address = headIndex == 0
            ? _settings.HeadAAdjustAddress
            : _settings.HeadBAdjustAddress;
        string head = headIndex == 0 ? "压头A" : "压头B";

        // 将 double 转为 float 写入 PLC（PLC 寄存器通常存 32 位浮点）
        float value = (float)deltaMm;
        OperateResult writeResult = await _plc!.WriteAsync(address, value);

        if (writeResult.IsSuccess)
        {
            OnLog?.Invoke($"[PLC] {head} 调整 {deltaMm:F5}mm → {address}");
        }
        else
        {
            OnLog?.Invoke($"[PLC] {head} 写入失败: {writeResult.Message}");
            IsConnected = false;
            OnConnectionChanged?.Invoke(false, $"写入失败: {writeResult.Message}");
        }
    }

    public async Task SendStopAsync(string reason, CancellationToken ct = default)
    {
        if (!await EnsureConnectedAsync(ct)) return;

        // 写入停机信号（值=1 表示停机）
        OperateResult writeResult = await _plc!.WriteAsync(_settings.StopSignalAddress, (short)1);

        if (writeResult.IsSuccess)
        {
            OnLog?.Invoke($"[PLC] 停机指令已发送: {reason}");
        }
        else
        {
            OnLog?.Invoke($"[PLC] 停机写入失败: {writeResult.Message}");
            IsConnected = false;
            OnConnectionChanged?.Invoke(false, $"停机写入失败: {writeResult.Message}");
        }
    }

    private async Task<bool> EnsureConnectedAsync(CancellationToken ct)
    {
        if (_plc != null && IsConnected) return true;
        return await ConnectAsync(ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _plc?.Dispose();
        _lock.Dispose();
        IsConnected = false;
        OnLog?.Invoke("[PLC] 连接已关闭");
    }
}
```

**Key design decisions**:
- Uses `SemaphoreSlim` for thread safety — the `DispatcherTimer` tick runs on UI thread but PLC calls are async and must not overlap.
- `EnsureConnectedAsync` provides lazy reconnect — if a write fails, `IsConnected` is set to `false` and the next call will attempt reconnect.
- `OnConnectionChanged` event lets the ViewModel update UI connection status.
- Writes `float` (32-bit) to PLC D-registers — this is standard for Keyence MC protocol float data.
- The adapter owns its own lifecycle and is `IDisposable`.

---

### Step 3: Update MainViewModel

**File**: `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs`

Changes:

1. **Replace field type** (line 24):
   ```csharp
   // Before:
   private MockPlcAdapter? _plcAdapter;
   // After:
   private IPlcAdapter? _plcAdapter;
   private IDisposable? _plcDisposable;
   ```

2. **Add PLC connection status properties**:
   ```csharp
   [ObservableProperty]
   private string plcStatusText = "未连接";

   [ObservableProperty]
   private string plcStatusColor = "#FFEF5350"; // red by default

   [ObservableProperty]
   private bool isPlcConnected;
   ```

3. **Update `InitDataProvider()`** (line 288-292):
   ```csharp
   private void InitDataProvider()
   {
       _dataProvider = new CsvDataProvider(Settings.MonitorPath, Settings.ScanDays);
       InitPlcAdapter();
   }

   private void InitPlcAdapter()
   {
       // Dispose previous adapter if any
       _plcDisposable?.Dispose();
       _plcDisposable = null;

       if (Settings.Plc.UseRealPlc)
       {
           var adapter = new KeyencePlcAdapter(Settings.Plc);
           adapter.OnLog += msg => System.Diagnostics.Debug.WriteLine(msg);
           adapter.OnConnectionChanged += (connected, message) =>
           {
               Application.Current.Dispatcher.Invoke(() =>
               {
                   IsPlcConnected = connected;
                   PlcStatusText = connected ? "已连接" : message;
                   PlcStatusColor = connected ? "#FF66BB6A" : "#FFEF5350";
               });
           };
           _plcAdapter = adapter;
           _plcDisposable = adapter;
           // Connect asynchronously (fire-and-forget, will retry on first send)
           _ = adapter.ConnectAsync();
       }
       else
       {
           var mock = new MockPlcAdapter();
           mock.OnLog += msg => System.Diagnostics.Debug.WriteLine(msg);
           _plcAdapter = mock;
           IsPlcConnected = true;
           PlcStatusText = "模拟模式";
           PlcStatusColor = "#FFFFCA28";
       }
   }
   ```

4. **Update `SaveSettings`** — after saving, re-init PLC adapter:
   The existing `SaveSettings` already calls `InitDataProvider()` which will now also reinitialize the PLC adapter.

5. **Add `Closing` cleanup command**:
   ```csharp
   [RelayCommand]
   private void Cleanup()
   {
       _refreshTimer.Stop();
       _plcDisposable?.Dispose();
   }
   ```

6. **Wire cleanup to Window.Closing** in `MainWindow.xaml.cs`:
   ```csharp
   Closing += (s, e) =>
   {
       if (DataContext is MainViewModel vm)
           vm.CleanupCommand.Execute(null);
   };
   ```

---

### Step 4: Update MainWindow.xaml — Add PLC Settings UI

**File**: `CrimpHeightAnalyzer/MainWindow.xaml`

#### 4a. Add PLC connection status indicator to the title bar

In the title bar StackPanel (around line 106-111), add a connection status indicator:

```xml
<!-- After the existing StatusText binding -->
<Ellipse Width="8" Height="8" Fill="{Binding PlcStatusColor}" Margin="16,0,4,0"
         ToolTip="{Binding PlcStatusText}"/>
<TextBlock Text="{Binding PlcStatusText}" Foreground="{Binding PlcStatusColor}"
           VerticalAlignment="Center" FontSize="10" Margin="0,0,0,0"/>
```

#### 4b. Add PLC configuration section to settings panel

In the settings panel (around line 401-489), add a new section before the "保存设置" button:

```xml
<Border Height="1" Background="#FF2A2A2A" Margin="0,4,0,12"/>

<TextBlock Text="PLC 通信" FontSize="13" FontWeight="Bold" Foreground="#FFB0B0B0" Margin="0,0,0,10"/>

<StackPanel Orientation="Horizontal" Margin="0,0,0,10">
    <CheckBox Content="启用真实 PLC" IsChecked="{Binding Settings.Plc.UseRealPlc}"
              Foreground="#FFB0B0B0" VerticalAlignment="Center"/>
    <TextBlock Text="{Binding PlcStatusText}" Foreground="{Binding PlcStatusColor}"
               VerticalAlignment="Center" Margin="12,0,0,0" FontSize="11"/>
</StackPanel>

<TextBlock Text="PLC IP 地址" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.IpAddress, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="端口" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.Port, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="连接超时 (ms)" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.ConnectTimeoutMs, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="压头A 调整地址" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.HeadAAdjustAddress, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="压头B 调整地址" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.HeadBAdjustAddress, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="停机信号地址" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.StopSignalAddress, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>

<TextBlock Text="自动重连间隔 (ms)" Style="{StaticResource LabelStyle}" Margin="0,0,0,4"/>
<TextBox Text="{Binding Settings.Plc.ReconnectIntervalMs, UpdateSourceTrigger=PropertyChanged}"
         Style="{StaticResource InputStyle}" Margin="0,0,0,10"/>
```

---

### Step 5: Reference HslCommunication.dll in .csproj

**File**: `CrimpHeightAnalyzer/CrimpHeightAnalyzer.csproj`

Since the DLLs are already in `bin/Debug/net8.0-windows`, add a direct DLL reference:

```xml
<ItemGroup>
    <Reference Include="HslCommunication">
        <HintPath>bin\Debug\net8.0-windows\HslCommunication.dll</HintPath>
        <Private>true</Private>
    </Reference>
</ItemGroup>
```

**Note**: The `Keyence_TCP_use_protocol_MC_Qna_3E_binary.dll` may be a wrapper/dependency of HslCommunication. If `KeyenceMcNet` is actually in that DLL rather than HslCommunication.dll, adjust the `using` and reference accordingly. Verify by checking the DLL contents or running a test build.

---

### Step 6: Update App.xaml.cs for Cleanup

**File**: `CrimpHeightAnalyzer/App.xaml.cs`

Add exit cleanup to ensure PLC connection is disposed:

```csharp
public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        if (MainWindow?.DataContext is MainViewModel vm)
            vm.CleanupCommand.Execute(null);
        base.OnExit(e);
    }
}
```

---

## File Change Summary

| File | Action | Description |
|------|--------|-------------|
| `CrimpHeightAnalyzer/Models/AppSettings.cs` | **Edit** | Add `PlcSettings` class and `Plc` property |
| `CrimpHeightAnalyzer/Services/KeyencePlcAdapter.cs` | **New** | Full PLC adapter implementation |
| `CrimpHeightAnalyzer/ViewModels/MainViewModel.cs` | **Edit** | Use IPlcAdapter, add connection status, add cleanup |
| `CrimpHeightAnalyzer/MainWindow.xaml` | **Edit** | Add PLC status indicator + settings UI |
| `CrimpHeightAnalyzer/MainWindow.xaml.cs` | **Edit** | Wire Closing event for cleanup |
| `CrimpHeightAnalyzer/App.xaml.cs` | **Edit** | Add OnExit cleanup |
| `CrimpHeightAnalyzer/CrimpHeightAnalyzer.csproj` | **Edit** | Add HslCommunication.dll reference |

## Implementation Order

1. `AppSettings.cs` — add PlcSettings (no dependencies)
2. `CrimpHeightAnalyzer.csproj` — add DLL reference
3. `KeyencePlcAdapter.cs` — create new file
4. `MainViewModel.cs` — update adapter creation and add status properties
5. `MainWindow.xaml` — add UI
6. `MainWindow.xaml.cs` + `App.xaml.cs` — lifecycle cleanup

## Risks & Considerations

- **DLL API surface**: The `KeyenceMcNet` class API (constructor, `ConnectServerAsync`, `WriteAsync` with `float`) assumes standard HslCommunication patterns. If the bundled DLLs have a different API, the adapter code may need adjustment. Verify with a test compile early.
- **Register address format**: The plan assumes D-register string format like `"D100"`. HslCommunication KeyenceMcNet may require a different address format (e.g., `"D100"` or `"D100.0"` for float). Verify against HslCommunication docs.
- **Thread safety**: `SemaphoreSlim` protects concurrent PLC writes. The `DispatcherTimer` tick is on UI thread but `await` releases it, so concurrent ticks are possible — the semaphore prevents overlapping writes.
- **Reconnect strategy**: The current design uses lazy reconnect (retry on next send). If periodic keep-alive is needed, add a timer-based ping in `KeyencePlcAdapter`.
- **MockPlcAdapter is preserved**: The `UseRealPlc` flag allows toggling between mock and real, useful for development and testing without hardware.
