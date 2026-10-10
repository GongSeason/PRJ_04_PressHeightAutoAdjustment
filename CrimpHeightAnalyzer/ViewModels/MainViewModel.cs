using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CrimpHeight.Core.Controller;
using CrimpHeight.Core.Interfaces;
using CrimpHeight.Core.Models;
using CrimpHeightAnalyzer.Models;
using CrimpHeightAnalyzer.Services;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using LiveChartsCore.SkiaSharpView.VisualElements;
using SkiaSharp;

namespace CrimpHeightAnalyzer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DecisionArbiter _headAArbiter = new();
    private readonly DecisionArbiter _headBArbiter = new();
    private readonly SettingsService _settingsService = new();
    private CsvDataProvider? _dataProvider;
    private IPlcAdapter? _plcAdapter;
    private IDisposable? _plcDisposable;
    private DispatcherTimer _refreshTimer = null!;
    private List<CrimpData> _currentData = new();
    private DateTime? _lastAnalyzedDataTimestamp;
    private DateTime? _dataUnavailableSince;
    private DateTime? _plcCommandDataTimestamp;
    private readonly HashSet<string> _issuedPlcCommandKeys = new();

    public MainViewModel()
    {
        LiveCharts.Configure(settings =>
            settings.HasGlobalSKTypeface(SKTypeface.FromFamilyName("Microsoft YaHei")));

        Settings = _settingsService.Load();
        InitDataProvider();
        SetupTimer();
        SetupCharts();

        // 初始加载
        _ = RefreshDataAsync();
    }

    #region 配置

    [ObservableProperty]
    private AppSettings settings;

    [ObservableProperty]
    private bool isSettingsPanelOpen;

    [ObservableProperty]
    private string statusText = "就绪";

    [ObservableProperty]
    private bool isLoading;

    #endregion

    #region PLC 状态

    [ObservableProperty]
    private string plcStatusText = "未连接";

    [ObservableProperty]
    private string plcStatusColor = "#FFEF5350";

    [ObservableProperty]
    private bool isPlcConnected;

    [ObservableProperty]
    private string headACurrentPosition = "--";

    [ObservableProperty]
    private string headBCurrentPosition = "--";

    #endregion

    #region 压头A 分析结果

    [ObservableProperty]
    private string headACombinedTrimmedMean = "--";

    [ObservableProperty]
    private string headADeviation = "--";

    [ObservableProperty]
    private string headAState = "--";

    [ObservableProperty]
    private string headAStateColor = "#FFFFFFFF";

    [ObservableProperty]
    private string headAAdjustment = "--";

    [ObservableProperty]
    private string headAAdjustmentColor = "#FFFFD54F";

    [ObservableProperty]
    private string headAProduct1Mean = "--";

    [ObservableProperty]
    private string headAProduct1StdDev = "--";

    [ObservableProperty]
    private string headAProduct2Mean = "--";

    [ObservableProperty]
    private string headAProduct2StdDev = "--";

    [ObservableProperty]
    private string headADataCount = "--";

    [ObservableProperty]
    private string headADivergence = "--";

    [ObservableProperty]
    private string headASlope = "--";

    #endregion

    #region 压头B 分析结果

    [ObservableProperty]
    private string headBCombinedTrimmedMean = "--";

    [ObservableProperty]
    private string headBDeviation = "--";

    [ObservableProperty]
    private string headBState = "--";

    [ObservableProperty]
    private string headBStateColor = "#FFFFFFFF";

    [ObservableProperty]
    private string headBAdjustment = "--";

    [ObservableProperty]
    private string headBAdjustmentColor = "#FFFFD54F";

    [ObservableProperty]
    private string headBProduct1Mean = "--";

    [ObservableProperty]
    private string headBProduct1StdDev = "--";

    [ObservableProperty]
    private string headBProduct2Mean = "--";

    [ObservableProperty]
    private string headBProduct2StdDev = "--";

    [ObservableProperty]
    private string headBDataCount = "--";

    [ObservableProperty]
    private string headBDivergence = "--";

    [ObservableProperty]
    private string headBSlope = "--";

    #endregion

    #region 告警

    [ObservableProperty]
    private string alertText = string.Empty;

    [ObservableProperty]
    private bool hasAlert;

    #endregion

    #region 图表

    [ObservableProperty]
    private string trendChartTitle = "趋势图";

    [ObservableProperty]
    private bool isP1Visible = true;

    [ObservableProperty]
    private bool isP2Visible = true;

    private LineSeries<double>? _p1Series;
    private LineSeries<double>? _p2Series;

    public ISeries[] DistributionSeries { get; set; } = Array.Empty<ISeries>();
    public ISeries[] TrendSeries { get; set; } = Array.Empty<ISeries>();
    public Axis[] TrendXAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] TrendYAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] DistributionXAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] DistributionYAxes { get; set; } = Array.Empty<Axis>();
    public RectangularSection[] TrendSections { get; set; } = Array.Empty<RectangularSection>();

    partial void OnIsP1VisibleChanged(bool value) { if (_p1Series != null) _p1Series.IsVisible = value; }
    partial void OnIsP2VisibleChanged(bool value) { if (_p2Series != null) _p2Series.IsVisible = value; }

    #endregion

    #region 命令

    [RelayCommand]
    private void ToggleSettings()
    {
        if (IsLoading)
        {
            StatusText = "数据刷新中，请稍后修改设置";
            return;
        }

        if (IsSettingsPanelOpen)
        {
            Settings = _settingsService.Load();
            IsSettingsPanelOpen = false;
            StatusText = "已取消未保存的设置";
            return;
        }

        IsSettingsPanelOpen = true;
    }

    [RelayCommand]
    private void SaveSettings()
    {
        if (IsLoading)
        {
            StatusText = "数据刷新中，请稍后保存设置";
            return;
        }

        try
        {
            ValidateSettings();
        }
        catch (InvalidOperationException ex)
        {
            StatusText = $"配置无效: {ex.Message}";
            return;
        }

        if (!_settingsService.Save(Settings))
        {
            StatusText = "配置保存失败，请检查程序目录写入权限";
            return;
        }

        InitDataProvider();
        IsSettingsPanelOpen = false;
        StatusText = "配置已保存";

        _refreshTimer.Interval = TimeSpan.FromSeconds(Settings.RefreshIntervalSeconds);
        _ = RefreshDataAsync();
    }

    [RelayCommand]
    private void BrowseFolder()
    {
        try
        {
            string initialDirectory = Settings.MonitorPath;
            if (!Directory.Exists(initialDirectory))
            {
                initialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                if (!Directory.Exists(initialDirectory))
                    initialDirectory = Environment.CurrentDirectory;
            }

            var dialog = new Microsoft.Win32.OpenFolderDialog
            {
                Title = "选择监视文件夹",
                InitialDirectory = initialDirectory
            };

            if (dialog.ShowDialog() == true)
            {
                Settings.MonitorPath = dialog.FolderName;
                OnPropertyChanged(nameof(Settings));
            }
        }
        catch (Exception ex)
        {
            StatusText = $"无法打开文件夹选择器: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RefreshDataAsync()
    {
        if (IsSettingsPanelOpen || IsLoading || _dataProvider == null) return;

        IsLoading = true;
        StatusText = "正在加载数据...";

        try
        {
            ValidateSettings();

            var data = await _dataProvider.GetLatestDataAsync(Settings.Controller.WindowSize);
            _currentData = data;

            if (_currentData.Count == 0 || !_currentData.Any(d => d.IsValid))
            {
                // 失活计时以"检测到无数据的墙钟时刻"为准，避免被历史/异常时间戳污染，
                // 导致首次刷新即误判超时停机。
                DateTime now = DateTime.Now;
                _dataUnavailableSince ??= now;

                double unavailableSeconds = (now - _dataUnavailableSince.Value).TotalSeconds;
                string unavailableReason = $"未找到压接数据，已持续 {unavailableSeconds:F0}s";
                HasAlert = true;
                AlertText = unavailableReason;

                if (unavailableSeconds > Settings.Controller.DataStaleTimeoutSeconds)
                {
                    await SendStopOnceAsync(null, "data-unavailable", unavailableReason);
                    StatusText = "数据源失活，已请求停机";
                }
                else
                {
                    StatusText = "未找到数据";
                }

                IsLoading = false;
                return;
            }

            _dataUnavailableSince = null;

            DateTime? currentDataTimestamp = _dataProvider.LastDataTimestamp;
            bool advanceDriftCycle = currentDataTimestamp.HasValue
                && currentDataTimestamp != _lastAnalyzedDataTimestamp;

            // 每个压头仅对应一路高度；分别过滤有效值并独立决策。
            var head1Values = _currentData.Where(d => d.IsHead1Valid).Select(d => d.Head1Height).ToList();
            var head2Values = _currentData.Where(d => d.IsHead2Valid).Select(d => d.Head2Height).ToList();
            var headAResult = _headAArbiter.ArbitrateSingle("铆压#1", head1Values,
                Settings.Controller, currentDataTimestamp, advanceDriftCycle);
            var headBResult = _headBArbiter.ArbitrateSingle("铆压#2", head2Values,
                Settings.Controller, currentDataTimestamp, advanceDriftCycle);

            _lastAnalyzedDataTimestamp = currentDataTimestamp;

            // 收集告警 — Outlier/Stop 为报警，Drift 为需要关注的调整预警，均需在界面可见。
            var alerts = new List<string>();
            if (ShouldAlert(headAResult.Decision))
                alerts.Add($"[铆压#1] {headAResult.Decision.Reason}");
            if (ShouldAlert(headBResult.Decision))
                alerts.Add($"[铆压#2] {headBResult.Decision.Reason}");

            // 发送 PLC 指令 — 停机优先于调整
            await SendPlcCommandsAsync(
                currentDataTimestamp,
                headAResult.Decision,
                headBResult.Decision);

            // 读取压头位置
            double? headAPosition = null;
            double? headBPosition = null;
            if (_plcAdapter != null)
            {
                headAPosition = await _plcAdapter.ReadHeadPositionAsync(0);
                headBPosition = await _plcAdapter.ReadHeadPositionAsync(1);
            }

            // 更新UI
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateHeadAResults(headAResult);
                UpdateHeadBResults(headBResult);
                UpdateAlerts(alerts);
                UpdateCharts(_currentData);
                UpdateHeadPositions(headAPosition, headBPosition);
                StatusText = $"数据加载完成 - {_currentData.Count}条记录";
                IsLoading = false;
            });
        }
        catch (Exception ex)
        {
            StatusText = $"加载失败: {ex.Message}";
            IsLoading = false;
        }
    }

    private void UpdateHeadPositions(double? headAPosition, double? headBPosition)
    {
        HeadACurrentPosition = headAPosition.HasValue ? $"{headAPosition.Value:F5}mm" : "--";
        HeadBCurrentPosition = headBPosition.HasValue ? $"{headBPosition.Value:F5}mm" : "--";
    }

    #endregion

    #region 私有方法

    private void InitDataProvider()
    {
        _dataProvider = new CsvDataProvider(Settings.MonitorPath, Settings.ScanDays);
        _headAArbiter.Reset();
        _headBArbiter.Reset();
        _lastAnalyzedDataTimestamp = null;
        _dataUnavailableSince = null;
        _plcCommandDataTimestamp = null;
        _issuedPlcCommandKeys.Clear();
        InitPlcAdapter();
    }

    private async Task SendPlcCommandsAsync(
        DateTime? dataTimestamp,
        HeadDecision headADecision,
        HeadDecision headBDecision)
    {
        var plcAdapter = _plcAdapter;
        if (plcAdapter == null) return;

        PreparePlcCommandVersion(dataTimestamp);

        if (headADecision.RequiresStop || headBDecision.RequiresStop)
        {
            string stopKey = $"stop|{headADecision.RequiresStop}|{headBDecision.RequiresStop}";
            string reason = headADecision.RequiresStop
                ? headADecision.Reason
                : headBDecision.Reason;
            await SendStopOnceAsync(dataTimestamp, stopKey, reason);
            return;
        }

        if (headADecision.RequiresAction)
        {
            string headAKey = FormattableString.Invariant(
                $"adjust|0|{headADecision.AdjustmentDelta:R}");
            if (!_issuedPlcCommandKeys.Contains(headAKey))
            {
                // 仅在写入成功后记录，失败时保留重试机会，避免调整量被漏发。
                if (await plcAdapter.SendAdjustmentAsync(0, headADecision.AdjustmentDelta))
                    _issuedPlcCommandKeys.Add(headAKey);
            }
        }

        if (headBDecision.RequiresAction)
        {
            string headBKey = FormattableString.Invariant(
                $"adjust|1|{headBDecision.AdjustmentDelta:R}");
            if (!_issuedPlcCommandKeys.Contains(headBKey))
            {
                if (await plcAdapter.SendAdjustmentAsync(1, headBDecision.AdjustmentDelta))
                    _issuedPlcCommandKeys.Add(headBKey);
            }
        }
    }

    private async Task SendStopOnceAsync(
        DateTime? dataTimestamp,
        string commandKey,
        string reason)
    {
        var plcAdapter = _plcAdapter;
        if (plcAdapter == null) return;

        PreparePlcCommandVersion(dataTimestamp);
        if (_issuedPlcCommandKeys.Contains(commandKey)) return;

        // 仅在停机信号成功写入后记录，失败时下一拍可重发。
        if (await plcAdapter.SendStopAsync(reason))
            _issuedPlcCommandKeys.Add(commandKey);
    }

    private void PreparePlcCommandVersion(DateTime? dataTimestamp)
    {
        if (dataTimestamp == _plcCommandDataTimestamp) return;

        _plcCommandDataTimestamp = dataTimestamp;
        _issuedPlcCommandKeys.Clear();
    }

    private void InitPlcAdapter()
    {
        // 清理旧的适配器
        if (_plcDisposable != null)
        {
            _plcDisposable.Dispose();
            _plcDisposable = null;
        }

        try
        {
            ValidateSettings();
        }
        catch (InvalidOperationException ex)
        {
            _plcAdapter = null;
            IsPlcConnected = false;
            PlcStatusText = "配置无效";
            PlcStatusColor = "#FFEF5350";
            StatusText = $"配置无效: {ex.Message}";
            return;
        }

        if (Settings.Plc.UseRealPlc)
        {
            var keyenceAdapter = new KeyencePlcAdapter(Settings.Plc);
            keyenceAdapter.OnLog += msg => System.Diagnostics.Debug.WriteLine(msg);
            keyenceAdapter.OnConnectionChanged += (connected, message) =>
            {
                Application.Current.Dispatcher.Invoke(() =>
                {
                    IsPlcConnected = connected;
                    PlcStatusText = connected ? "已连接" : message;
                    PlcStatusColor = connected ? "#FF66BB6A" : "#FFEF5350";
                });
            };

            _plcAdapter = keyenceAdapter;
            _plcDisposable = keyenceAdapter;

            // 异步连接
            _ = keyenceAdapter.ConnectAsync();
        }
        else
        {
            var mockAdapter = new MockPlcAdapter();
            mockAdapter.OnLog += msg => System.Diagnostics.Debug.WriteLine(msg);
            _plcAdapter = mockAdapter;

            IsPlcConnected = false;
            PlcStatusText = "模拟模式";
            PlcStatusColor = "#FFFFCA28";
        }
    }

    [RelayCommand]
    private void Cleanup()
    {
        _refreshTimer?.Stop();
        _plcDisposable?.Dispose();
        _plcDisposable = null;
        _plcAdapter = null;
    }

    private void SetupTimer()
    {
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.RefreshIntervalSeconds))
        };
        _refreshTimer.Tick += async (s, e) => await RefreshDataAsync();
        _refreshTimer.Start();
    }

    private void ValidateSettings()
    {
        if (string.IsNullOrWhiteSpace(Settings.MonitorPath))
            throw new InvalidOperationException("监视文件夹不能为空。");
        if (Settings.ScanDays < 0)
            throw new InvalidOperationException("扫描天数不能为负数。");
        if (Settings.RefreshIntervalSeconds < 1)
            throw new InvalidOperationException("刷新间隔必须大于 0 秒。");

        if (Settings.Controller == null)
            throw new InvalidOperationException("控制器配置不能为空。");
        Settings.Controller.Validate();

        if (Settings.Plc == null)
            throw new InvalidOperationException("PLC 配置不能为空。");
        if (Settings.Plc.UseRealPlc)
        {
            if (string.IsNullOrWhiteSpace(Settings.Plc.IpAddress))
                throw new InvalidOperationException("PLC IP 地址不能为空。");
            if (Settings.Plc.Port is < 1 or > 65535)
                throw new InvalidOperationException("PLC 端口必须在 1 到 65535 之间。");
            if (Settings.Plc.ConnectTimeoutMs < 1)
                throw new InvalidOperationException("PLC 连接超时必须大于 0 毫秒。");
            if (Settings.Plc.ReconnectIntervalMs < 1)
                throw new InvalidOperationException("PLC 重连间隔必须大于 0 毫秒。");
            if (string.IsNullOrWhiteSpace(Settings.Plc.HeadAAdjustAddress)
                || string.IsNullOrWhiteSpace(Settings.Plc.HeadBAdjustAddress)
                || string.IsNullOrWhiteSpace(Settings.Plc.StopSignalAddress)
                || string.IsNullOrWhiteSpace(Settings.Plc.HeadAPositionAddress)
                || string.IsNullOrWhiteSpace(Settings.Plc.HeadBPositionAddress))
            {
                throw new InvalidOperationException("PLC 寄存器地址不能为空。");
            }
        }
    }

    private void UpdateHeadAResults(HeadAnalysisResult result)
    {
        HeadACombinedTrimmedMean = result.CombinedTrimmedMean.ToString("F3");
        string sign = result.Deviation >= 0 ? "+" : "";
        HeadADeviation = $"{sign}{result.Deviation:F4} mm";
        HeadADataCount = $"有效样本 {result.Product1.DataCount} 条";

        HeadAProduct1Mean = result.Product1.TrimmedMean.ToString("F3");
        HeadAProduct1StdDev = result.Product1.StandardDeviation.ToString("F4");
        HeadAProduct2Mean = "--";
        HeadAProduct2StdDev = "--";

        HeadADivergence = "单产品模式";
        HeadASlope = $"{result.CurrentSlope:F6} mm/拍";

        UpdateStateDisplay(result.Decision, out string stateText, out string stateColor, out string adjustment, out string adjustmentColor);
        HeadAState = stateText;
        HeadAStateColor = stateColor;
        HeadAAdjustment = adjustment;
        HeadAAdjustmentColor = adjustmentColor;
    }

    private void UpdateHeadBResults(HeadAnalysisResult result)
    {
        HeadBCombinedTrimmedMean = result.CombinedTrimmedMean.ToString("F3");
        string sign = result.Deviation >= 0 ? "+" : "";
        HeadBDeviation = $"{sign}{result.Deviation:F4} mm";
        HeadBDataCount = $"有效样本 {result.Product1.DataCount} 条";

        HeadBProduct1Mean = result.Product1.TrimmedMean.ToString("F3");
        HeadBProduct1StdDev = result.Product1.StandardDeviation.ToString("F4");
        HeadBProduct2Mean = "--";
        HeadBProduct2StdDev = "--";

        HeadBDivergence = "单产品模式";
        HeadBSlope = $"{result.CurrentSlope:F6} mm/拍";

        UpdateStateDisplay(result.Decision, out string stateText, out string stateColor, out string adjustment, out string adjustmentColor);
        HeadBState = stateText;
        HeadBStateColor = stateColor;
        HeadBAdjustment = adjustment;
        HeadBAdjustmentColor = adjustmentColor;
    }

    private static void UpdateStateDisplay(HeadDecision decision,
        out string stateText, out string stateColor, out string adjustment, out string adjustmentColor)
    {
        switch (decision.State)
        {
            case HeadState.WarmUp:
                stateText = "⏳ WarmUp — 置信不足";
                stateColor = "#FFBDBDBD";
                adjustment = "采集中，不动作";
                adjustmentColor = "#FFBDBDBD"; // 灰色
                break;
            case HeadState.Stable:
                stateText = "✓ Stable — 稳定";
                stateColor = "#FF66BB6A";
                adjustment = "无需调整";
                adjustmentColor = "#FF66BB6A"; // 绿色
                break;
            case HeadState.Drift:
                if (decision.DivergenceLevel == DivergenceLevel.Stop)
                {
                    stateText = "⛔ Diverged — 发散停止";
                    stateColor = "#FFEF5350";
                    adjustment = "需维护，已停机";
                    adjustmentColor = "#FFEF5350"; // 红色
                }
                else if (decision.DivergenceLevel == DivergenceLevel.Degraded)
                {
                    stateText = "⚠ Drift (Degraded)";
                    stateColor = "#FFFFCA28";
                    adjustment = $"{GetDirectionSymbol(decision.Direction)} Δ={decision.AdjustmentDelta:F5}mm (降K)";
                    adjustmentColor = "#FFFFD54F"; // 黄色
                }
                else
                {
                    stateText = "↗ Drift — 漂移";
                    stateColor = "#FFFFB74D";
                    adjustment = $"{GetDirectionSymbol(decision.Direction)} Δ={decision.AdjustmentDelta:F5}mm";
                    adjustmentColor = "#FFFFD54F"; // 黄色
                }
                break;
            case HeadState.Outlier:
                stateText = "🚨 Outlier — 异常";
                stateColor = "#FFEF5350";
                adjustment = "已停机报警";
                adjustmentColor = "#FFEF5350"; // 红色
                break;
            default:
                stateText = "--";
                stateColor = "#FFFFFFFF";
                adjustment = "--";
                adjustmentColor = "#FFFFFFFF";
                break;
        }
    }

    private static string GetDirectionSymbol(AdjustmentDirection dir) => dir switch
    {
        AdjustmentDirection.Up => "↑ 压头上升",
        AdjustmentDirection.Down => "↓ 压头下降",
        _ => "—"
    };

    /// <summary>
    /// 判断某压头决策是否需要在界面上提示预警。
    /// Outlier（安全护栏）、Drift（越界/漂移需调整）、以及发散停机都需提示，
    /// 确保铆头压接偏移在界面上可见，而不仅仅下发到 PLC。
    /// </summary>
    private static bool ShouldAlert(HeadDecision decision) =>
        decision.State == HeadState.Outlier
        || decision.State == HeadState.Drift
        || decision.DivergenceLevel == DivergenceLevel.Stop;

    private void UpdateAlerts(List<string> alerts)
    {
        HasAlert = alerts.Count > 0;
        AlertText = string.Join("\n", alerts);
    }

    private void SetupCharts()
    {
        var axisPaint = new SolidColorPaint(new SKColor(176, 176, 176));
        var labelPaint = new SolidColorPaint(new SKColor(117, 117, 117));
        var separatorPaint = new SolidColorPaint(new SKColor(42, 42, 42));

        TrendYAxes = new[]
        {
            new Axis
            {
                Name = "Height (mm)",
                NamePaint = axisPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint
            }
        };

        TrendXAxes = new[]
        {
            new Axis
            {
                Name = "Samples",
                NamePaint = axisPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint
            }
        };

        DistributionXAxes = new[]
        {
            new Axis
            {
                Name = "Height (mm)",
                NamePaint = axisPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint,
                LabelsRotation = 45
            }
        };

        DistributionYAxes = new[]
        {
            new Axis
            {
                Name = "Count",
                NamePaint = axisPaint,
                LabelsPaint = labelPaint,
                SeparatorsPaint = separatorPaint
            }
        };
    }

    private void UpdateCharts(List<CrimpData> data)
    {
        var validData = data.Where(d => d.IsValid).ToList();
        if (validData.Count == 0) return;

        int trendCount = Math.Min(Settings.Controller.WindowSize, validData.Count);
        TrendChartTitle = $"趋势图 (最近{trendCount}条数据)";
        var trendData = validData.TakeLast(trendCount).ToList();

        var p1Values = trendData.Where(d => d.IsHead1Valid).Select(d => d.Head1Height).ToArray();
        var p2Values = trendData.Where(d => d.IsHead2Valid).Select(d => d.Head2Height).ToArray();

        _p1Series = new LineSeries<double>
        {
            Name = "1高度 (铆压#1)",
            Values = p1Values,
            Stroke = new SolidColorPaint(new SKColor(255, 0, 110)) { StrokeThickness = 2.5f },
            GeometryStroke = null,
            GeometryFill = null,
            GeometrySize = 0,
            LineSmoothness = 0.65,
            IsVisible = IsP1Visible
        };

        _p2Series = new LineSeries<double>
        {
            Name = "2高度 (铆压#2)",
            Values = p2Values,
            Stroke = new SolidColorPaint(new SKColor(131, 56, 236)) { StrokeThickness = 2.5f },
            GeometryStroke = null,
            GeometryFill = null,
            GeometrySize = 0,
            LineSmoothness = 0.65,
            IsVisible = IsP2Visible
        };

        TrendSeries = new ISeries[] { _p1Series, _p2Series };

        // Y轴范围
        var allTrendValues = p1Values.Concat(p2Values).ToList();
        double yMin = allTrendValues.Min() - 0.01;
        double yMax = allTrendValues.Max() + 0.01;

        TrendYAxes = new[]
        {
            new Axis
            {
                Name = "Height (mm)",
                NamePaint = new SolidColorPaint(new SKColor(176, 176, 176)),
                LabelsPaint = new SolidColorPaint(new SKColor(117, 117, 117)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(42, 42, 42)),
                MinLimit = yMin,
                MaxLimit = yMax
            }
        };

        // 参考线
        TrendSections = new[]
        {
            new RectangularSection
            {
                Yi = Settings.Controller.UpperLimit,
                Yj = Settings.Controller.UpperLimit,
                Stroke = new SolidColorPaint(new SKColor(239, 83, 80, 100)) { StrokeThickness = 1 },
                Fill = null
            },
            new RectangularSection
            {
                Yi = Settings.Controller.LowerLimit,
                Yj = Settings.Controller.LowerLimit,
                Stroke = new SolidColorPaint(new SKColor(102, 187, 106, 100)) { StrokeThickness = 1 },
                Fill = null
            },
            new RectangularSection
            {
                Yi = Settings.TargetMidpoint,
                Yj = Settings.TargetMidpoint,
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 60)) { StrokeThickness = 1 },
                Fill = null
            }
        };

        // 分布直方图
        var allProductValues = new List<double>();
        allProductValues.AddRange(validData.Where(d => d.IsHead1Valid).Select(d => d.Head1Height));
        allProductValues.AddRange(validData.Where(d => d.IsHead2Valid).Select(d => d.Head2Height));

        double hMin = allProductValues.Min();
        double hMax = allProductValues.Max();
        if (Math.Abs(hMax - hMin) < 1e-10) { hMin -= 0.5; hMax += 0.5; }
        int hBinCount = 20;
        double hBinWidth = (hMax - hMin) / hBinCount;

        var histCounts = new int[hBinCount];
        foreach (var v in allProductValues)
        {
            int b = (int)((v - hMin) / hBinWidth);
            if (b >= hBinCount) b = hBinCount - 1;
            if (b < 0) b = 0;
            histCounts[b]++;
        }

        var histLabels = new string[hBinCount];
        for (int i = 0; i < hBinCount; i++)
            histLabels[i] = (hMin + (i + 0.5) * hBinWidth).ToString("F3");

        DistributionSeries = new ISeries[]
        {
            new ColumnSeries<int>
            {
                Name = "Count",
                Values = histCounts,
                Fill = new SolidColorPaint(new SKColor(0, 191, 165)),
                Stroke = new SolidColorPaint(new SKColor(0, 191, 165, 150)) { StrokeThickness = 1 }
            }
        };

        DistributionXAxes = new[]
        {
            new Axis
            {
                Name = "Height (mm)",
                NamePaint = new SolidColorPaint(new SKColor(176, 176, 176)),
                LabelsPaint = new SolidColorPaint(new SKColor(117, 117, 117)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(42, 42, 42)),
                Labels = histLabels,
                LabelsRotation = 45,
                MinLimit = hMin - hBinWidth,
                MaxLimit = hMax + hBinWidth
            }
        };

        DistributionYAxes = new[]
        {
            new Axis
            {
                Name = "Count",
                NamePaint = new SolidColorPaint(new SKColor(176, 176, 176)),
                LabelsPaint = new SolidColorPaint(new SKColor(117, 117, 117)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(42, 42, 42)),
                MinLimit = 0
            }
        };

        OnPropertyChanged(nameof(TrendSeries));
        OnPropertyChanged(nameof(TrendSections));
        OnPropertyChanged(nameof(DistributionSeries));
        OnPropertyChanged(nameof(DistributionXAxes));
    }

    #endregion
}
