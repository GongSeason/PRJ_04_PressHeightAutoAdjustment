using System.Collections.ObjectModel;
using System.Diagnostics;
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
using LiveChartsCore.SkiaSharpView.Painting.Effects;
using LiveChartsCore.SkiaSharpView.VisualElements;
using SkiaSharp;

namespace CrimpHeightAnalyzer.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DecisionArbiter _headAArbiter = new();
    private readonly DecisionArbiter _headBArbiter = new();
    private readonly SettingsService _settingsService = new();
    private readonly StopStateService _stopStateService = new();
    // 已确认停机水位线（分压头）。持久化到 stop_state.json，重启后仍有效，
    // 用于识别"又读到停机当时或更早的历史不良数据"并避免重复停机。
    private StopState _stopState = new();
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
        _stopState = _stopStateService.Load();
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

    [ObservableProperty]
    private string performanceText = "等待首次刷新";

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

    #region 手动调整

    // 手动写入的增量值 (mm)，正=下降，负=上升；与自动下发语义一致。
    [ObservableProperty]
    private double headAManualDelta;

    [ObservableProperty]
    private double headBManualDelta;

    // 上下微调按钮的步进量 (mm)。
    private const double ManualDeltaStep = 0.001;

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

    // 趋势图参考线持久化：只建一次，之后仅更新 Y 值，避免每次刷新重建导致虚线样式丢失。
    private RectangularSection? _trendUpperLimitSection;
    private RectangularSection? _trendLowerLimitSection;
    private RectangularSection? _trendSafetyUpperSection;
    private RectangularSection? _trendSafetyLowerSection;
    private RectangularSection? _trendMidpointSection;

    private const int CompensationHistoryLimit = 300;
    // 以自增索引作为 X 轴（第几次刷新计算），而非时间；仅追踪补偿值波动。
    private readonly ObservableCollection<ObservablePoint> _headACompensationValues = new();
    private readonly ObservableCollection<ObservablePoint> _headBCompensationValues = new();
    private int _compensationIndex;

    public ISeries[] DistributionSeries { get; set; } = Array.Empty<ISeries>();
    public ISeries[] TrendSeries { get; set; } = Array.Empty<ISeries>();
    public Axis[] TrendXAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] TrendYAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] DistributionXAxes { get; set; } = Array.Empty<Axis>();
    public Axis[] DistributionYAxes { get; set; } = Array.Empty<Axis>();
    public RectangularSection[] TrendSections { get; set; } = Array.Empty<RectangularSection>();
    public ISeries[] CompensationSeries { get; private set; } = Array.Empty<ISeries>();
    public Axis[] CompensationXAxes { get; private set; } = Array.Empty<Axis>();
    public Axis[] CompensationYAxes { get; private set; } = Array.Empty<Axis>();
    public RectangularSection[] CompensationSections { get; private set; } = Array.Empty<RectangularSection>();

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
        PerformanceText = "开始刷新…";
        var totalTimer = Stopwatch.StartNew();
        string currentPhase = "配置校验";
        var progressTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        progressTimer.Tick += (_, _) =>
            PerformanceText = $"阶段：{currentPhase} | 本次刷新已耗时 {totalTimer.Elapsed.TotalSeconds:F1} 秒";
        progressTimer.Start();
        long csvMs = 0, analysisMs = 0, controlMs = 0, uiMs = 0;

        try
        {
            ValidateSettings();

            currentPhase = "CSV扫描/读取";
            PerformanceText = "正在扫描并读取 CSV…";
            var csvTimer = Stopwatch.StartNew();
            var data = await _dataProvider.GetLatestDataAsync(Settings.Controller.WindowSize);
            csvTimer.Stop();
            csvMs = csvTimer.ElapsedMilliseconds;
            _currentData = data;
            PerformanceText = $"CSV {csvMs} ms | {_dataProvider.LastReadDiagnostics} | 读取 {data.Count} 条";

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

                progressTimer.Stop();
                totalTimer.Stop();
                PerformanceText = $"CSV {csvMs} ms | {_dataProvider.LastReadDiagnostics} | 无有效数据 | 总计 {totalTimer.ElapsedMilliseconds} ms";
                IsLoading = false;
                return;
            }

            _dataUnavailableSince = null;

            currentPhase = "数据分析/决策";
            PerformanceText = $"CSV {csvMs} ms | 正在分析 {_currentData.Count} 条…";
            var analysisTimer = Stopwatch.StartNew();
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

            // 停机水位线闸门（分压头）：
            // 若某压头本拍是"数据内容触发"的停机（Outlier/发散 Stop），
            // 且当前窗口最新时间戳 <= 该压头已确认停机水位线，说明只是重启后
            // 又读到停机当时或更早的历史不良数据，并非新生产的不良 → 判为历史回放，
            // 跳过该压头的停机下发；出现晚于水位线的新数据时自动解除。
            bool headAStaleStopReplay = IsStaleStopReplay(
                headAResult.Decision, currentDataTimestamp, _stopState.HeadA);
            bool headBStaleStopReplay = IsStaleStopReplay(
                headBResult.Decision, currentDataTimestamp, _stopState.HeadB);
            analysisTimer.Stop();
            analysisMs = analysisTimer.ElapsedMilliseconds;
            PerformanceText = $"CSV {csvMs} ms | 分析 {analysisMs} ms | 正在更新状态/图表…";

            // 收集告警 — Outlier/Stop 为报警，Drift 为需要关注的调整预警，均需在界面可见。
            var alerts = new List<string>();
            if (ShouldAlert(headAResult.Decision))
                alerts.Add(headAStaleStopReplay
                    ? "[铆压#1] 检测到历史停机数据，等待新生产数据"
                    : $"[铆压#1] {headAResult.Decision.Reason}");
            if (ShouldAlert(headBResult.Decision))
                alerts.Add(headBStaleStopReplay
                    ? "[铆压#2] 检测到历史停机数据，等待新生产数据"
                    : $"[铆压#2] {headBResult.Decision.Reason}");

            // 发送 PLC 指令 — 停机优先于调整。历史回放的停机不下发到 PLC。
            currentPhase = "控制/位置读取（当前模拟模式应很快）";
            var controlTimer = Stopwatch.StartNew();
            await SendPlcCommandsAsync(
                currentDataTimestamp,
                headAResult.Decision,
                headBResult.Decision,
                headAStaleStopReplay,
                headBStaleStopReplay);

            // 新触发的内容停机（非历史回放）推进并持久化水位线。
            UpdateStopWatermarks(
                currentDataTimestamp,
                headAResult.Decision, headAStaleStopReplay,
                headBResult.Decision, headBStaleStopReplay);

            // 读取压头位置
            double? headAPosition = null;
            double? headBPosition = null;
            if (_plcAdapter != null)
            {
                headAPosition = await _plcAdapter.ReadHeadPositionAsync(0);
                headBPosition = await _plcAdapter.ReadHeadPositionAsync(1);
            }

            controlTimer.Stop();
            controlMs = controlTimer.ElapsedMilliseconds;
            PerformanceText = $"CSV {csvMs} ms | 分析 {analysisMs} ms | 控制/状态读取 {controlMs} ms | 更新图表…";

            currentPhase = "UI/图表更新";
            // 更新UI
            var uiTimer = Stopwatch.StartNew();
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateHeadAResults(headAResult);
                UpdateHeadBResults(headBResult);
                UpdateAlerts(alerts);
                UpdateCharts(_currentData);
                AppendCompensationPoint(
                    GetCompensationChartValue(headAResult),
                    GetCompensationChartValue(headBResult));
                UpdateHeadPositions(headAPosition, headBPosition);
                StatusText = $"数据加载完成 - {_currentData.Count}条记录";
                IsLoading = false;
            });
            uiTimer.Stop();
            uiMs = uiTimer.ElapsedMilliseconds;
            progressTimer.Stop();
            totalTimer.Stop();
            PerformanceText = $"CSV {csvMs} ms ({_dataProvider.LastReadDiagnostics}) | 分析 {analysisMs} ms | 控制/状态 {controlMs} ms | UI/图表 {uiMs} ms | 总计 {totalTimer.ElapsedMilliseconds} ms | {data.Count} 条";
        }
        catch (Exception ex)
        {
            progressTimer.Stop();
            totalTimer.Stop();
            PerformanceText = $"刷新失败 | 已耗时 {totalTimer.ElapsedMilliseconds} ms | {ex.Message}";
            StatusText = $"加载失败: {ex.Message}";
            IsLoading = false;
        }
    }

    private void UpdateHeadPositions(double? headAPosition, double? headBPosition)
    {
        HeadACurrentPosition = headAPosition.HasValue ? $"{headAPosition.Value:F5}mm" : "--";
        HeadBCurrentPosition = headBPosition.HasValue ? $"{headBPosition.Value:F5}mm" : "--";
    }

    /// <summary>
    /// 上下微调按钮：对指定压头的手动增量做 +/- 步进。
    /// 参数格式 "头索引:方向"，方向 +1/-1，例如 "0:1"=压头A 增、"1:-1"=压头B 减。
    /// </summary>
    [RelayCommand]
    private void StepManualDelta(string? param)
    {
        if (string.IsNullOrWhiteSpace(param)) return;
        var parts = param.Split(':');
        if (parts.Length != 2
            || !int.TryParse(parts[0], out int headIndex)
            || !int.TryParse(parts[1], out int direction)
            || headIndex is not (0 or 1))
        {
            return;
        }

        double step = Math.Sign(direction) * ManualDeltaStep;
        if (headIndex == 0)
            HeadAManualDelta = Math.Round(HeadAManualDelta + step, 5);
        else
            HeadBManualDelta = Math.Round(HeadBManualDelta + step, 5);
    }

    /// <summary>
    /// 手动把一个增量值写入对应压头的调整地址（人工干预通道）。
    /// 复用 SendAdjustmentAsync，与自动下发共用 ×100000 取整、日志等逻辑。
    /// </summary>
    [RelayCommand]
    private async Task ManualAdjustAsync(string? headParam)
    {
        if (_dataProvider == null) return;

        if (!int.TryParse(headParam, out int headIndex) || headIndex is not (0 or 1))
        {
            StatusText = "手动写入失败: 压头参数无效";
            return;
        }

        var plcAdapter = _plcAdapter;
        if (plcAdapter == null)
        {
            StatusText = "手动写入失败: PLC 未就绪";
            return;
        }

        string headName = headIndex == 0 ? "铆压#1" : "铆压#2";
        double delta = headIndex == 0 ? HeadAManualDelta : HeadBManualDelta;

        if (!double.IsFinite(delta) || delta == 0)
        {
            StatusText = $"手动写入已跳过: {headName} 调整量为 0 或无效";
            return;
        }

        // 安全护栏：单次手动增量不得超过单步限幅，避免误操作一次调过头。
        double maxStep = Settings.Controller.MaxStepPerAdjust;
        if (Math.Abs(delta) > maxStep)
        {
            StatusText = $"手动写入被拒绝: {headName} Δ={delta:F5}mm 超过单步限幅 ±{maxStep:F5}mm";
            return;
        }

        try
        {
            StatusText = $"正在手动写入 {headName} Δ={delta:F5}mm ...";
            bool ok = await plcAdapter.SendAdjustmentAsync(headIndex, delta);
            StatusText = ok
                ? $"手动写入成功: {headName} Δ={delta:F5}mm"
                : $"手动写入失败: {headName} 未能写入 PLC（检查连接/地址）";
        }
        catch (Exception ex)
        {
            StatusText = $"手动写入异常: {ex.Message}";
        }
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
        _compensationIndex = 0;
        _headACompensationValues.Clear();
        _headBCompensationValues.Clear();
        InitPlcAdapter();
    }

    private async Task<(bool HeadASent, bool HeadBSent)> SendPlcCommandsAsync(
        DateTime? dataTimestamp,
        HeadDecision headADecision,
        HeadDecision headBDecision,
        bool headAStaleStopReplay = false,
        bool headBStaleStopReplay = false)
    {
        var plcAdapter = _plcAdapter;
        if (plcAdapter == null) return (false, false);

        PreparePlcCommandVersion(dataTimestamp);

        // 历史回放的停机不计入"需要停机"，避免重启后读到旧不良再次下发停机。
        bool headAStopEffective = headADecision.RequiresStop && !headAStaleStopReplay;
        bool headBStopEffective = headBDecision.RequiresStop && !headBStaleStopReplay;

        if (headAStopEffective || headBStopEffective)
        {
            string stopKey = $"stop|{headAStopEffective}|{headBStopEffective}";
            string reason = headAStopEffective
                ? headADecision.Reason
                : headBDecision.Reason;
            await SendStopOnceAsync(dataTimestamp, stopKey, reason);
            return (false, false);
        }

        // 两个压头都处于"历史回放停机"时，不下发调整，维持停机等待状态。
        if (headADecision.RequiresStop || headBDecision.RequiresStop)
            return (false, false);

        bool headASent = false;
        bool headBSent = false;

        if (headADecision.RequiresAction)
        {
            string headAKey = FormattableString.Invariant(
                $"adjust|0|{headADecision.AdjustmentDelta:R}");
            if (!_issuedPlcCommandKeys.Contains(headAKey))
            {
                // 仅在写入成功后记录，失败时保留重试机会，避免调整量被漏发。
                headASent = await plcAdapter.SendAdjustmentAsync(0, headADecision.AdjustmentDelta);
                if (headASent)
                    _issuedPlcCommandKeys.Add(headAKey);
            }
        }

        if (headBDecision.RequiresAction)
        {
            string headBKey = FormattableString.Invariant(
                $"adjust|1|{headBDecision.AdjustmentDelta:R}");
            if (!_issuedPlcCommandKeys.Contains(headBKey))
            {
                headBSent = await plcAdapter.SendAdjustmentAsync(1, headBDecision.AdjustmentDelta);
                if (headBSent)
                    _issuedPlcCommandKeys.Add(headBKey);
            }
        }

        return (headASent, headBSent);
    }

    /// <summary>
    /// 判断某压头本拍是否为"历史停机数据回放"：
    /// 决策为内容触发停机（Outlier/发散 Stop），且该压头已有水位线，
    /// 且当前窗口最新数据时间戳不晚于水位线（即没有晚于停机点的新数据）。
    /// 时间戳缺失时保守处理为非回放（照常停机）。
    /// </summary>
    private static bool IsStaleStopReplay(
        HeadDecision decision,
        DateTime? currentDataTimestamp,
        HeadStopState headStopState)
    {
        if (!decision.RequiresStop) return false;
        if (!headStopState.LastStopDataTimestamp.HasValue) return false;
        if (!currentDataTimestamp.HasValue) return false;

        return currentDataTimestamp.Value <= headStopState.LastStopDataTimestamp.Value;
    }

    /// <summary>
    /// 推进并持久化停机水位线：仅对"新触发、非历史回放"的内容停机更新。
    /// 任一压头有更新即落盘一次。
    /// </summary>
    private void UpdateStopWatermarks(
        DateTime? currentDataTimestamp,
        HeadDecision headADecision, bool headAStaleStopReplay,
        HeadDecision headBDecision, bool headBStaleStopReplay)
    {
        bool changed = false;

        if (headADecision.RequiresStop && !headAStaleStopReplay && currentDataTimestamp.HasValue)
        {
            _stopState.HeadA.LastStopDataTimestamp = currentDataTimestamp;
            _stopState.HeadA.Reason = headADecision.Reason;
            _stopState.HeadA.RecordedAt = DateTime.Now;
            changed = true;
        }

        if (headBDecision.RequiresStop && !headBStaleStopReplay && currentDataTimestamp.HasValue)
        {
            _stopState.HeadB.LastStopDataTimestamp = currentDataTimestamp;
            _stopState.HeadB.Reason = headBDecision.Reason;
            _stopState.HeadB.RecordedAt = DateTime.Now;
            changed = true;
        }

        if (changed)
            _stopStateService.Save(_stopState);
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

        SetupTrendSections();
        SetupCompensationChart();
    }

    /// <summary>
    /// 创建趋势图参考线（只执行一次）：
    /// 规格上下限=绿色虚线，安全上下限=红色实线，目标中点=白色细线。
    /// 之后每次刷新只更新各自的 Yi/Yj，避免重建导致虚线效果丢失或闪烁。
    /// </summary>
    private void SetupTrendSections()
    {
        RectangularSection MakeSection(SolidColorPaint stroke) => new()
        {
            Yi = 0,
            Yj = 0,
            Stroke = stroke,
            Fill = null
        };

        _trendUpperLimitSection = MakeSection(new SolidColorPaint(new SKColor(102, 187, 106))
        {
            StrokeThickness = 1.5f,
            PathEffect = new DashEffect(new float[] { 6, 4 })
        });
        _trendLowerLimitSection = MakeSection(new SolidColorPaint(new SKColor(102, 187, 106))
        {
            StrokeThickness = 1.5f,
            PathEffect = new DashEffect(new float[] { 6, 4 })
        });
        _trendSafetyUpperSection = MakeSection(
            new SolidColorPaint(new SKColor(239, 83, 80)) { StrokeThickness = 1.5f });
        _trendSafetyLowerSection = MakeSection(
            new SolidColorPaint(new SKColor(239, 83, 80)) { StrokeThickness = 1.5f });
        _trendMidpointSection = MakeSection(
            new SolidColorPaint(new SKColor(255, 255, 255, 60)) { StrokeThickness = 1 });

        TrendSections = new[]
        {
            _trendUpperLimitSection,
            _trendLowerLimitSection,
            _trendSafetyUpperSection,
            _trendSafetyLowerSection,
            _trendMidpointSection
        };
    }

    private void SetupCompensationChart()
    {
        var headAColor = new SKColor(66, 165, 245);
        var headBColor = new SKColor(255, 183, 77);

        CompensationSeries = new ISeries[]
        {
            new LineSeries<ObservablePoint>
            {
                Name = "铆压#1 补偿值",
                Values = _headACompensationValues,
                Stroke = new SolidColorPaint(headAColor) { StrokeThickness = 2 },
                Fill = null,
                GeometrySize = 5,
                GeometryFill = new SolidColorPaint(headAColor),
                GeometryStroke = null,
                LineSmoothness = 0
            },
            new LineSeries<ObservablePoint>
            {
                Name = "铆压#2 补偿值",
                Values = _headBCompensationValues,
                Stroke = new SolidColorPaint(headBColor) { StrokeThickness = 2 },
                Fill = null,
                GeometrySize = 5,
                GeometryFill = new SolidColorPaint(headBColor),
                GeometryStroke = null,
                LineSmoothness = 0
            }
        };

        CompensationXAxes = new[]
        {
            new Axis
            {
                Name = "索引",
                NamePaint = new SolidColorPaint(new SKColor(176, 176, 176)),
                LabelsPaint = new SolidColorPaint(new SKColor(117, 117, 117)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(42, 42, 42)),
                MinStep = 1,
                Labeler = value => value.ToString("F0")
            }
        };

        CompensationYAxes = new[]
        {
            new Axis
            {
                Name = "补偿值 (mm)",
                NamePaint = new SolidColorPaint(new SKColor(176, 176, 176)),
                LabelsPaint = new SolidColorPaint(new SKColor(117, 117, 117)),
                SeparatorsPaint = new SolidColorPaint(new SKColor(42, 42, 42)),
                // PLC 写入分辨率为 1e-5 mm；默认格式会把微小补偿刻度舍入成 0。
                Labeler = value => value.ToString("F5"),
                MinStep = 0.00001
            }
        };

        // y=0 参考线，便于观察补偿值正负波动。
        CompensationSections = new[]
        {
            new RectangularSection
            {
                Yi = 0,
                Yj = 0,
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 60)) { StrokeThickness = 1 },
                Fill = null
            }
        };
    }

    /// <summary>
    /// 返回补偿图表的理论值，不代表 PLC 实际下发值。
    /// 仅在 WarmUp（样本不足）时显示决策值；其余有分析结果的状态，
    /// 按偏差 × 增益计算并限幅，使 Stable/停机状态也能显示有意义的候选补偿值。
    /// PLC 下发仍由 SendPlcCommandsAsync 根据决策状态控制。
    /// </summary>
    private double GetCompensationChartValue(HeadAnalysisResult result)
    {
        if (result.Decision.State == HeadState.WarmUp)
            return result.Decision.AdjustmentDelta;

        // Drift 实际动作使用仲裁结果（包含降 K 等控制策略）；停机/稳定才显示理论候选值。
        if (result.Decision.RequiresAction)
            return result.Decision.AdjustmentDelta;

        double theoreticalDelta = result.Deviation * Settings.Controller.ControllerGain;
        return double.IsFinite(theoreticalDelta)
            ? Math.Clamp(theoreticalDelta,
                -Settings.Controller.MaxStepPerAdjust,
                Settings.Controller.MaxStepPerAdjust)
            : 0;
    }

    private void AppendCompensationPoint(double headAValue, double headBValue)
    {
        double x = ++_compensationIndex;
        _headACompensationValues.Add(new ObservablePoint(x, headAValue));
        _headBCompensationValues.Add(new ObservablePoint(x, headBValue));

        while (_headACompensationValues.Count > CompensationHistoryLimit)
            _headACompensationValues.RemoveAt(0);
        while (_headBCompensationValues.Count > CompensationHistoryLimit)
            _headBCompensationValues.RemoveAt(0);

        UpdateCompensationAxisRange();
    }

    /// <summary>
    /// 根据当前补偿值历史动态缩放纵轴，避免 Stable 状态下的微小非零理论补偿
    /// 因为量级较小而视觉上贴近 0 轴。
    /// </summary>
    private void UpdateCompensationAxisRange()
    {
        if (CompensationYAxes.Length == 0) return;

        var values = _headACompensationValues
            .Concat(_headBCompensationValues)
            .Select(point => point.Y)
            .Where(value => value.HasValue && double.IsFinite(value.Value))
            .Select(value => value!.Value)
            .ToList();

        double min = values.Count == 0 ? 0 : values.Min();
        double max = values.Count == 0 ? 0 : values.Max();
        double range = max - min;
        double magnitude = Math.Max(Math.Abs(min), Math.Abs(max));
        double padding = Math.Max(range * 0.1, magnitude * 0.1);
        if (padding < 0.00001) padding = 0.00001;

        CompensationYAxes[0].MinLimit = min - padding;
        CompensationYAxes[0].MaxLimit = max + padding;
    }

    /// <summary>
    /// 刷新趋势图参考线的 Y 值（规格/安全上下限、目标中点），
    /// 不重建 Section 实例，保证虚线与实线样式持久不丢失。
    /// </summary>
    private void UpdateTrendSectionValues()
    {
        var ctrl = Settings.Controller;

        if (_trendUpperLimitSection != null)
            _trendUpperLimitSection.Yi = _trendUpperLimitSection.Yj = ctrl.UpperLimit;
        if (_trendLowerLimitSection != null)
            _trendLowerLimitSection.Yi = _trendLowerLimitSection.Yj = ctrl.LowerLimit;
        if (_trendSafetyUpperSection != null)
            _trendSafetyUpperSection.Yi = _trendSafetyUpperSection.Yj = ctrl.SafetyUpperBound;
        if (_trendSafetyLowerSection != null)
            _trendSafetyLowerSection.Yi = _trendSafetyLowerSection.Yj = ctrl.SafetyLowerBound;
        if (_trendMidpointSection != null)
            _trendMidpointSection.Yi = _trendMidpointSection.Yj = Settings.TargetMidpoint;
    }

    /// <summary>
    /// 计算趋势图 Y 轴范围：以安全带（安全上下限 + 少量余量）为核心固定视窗，
    /// 只在安全带内按分位数扩展，彻底忽略落在安全带之外的异常值。
    /// 这样 outlier 不会参与缩放，正常波动始终占满可视区，
    /// 且安全带红线与规格带橙线都稳定可见。
    /// </summary>
    private (double Min, double Max) ComputeRobustTrendRange(List<double> values)
    {
        var ctrl = Settings.Controller;

        // 核心视窗：安全带外扩一段余量，保证安全上下限红线不贴边、清晰可见。
        // 余量取安全带宽度的 8%（至少 0.02mm），让红线稳定落在可视区内。
        double safetySpan = ctrl.SafetyUpperBound - ctrl.SafetyLowerBound;
        double margin = Math.Max(safetySpan * 0.08, 0.02);
        double yMin = ctrl.SafetyLowerBound - margin;
        double yMax = ctrl.SafetyUpperBound + margin;

        if (values.Count > 0)
        {
            var sorted = values.OrderBy(v => v).ToList();

            // 只看落在安全带内的点；安全带外即为真正的 outlier，不参与缩放。
            var inSafety = sorted
                .Where(v => v >= ctrl.SafetyLowerBound && v <= ctrl.SafetyUpperBound)
                .ToList();
            if (inSafety.Count == 0) inSafety = sorted;

            // 用 2%/98% 分位数裁掉安全带内的零星极值，进一步防止拉伸。
            double low = Percentile(inSafety, 0.02);
            double high = Percentile(inSafety, 0.98);

            // 允许数据在核心视窗基础上小幅外扩，但不会超出安全带硬裁剪。
            yMin = Math.Min(yMin, low - 0.005);
            yMax = Math.Max(yMax, high + 0.005);
        }

        // 最终硬性裁剪到安全带，杜绝任何异常点把轴撑开。
        yMin = Math.Max(yMin, ctrl.SafetyLowerBound - margin);
        yMax = Math.Min(yMax, ctrl.SafetyUpperBound + margin);

        if (yMax - yMin < 0.01)
        {
            yMin -= 0.01;
            yMax += 0.01;
        }

        return (yMin, yMax);
    }

    /// <summary>对已升序排列的数据做线性插值分位数（p 取 0~1）。</summary>
    private static double Percentile(IReadOnlyList<double> sorted, double p)
    {
        if (sorted.Count == 1) return sorted[0];

        double rank = p * (sorted.Count - 1);
        int lowIndex = (int)Math.Floor(rank);
        int highIndex = (int)Math.Ceiling(rank);
        double fraction = rank - lowIndex;
        return sorted[lowIndex] + (sorted[highIndex] - sorted[lowIndex]) * fraction;
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
            Stroke = new SolidColorPaint(new SKColor(66, 165, 245)) { StrokeThickness = 2.5f },
            // 显式设为 null：否则 LiveCharts 每次重建会从主题调色板轮换一个半透明填充色，
            // 导致线下空白区的颜色每次刷新都在变。
            Fill = null,
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
            Stroke = new SolidColorPaint(new SKColor(255, 183, 77)) { StrokeThickness = 2.5f },
            Fill = null,
            GeometryStroke = null,
            GeometryFill = null,
            GeometrySize = 0,
            LineSmoothness = 0.65,
            IsVisible = IsP2Visible
        };

        TrendSeries = new ISeries[] { _p1Series, _p2Series };

        // Y轴范围 — 用稳健范围（忽略离群值），避免个别 outlier 把整个趋势压扁。
        // 用户更关注规格带附近的正常波动，而非异常值。
        var allTrendValues = p1Values.Concat(p2Values).ToList();
        var (yMin, yMax) = ComputeRobustTrendRange(allTrendValues);

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

        // 参考线：复用持久化的 Section 实例，只更新 Y 值，保持虚线/实线样式稳定不闪烁。
        UpdateTrendSectionValues();

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
        OnPropertyChanged(nameof(TrendYAxes));
        OnPropertyChanged(nameof(TrendSections));
        OnPropertyChanged(nameof(DistributionSeries));
        OnPropertyChanged(nameof(DistributionXAxes));
    }

    #endregion
}
