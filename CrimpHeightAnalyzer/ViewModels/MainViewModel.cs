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
using System.Text.Json;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
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
    private readonly CompensationStateService _compensationStateService = new();
    private CsvDataProvider? _dataProvider;
    private IPlcAdapter? _plcAdapter;
    private IDisposable? _plcDisposable;
    
    private DispatcherTimer _refreshTimer = null!;
    private List<CrimpData> _currentData = new();
    private DateTime? _lastAnalyzedDataTimestamp;
    private HeadDecision? _lastHeadADecision;
    private HeadDecision? _lastHeadBDecision;
    private DateTime? _resetTimestamp;

    // P0-2: 每个压头独立的调整时间戳（数据时间轴），用于 bypass 旧数据
    private DateTime? _headAAdjustDataTimestamp;
    private DateTime? _headBAdjustDataTimestamp;

    // P1-1: 上一拍时最后一条数据的时间戳，用于统计新增样本数推进漂移检测拍。
    // 不再用窗口内总条数（窗口填满后不再增长，会导致漂移拍永远不推进）。
    private DateTime? _headALastDriftTimestamp;
    private DateTime? _headBLastDriftTimestamp;

    // 推荐值按钮防重复累加标志：写入 PLC 成功后置 false，允许再次点击
    private bool _headARecommendationApplied;
    private bool _headBRecommendationApplied;

    // WarmUp 时沿用上次的有效偏差值，避免截尾均值=0 导致图表突变尖刺
    private double _lastValidHeadADeviation;
    private double _lastValidHeadBDeviation;

    public MainViewModel()
    {
        LiveCharts.Configure(settings =>
            settings.HasGlobalSKTypeface(SKTypeface.FromFamilyName("Microsoft YaHei")));

        Settings = _settingsService.Load();

        // 从持久化文件恢复手动偏差值
        var compensationState = _compensationStateService.Load();
        HeadAManualDelta = compensationState.HeadAManualDelta;
        HeadBManualDelta = compensationState.HeadBManualDelta;

        InitDataProvider();
        SetupTimer();
        SetupCharts();
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

    #endregion

    #region 运行状态

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualOperationAllowed))]
    [NotifyPropertyChangedFor(nameof(RunButtonText))]
    [NotifyPropertyChangedFor(nameof(RunButtonTooltip))]
    private bool isRunning;

    /// <summary>停止状态且非加载中时，允许手动操作</summary>
    public bool IsManualOperationAllowed => !IsRunning && !IsLoading;

    public string RunButtonText => IsRunning ? "⏹" : "▶";
    public string RunButtonTooltip => IsRunning ? "停止自动运行" : "启动自动运行";

    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(IsManualOperationAllowed));

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

    #region 累计补偿显示

    [ObservableProperty]
    private string headAAccumulatedCompensation = "0.00000";

    [ObservableProperty]
    private string headBAccumulatedCompensation = "0.00000";

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
    private RectangularSection? _trendMidpointSection;

    private const int CompensationHistoryLimit = 300;
    // 以自增索引作为 X 轴（第几次刷新计算），而非时间；仅追踪补偿值波动。
    private readonly ObservableCollection<ObservablePoint> _headACompensationValues = new();
    private readonly ObservableCollection<ObservablePoint> _headBCompensationValues = new();
    private readonly Dictionary<int, DateTime> _compensationTimestamps = new();
    /// <summary>供自定义 Tooltip 读取每个补偿点的记录时间。</summary>
    public IReadOnlyDictionary<int, DateTime> CompensationTimestamps => _compensationTimestamps;
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
    private void ToggleRun()
    {
        if (IsRunning)
        {
            // 停止
            _refreshTimer.Stop();
            IsRunning = false;
            WriteLogToFile("[操作] 用户停止自动运行");
            StatusText = "已停止自动运行";
        }
        else
        {
            // 启动
            _refreshTimer.Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.RefreshIntervalSeconds));
            _refreshTimer.Start();
            IsRunning = true;
            WriteLogToFile("[操作] 用户启动自动运行");
            StatusText = "自动运行中...";
            _ = RefreshDataAsync();
        }
    }

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

            // 按重置时间戳过滤：只保留重置之后产生的数据
            if (_resetTimestamp.HasValue)
                data = data.Where(d => d.RecordedAt.HasValue && d.RecordedAt.Value >= _resetTimestamp.Value).ToList();

            _currentData = data;
            // 诊断：打印首尾时间戳，确认每次读到的数据是否真的不同
            string firstTs = data.Count > 0 ? data[0].Timestamp : "无";
            string lastTs = data.Count > 0 ? data[^1].Timestamp : "无";
            PerformanceText = $"CSV {csvMs} ms | {_dataProvider.LastReadDiagnostics} | 有效 {data.Count} 条 | 首={firstTs} 尾={lastTs}";

            if (_currentData.Count == 0 || !_currentData.Any(d => d.IsValid))
            {
                HasAlert = true;
                AlertText = "未找到压接数据";
                StatusText = "未找到数据";

                progressTimer.Stop();
                totalTimer.Stop();
                PerformanceText = $"CSV {csvMs} ms | {_dataProvider.LastReadDiagnostics} | 无有效数据 | 总计 {totalTimer.ElapsedMilliseconds} ms";
                IsLoading = false;
                return;
            }

            currentPhase = "数据分析/决策";
            PerformanceText = $"CSV {csvMs} ms | 正在分析 {_currentData.Count} 条…";
            var analysisTimer = Stopwatch.StartNew();
            DateTime? currentDataTimestamp = _dataProvider.LastDataTimestamp;

            // P0-2: 每个压头按各自的调整时间戳 bypass 旧数据，只取调整后的新数据
            var headAData = _headAAdjustDataTimestamp.HasValue
                ? _currentData.Where(d => d.RecordedAt.HasValue && d.RecordedAt.Value > _headAAdjustDataTimestamp.Value).ToList()
                : _currentData;
            var headBData = _headBAdjustDataTimestamp.HasValue
                ? _currentData.Where(d => d.RecordedAt.HasValue && d.RecordedAt.Value > _headBAdjustDataTimestamp.Value).ToList()
                : _currentData;

            var head1Values = headAData.Where(d => d.IsHead1Valid).Select(d => d.Head1Height).ToList();
            var head2Values = headBData.Where(d => d.IsHead2Valid).Select(d => d.Head2Height).ToList();

            // P1-1: 用时间戳统计新增样本数来推进漂移检测拍。
            // 不用窗口内总条数（窗口填满后不再增长，漂移拍会永远停住）。
            int driftSampleStride = Math.Max(Settings.Controller.DriftSampleStrideMin, Settings.Controller.MinSamplesPerEpoch / 2);

            int headANewSamples = CountNewSamplesSince(headAData, _headALastDriftTimestamp);
            int headBNewSamples = CountNewSamplesSince(headBData, _headBLastDriftTimestamp);
            bool headAAdvanceDrift = headANewSamples >= driftSampleStride;
            bool headBAdvanceDrift = headBNewSamples >= driftSampleStride;
            if (headAAdvanceDrift && headAData.Count > 0)
                _headALastDriftTimestamp = headAData[^1].RecordedAt;
            if (headBAdvanceDrift && headBData.Count > 0)
                _headBLastDriftTimestamp = headBData[^1].RecordedAt;

            var headAResult = _headAArbiter.ArbitrateSingle("铆压#1", head1Values,
                Settings.Controller, currentDataTimestamp, headAAdvanceDrift);
            var headBResult = _headBArbiter.ArbitrateSingle("铆压#2", head2Values,
                Settings.Controller, currentDataTimestamp, headBAdvanceDrift);

            _lastAnalyzedDataTimestamp = currentDataTimestamp;
            _lastHeadADecision = headAResult.Decision;
            _lastHeadBDecision = headBResult.Decision;

            analysisTimer.Stop();
            analysisMs = analysisTimer.ElapsedMilliseconds;
            PerformanceText = $"CSV {csvMs} ms | 分析 {analysisMs} ms | 正在更新状态/图表…";

            // 收集告警 — Drift 为需要关注的调整预警，在界面可见。
            var alerts = new List<string>();
            if (ShouldAlert(headAResult.Decision))
                alerts.Add($"[铆压#1] {headAResult.Decision.Reason}");
            if (ShouldAlert(headBResult.Decision))
                alerts.Add($"[铆压#2] {headBResult.Decision.Reason}");

            // 发送 PLC 调整指令
            currentPhase = "控制指令下发";
            var controlTimer = Stopwatch.StartNew();
            await SendPlcCommandsAsync(
                currentDataTimestamp,
                headAResult,
                headBResult);

            controlTimer.Stop();
            controlMs = controlTimer.ElapsedMilliseconds;
            PerformanceText = $"CSV {csvMs} ms | 分析 {analysisMs} ms | 控制 {controlMs} ms | 更新图表…";

            currentPhase = "UI/图表更新";
            // 更新UI
            var uiTimer = Stopwatch.StartNew();
            Application.Current.Dispatcher.Invoke(() =>
            {
                UpdateHeadAResults(headAResult);
                UpdateHeadBResults(headBResult);
                UpdateAlerts(alerts);
                UpdateCharts(_currentData);

                // WarmUp 时截尾均值为 0，偏差 = 0 - TargetMidpoint 是假值，
                // 绘制会产生突变尖刺。此时沿用上次的偏差值保持折线连续。
                double headADeviation = headAResult.Decision.State == HeadState.WarmUp
                    ? _lastValidHeadADeviation
                    : headAResult.Deviation;
                double headBDeviation = headBResult.Decision.State == HeadState.WarmUp
                    ? _lastValidHeadBDeviation
                    : headBResult.Deviation;

                // 记住有效偏差值供下次 WarmUp 时使用
                if (headAResult.Decision.State != HeadState.WarmUp)
                    _lastValidHeadADeviation = headAResult.Deviation;
                if (headBResult.Decision.State != HeadState.WarmUp)
                    _lastValidHeadBDeviation = headBResult.Deviation;

                AppendCompensationPoint(
                    ComputeTheoreticalDelta(headADeviation),
                    ComputeTheoreticalDelta(headBDeviation));
                // 更新当前下发值显示：M + N
                double headAAutoN = ComputeTheoreticalDelta(headADeviation);
                double headBAutoN = ComputeTheoreticalDelta(headBDeviation);
                HeadAAccumulatedCompensation = (HeadAManualDelta + headAAutoN).ToString("F5");
                HeadBAccumulatedCompensation = (HeadBManualDelta + headBAutoN).ToString("F5");
                StatusText = $"数据加载完成 - {_currentData.Count}条记录";
                IsLoading = false;
            });
            uiTimer.Stop();
            uiMs = uiTimer.ElapsedMilliseconds;
            progressTimer.Stop();
            totalTimer.Stop();
            PerformanceText = $"CSV {csvMs} ms ({_dataProvider.LastReadDiagnostics}) | 分析 {analysisMs} ms | 控制 {controlMs} ms | UI/图表 {uiMs} ms | 总计 {totalTimer.ElapsedMilliseconds} ms | {data.Count} 条";
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
    /// 推荐值按钮：将当前输入框值 + 自动计算偏差值合并后填入输入框。
    /// 防重复累加：写入 PLC 成功后才允许再次点击。
    /// </summary>
    [RelayCommand]
    private void FillRecommended(string? headParam)
    {
        if (!int.TryParse(headParam, out int headIndex) || headIndex is not (0 or 1))
            return;

        string headName = headIndex == 0 ? "铆压#1" : "铆压#2";

        // 防重复累加：未写入 PLC 前不允许再次点击
        bool alreadyApplied = headIndex == 0 ? _headARecommendationApplied : _headBRecommendationApplied;
        if (alreadyApplied)
        {
            StatusText = $"{headName} 推荐值已填入，请先写入 PLC 后再次使用";
            return;
        }

        // 获取自动计算的偏差调整量（无论 Stable 还是 Drift 都取理论值）
        double autoDelta = 0;
        var decision = headIndex == 0 ? _lastHeadADecision : _lastHeadBDecision;
        if (decision != null)
        {
            if (decision.RequiresAction)
            {
                autoDelta = decision.AdjustmentDelta;
            }
            else
            {
                // Stable 状态也计算理论值供推荐
                var lastResult = headIndex == 0 ? _lastHeadADecision : _lastHeadBDecision;
                // 从最近的分析结果中取偏差，用控制律计算
                // 这里直接用 R2RController 的逻辑：K × deviation，限幅
                autoDelta = ComputeTheoreticalDelta(
                    headIndex == 0
                        ? double.TryParse(HeadADeviation.Replace("+", "").Replace(" mm", ""), out var devA) ? devA : 0
                        : double.TryParse(HeadBDeviation.Replace("+", "").Replace(" mm", ""), out var devB) ? devB : 0);
            }
        }

        double currentManual = headIndex == 0 ? HeadAManualDelta : HeadBManualDelta;
        double combined = Math.Round(currentManual + autoDelta, 5);

        var result = MessageBox.Show(
            $"确认将推荐值填入 {headName} 的手动调整框？\n" +
            $"当前输入: {currentManual:F5}mm\n" +
            $"自动偏差: {autoDelta:F5}mm\n" +
            $"合计: {combined:F5}mm",
            "确认操作", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (result != MessageBoxResult.Yes)
        {
            WriteLogToFile($"[操作] 用户取消填入推荐值 {headName} 合计={combined:F5}mm");
            return;
        }

        WriteLogToFile($"[操作] 用户确认填入推荐值 {headName} 当前={currentManual:F5} + 自动={autoDelta:F5} = {combined:F5}mm");

        if (headIndex == 0)
        {
            HeadAManualDelta = combined;
            _headARecommendationApplied = true;
        }
        else
        {
            HeadBManualDelta = combined;
            _headBRecommendationApplied = true;
        }

        StatusText = $"{headName} 推荐值已填入: {combined:F5}mm";
    }

    /// <summary>
    /// 重置刷新：清空历史数据和分析状态，保留手动调整量，
    /// 从当前时间点重新采集数据进行自动计算。
    /// </summary>
    [RelayCommand]
    private async Task ResetAndRefreshAsync()
    {
        // P1-3: 用数据时间轴，不用上位机时间
        _resetTimestamp = _dataProvider?.LastDataTimestamp ?? DateTime.Now;
        _headAArbiter.Reset();
        _headBArbiter.Reset();
        _lastAnalyzedDataTimestamp = null;
        _lastHeadADecision = null;
        _lastHeadBDecision = null;
        // P0-2: 重置两个压头的调整时间戳（但不清零 CompensationTracker 累计值）
        _headAAdjustDataTimestamp = null;
        _headBAdjustDataTimestamp = null;
        // P1-1: 重置漂移拍时间戳
        _headALastDriftTimestamp = null;
        _headBLastDriftTimestamp = null;
        _compensationIndex = 0;
        _headACompensationValues.Clear();
        _headBCompensationValues.Clear();
        _compensationTimestamps.Clear();

        StatusText = "已重置，从当前时间点重新采集数据";
        await RefreshDataAsync();
    }

    /// <summary>
    /// 手动写入：只写入用户输入的手动调整值到 PLC，写入后自动重置刷新。
    /// </summary>
    [RelayCommand]
    private async Task ManualAdjustAsync(string? headParam)
    {
        WriteLogToFile($"[手动写入] 开始, headParam={headParam}");

        if (!int.TryParse(headParam, out int headIndex) || headIndex is not (0 or 1))
        {
            StatusText = "手动写入失败: 压头参数无效";
            WriteLogToFile($"[手动写入] 拦截: 压头参数无效 headParam={headParam}");
            return;
        }

        var plcAdapter = _plcAdapter;
        if (plcAdapter == null)
        {
            StatusText = "手动写入失败: PLC 未就绪";
            WriteLogToFile("[手动写入] 拦截: _plcAdapter == null");
            return;
        }

        string headName = headIndex == 0 ? "铆压#1" : "铆压#2";
        double manualDelta = headIndex == 0 ? HeadAManualDelta : HeadBManualDelta;
        WriteLogToFile($"[手动写入] {headName} manualDelta={manualDelta:F5}");

        if (!double.IsFinite(manualDelta))
        {
            StatusText = $"手动写入已跳过: {headName} 调整量无效";
            WriteLogToFile($"[手动写入] 拦截: 调整量无效 manualDelta={manualDelta}");
            return;
        }

        string adjustAddress = headIndex == 0 ? Settings.Plc.HeadAAdjustAddress : Settings.Plc.HeadBAdjustAddress;
        var confirmResult = MessageBox.Show(
            $"确认将 {manualDelta:F5}mm 写入 {headName} 的 PLC 地址 {adjustAddress}？",
            "确认写入", MessageBoxButton.YesNo, MessageBoxImage.Warning);

        if (confirmResult != MessageBoxResult.Yes)
        {
            WriteLogToFile($"[手动写入] 用户取消写入 {headName} Δ={manualDelta:F5}mm");
            StatusText = "写入已取消";
            return;
        }

        WriteLogToFile($"[手动写入] 用户确认写入 {headName} Δ={manualDelta:F5}mm → {adjustAddress}");

        try
        {
            // 手动写入：直接写 M（手动偏差值），不累加
            double plcValue = manualDelta;

            StatusText = $"正在写入 {headName}: M={plcValue:F5}mm ...";
            WriteLogToFile($"[手动写入] 调用 SendAdjustmentAsync({headIndex}, {plcValue:F5}) (M={manualDelta:F5})");
            bool ok = await plcAdapter.SendAdjustmentAsync(headIndex, plcValue);
            WriteLogToFile($"[手动写入] SendAdjustmentAsync 返回: {ok}");
            if (ok)
            {
                // P0-2: 手动调整后也 bypass 旧数据
                var currentDataTimestamp = _dataProvider?.LastDataTimestamp;
                if (headIndex == 0)
                {
                    _headAAdjustDataTimestamp = currentDataTimestamp;
                    _headARecommendationApplied = false; // 写入成功，允许再次点击推荐值
                    _headAArbiter.Reset();
                    _headALastDriftTimestamp = null; // bypass 后重新统计新增
                }
                else
                {
                    _headBAdjustDataTimestamp = currentDataTimestamp;
                    _headBRecommendationApplied = false;
                    _headBArbiter.Reset();
                    _headBLastDriftTimestamp = null;
                }

                SaveCompensationState();

                StatusText = $"写入成功: {headName} M={plcValue:F5}mm，等待新数据...";
                WriteLogToFile($"[手动写入] 成功，已设置 bypass 时间戳，等待 warmup");
            }
            else
            {
                StatusText = $"写入失败: {headName} 未能写入 PLC（检查连接/地址）";
            }
        }
        catch (Exception ex)
        {
            WriteLogToFile($"[手动写入] 异常: {ex}");
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
        _headAAdjustDataTimestamp = null;
        _headBAdjustDataTimestamp = null;
        _headALastDriftTimestamp = null;
        _headBLastDriftTimestamp = null;
        _compensationIndex = 0;
        _headACompensationValues.Clear();
        _headBCompensationValues.Clear();
        _compensationTimestamps.Clear();
        InitPlcAdapter();
    }

    private async Task<(bool HeadASent, bool HeadBSent)> SendPlcCommandsAsync(
        DateTime? dataTimestamp,
        HeadAnalysisResult headAResult,
        HeadAnalysisResult headBResult)
    {
        var plcAdapter = _plcAdapter;
        if (plcAdapter == null) return (false, false);

        bool headASent = await SendSingleHeadAsync(plcAdapter, 0, dataTimestamp, headAResult,
            _headAArbiter, v => _headAAdjustDataTimestamp = v);
        bool headBSent = await SendSingleHeadAsync(plcAdapter, 1, dataTimestamp, headBResult,
            _headBArbiter, v => _headBAdjustDataTimestamp = v);

        return (headASent, headBSent);
    }

    /// <summary>
    /// 单个压头的自动下发逻辑。
    /// PLC 收到的值 = M(手动偏差) + N(自动计算偏差)，不累加历史。
    /// 节能模式：仅 Drift 时下发；非节能模式：Stable 也下发微小偏差。
    /// </summary>
    private async Task<bool> SendSingleHeadAsync(
        IPlcAdapter plcAdapter,
        int headIndex,
        DateTime? dataTimestamp,
        HeadAnalysisResult result,
        DecisionArbiter arbiter,
        Action<DateTime?> setAdjustTimestamp)
    {
        var decision = result.Decision;
        string headName = headIndex == 0 ? "铆压#1" : "铆压#2";
        double manualDelta = headIndex == 0 ? HeadAManualDelta : HeadBManualDelta;

        // 确定本次自动调整量 N
        double autoDelta;
        bool shouldSend;

        if (decision.RequiresAction)
        {
            // Drift 状态：使用仲裁结果
            autoDelta = decision.AdjustmentDelta;
            shouldSend = true;
        }
        else if (!Settings.EnergySavingMode && decision.State == HeadState.Stable)
        {
            // 非节能模式 + Stable：计算微小偏差（跳过死区，直接 K × deviation 限幅）
            autoDelta = ComputeTheoreticalDelta(result.Deviation);
            shouldSend = true;
        }
        else
        {
            // 节能模式 Stable 或 WarmUp：不下发
            return false;
        }

        if (!shouldSend) return false;

        // PLC 收到的值 = M(手动偏差) + N(自动偏差)，不累加
        double plcValue = manualDelta + autoDelta;

        string mode = decision.RequiresAction ? "Drift" : "Stable(非节能)";
        WriteLogToFile($"[自动下发] {headName} [{mode}]: M={manualDelta:F5} + N={autoDelta:F5} = {plcValue:F5}mm");
        bool sent = await plcAdapter.SendAdjustmentAsync(headIndex, plcValue);
        WriteLogToFile($"[自动下发] {headName} 结果: {(sent ? "成功" : "失败")}");

        if (sent)
        {
            // Drift 下发成功：记录调整时间戳 bypass 旧数据 + 重置漂移历史 + 清零漂移拍计数
            if (decision.RequiresAction)
            {
                setAdjustTimestamp(dataTimestamp);
                arbiter.Reset();
                // bypass 后重新统计新增，漂移拍时间戳清空
                if (headIndex == 0) _headALastDriftTimestamp = null;
                else _headBLastDriftTimestamp = null;
            }
            // 非节能模式 Stable 下发不 bypass（不需要冷却期，每次都发）
        }

        return sent;
    }

    /// <summary>
    /// P0-1: 持久化累计补偿状态
    /// </summary>
    private void SaveCompensationState()
    {
        _compensationStateService.Save(new CompensationState
        {
            HeadAManualDelta = HeadAManualDelta,
            HeadBManualDelta = HeadBManualDelta,
            LastSavedAt = DateTime.Now
        });
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
            keyenceAdapter.OnLog += msg =>
            {
                System.Diagnostics.Debug.WriteLine(msg);
                WriteLogToFile(msg);
            };
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
            mockAdapter.OnLog += msg =>
            {
                System.Diagnostics.Debug.WriteLine(msg);
                WriteLogToFile(msg);
            };
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

    private static readonly object _logLock = new();
    private static readonly string _logPath = Path.Combine(
        AppDomain.CurrentDomain.BaseDirectory, "log");

    private static void WriteLogToFile(string message)
    {
        try
        {
            Directory.CreateDirectory(_logPath);
            string fileName = $"plc_{DateTime.Now:yyyyMMdd}.log";
            string filePath = Path.Combine(_logPath, fileName);
            string line = $"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}";
            lock (_logLock)
            {
                File.AppendAllText(filePath, line);
            }
        }
        catch
        {
            // 日志写入失败不影响主流程
        }
    }

    private void SetupTimer()
    {
        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(1, Settings.RefreshIntervalSeconds))
        };
        _refreshTimer.Tick += async (s, e) => await RefreshDataAsync();
        // 不自动启动，由用户点击启动按钮控制
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

        // 校验刷新间隔与数据产生间隔的关系：
        // 每次刷新新增条数 = RefreshInterval / DataInterval
        // 漂移拍推进门限 = Max(10, MinSamplesPerEpoch / 2)
        // 新增条数必须 ≥ 门限，否则漂移检测永远不推进
        if (Settings.Controller.DataIntervalSeconds > 0)
        {
            double newSamplesPerRefresh = Settings.RefreshIntervalSeconds / Settings.Controller.DataIntervalSeconds;
            int driftStride = Math.Max(Settings.Controller.DriftSampleStrideMin, Settings.Controller.MinSamplesPerEpoch / 2);
            if (newSamplesPerRefresh < driftStride)
            {
                double minRefreshInterval = driftStride * Settings.Controller.DataIntervalSeconds;
                throw new InvalidOperationException(
                    $"刷新间隔 {Settings.RefreshIntervalSeconds}s 内新增数据约 {newSamplesPerRefresh:F0} 条，" +
                    $"不足漂移检测推进门限 {driftStride} 条。" +
                    $"请增大刷新间隔（≥{minRefreshInterval:F0}s）或减小 MinSamplesPerEpoch。");
            }
        }

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
                || string.IsNullOrWhiteSpace(Settings.Plc.HeadAStatusAddress)
                || string.IsNullOrWhiteSpace(Settings.Plc.HeadBStatusAddress))
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

        UpdateStateDisplay(result.Decision, result.Deviation, out string stateText, out string stateColor, out string adjustment, out string adjustmentColor);
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

        UpdateStateDisplay(result.Decision, result.Deviation, out string stateText, out string stateColor, out string adjustment, out string adjustmentColor);
        HeadBState = stateText;
        HeadBStateColor = stateColor;
        HeadBAdjustment = adjustment;
        HeadBAdjustmentColor = adjustmentColor;
    }

    private void UpdateStateDisplay(HeadDecision decision, double deviation,
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
                double stableDelta = ComputeTheoreticalDelta(deviation);
                adjustment = $"Δ={stableDelta:F5}mm (稳定)";
                adjustmentColor = "#FF66BB6A"; // 绿色
                break;
            case HeadState.Drift:
                if (decision.DivergenceLevel == DivergenceLevel.Stop)
                {
                    double theoreticalDelta = ComputeTheoreticalDelta(deviation);
                    var dir = R2RController.GetDirection(theoreticalDelta);
                    stateText = "⛔ Diverged — 发散";
                    stateColor = "#FFEF5350";
                    adjustment = $"{GetDirectionSymbol(dir)} Δ={theoreticalDelta:F5}mm (发散)";
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
            default:
                stateText = "--";
                stateColor = "#FFFFFFFF";
                adjustment = "--";
                adjustmentColor = "#FFFFFFFF";
                break;
        }
    }

    /// <summary>
    /// 统计指定时间戳之后的新增样本数。用于漂移拍推进判定。
    /// 首次调用（sinceTimestamp == null）时返回全部条数。
    /// </summary>
    private static int CountNewSamplesSince(List<CrimpData> data, DateTime? sinceTimestamp)
    {
        if (!sinceTimestamp.HasValue || data.Count == 0)
            return data.Count;

        return data.Count(d => d.RecordedAt.HasValue && d.RecordedAt.Value > sinceTimestamp.Value);
    }

    /// <summary>
    /// 根据偏差计算理论调整量（用于 Outlier/发散等不实际下发但需要显示的场景）。
    /// </summary>
    private double ComputeTheoreticalDelta(double deviation)
    {
        double rawDelta = Settings.Controller.ControllerGain * deviation;
        return Math.Clamp(rawDelta, -Settings.Controller.MaxStepPerAdjust, Settings.Controller.MaxStepPerAdjust);
    }

    private static string GetDirectionSymbol(AdjustmentDirection dir) => dir switch
    {
        AdjustmentDirection.Up => "↑ 压头上升",
        AdjustmentDirection.Down => "↓ 压头下降",
        _ => "—"
    };

    /// <summary>
    /// 判断某压头决策是否需要在界面上提示预警。
    /// Drift（越界/漂移需调整）需提示，确保铆头压接偏移在界面上可见。
    /// </summary>
    private static bool ShouldAlert(HeadDecision decision) =>
        decision.State == HeadState.Drift;

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
        _trendMidpointSection = MakeSection(
            new SolidColorPaint(new SKColor(255, 255, 255, 60)) { StrokeThickness = 1 });

        TrendSections = new[]
        {
            _trendUpperLimitSection,
            _trendLowerLimitSection,
            _trendMidpointSection
        };
    }

    private void SetupCompensationChart()
    {
        var headAColor = new SKColor(66, 165, 245);
        var headBColor = new SKColor(255, 183, 77);

        CompensationSeries = new ISeries[]
        {
            new CompensationLineSeries(_compensationTimestamps)
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
            new CompensationLineSeries(_compensationTimestamps)
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
                // 固定范围 -0.1~0.1，每 0.02mm 一个刻度，共 10 格
                Labeler = value => value.ToString("F3"),
                MinStep = 0.02,
                MinLimit = -0.1,
                MaxLimit = 0.1
            }
        };

        // y=0 参考线，便于观察补偿值正负波动。
        CompensationSections = new[]
        {
            new RectangularSection
            {
                Yi = 0,
                Yj = 0,
                Stroke = new SolidColorPaint(new SKColor(255, 255, 255, 150))
                {
                    StrokeThickness = 1.5f,
                    PathEffect = new DashEffect(new float[] { 6, 4 })
                },
                Fill = null
            }
        };
    }

    private void AppendCompensationPoint(double headAValue, double headBValue)
    {
        double x = ++_compensationIndex;
        _compensationTimestamps[_compensationIndex] = DateTime.Now;
        _headACompensationValues.Add(new ObservablePoint(x, headAValue));
        _headBCompensationValues.Add(new ObservablePoint(x, headBValue));

        while (_headACompensationValues.Count > CompensationHistoryLimit)
        {
            var removed = _headACompensationValues[0];
            int removedIndex = (int)(removed.X ?? 0);
            _compensationTimestamps.Remove(removedIndex);
            _headACompensationValues.RemoveAt(0);
        }
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

        // Y 轴固定为 -0.1 ~ 0.1 mm，与实际补偿值调整范围匹配，
        // 使微小波动在图表上清晰可见，不再动态缩放。
        CompensationYAxes[0].MinLimit = -0.1;
        CompensationYAxes[0].MaxLimit = 0.1;

        // X 轴：只显示当前可见的点范围，避免索引一直增长导致图表拉伸
        if (CompensationXAxes.Length > 0 && _headACompensationValues.Count > 0)
        {
            double xMin = _headACompensationValues[0].X ?? 0;
            double xMax = _headACompensationValues[^1].X ?? 0;
            CompensationXAxes[0].MinLimit = xMin;
            CompensationXAxes[0].MaxLimit = xMax;
        }
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
        if (_trendMidpointSection != null)
            _trendMidpointSection.Yi = _trendMidpointSection.Yj = Settings.TargetMidpoint;
    }

    /// <summary>
    /// 计算趋势图 Y 轴范围：以规格上下限为核心视窗，
    /// 用分位数过滤极端值，保证正常波动占满可视区。
    /// </summary>
    private (double Min, double Max) ComputeRobustTrendRange(List<double> values)
    {
        var ctrl = Settings.Controller;

        // 核心视窗：规格上下限外扩余量，保证规格线不贴边。
        double specSpan = ctrl.UpperLimit - ctrl.LowerLimit;
        double margin = Math.Max(specSpan * 0.15, 0.02);
        double yMin = ctrl.LowerLimit - margin;
        double yMax = ctrl.UpperLimit + margin;

        if (values.Count > 0)
        {
            var sorted = values.OrderBy(v => v).ToList();

            // 用 2%/98% 分位数裁掉极端值，防止拉伸。
            double low = Percentile(sorted, 0.02);
            double high = Percentile(sorted, 0.98);

            yMin = Math.Min(yMin, low - 0.005);
            yMax = Math.Max(yMax, high + 0.005);
        }

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

/// <summary>
/// 自定义 LineSeries，覆盖 tooltip 文本以显示记录时间。
/// </summary>
internal class CompensationLineSeries : LineSeries<ObservablePoint>
{
    private readonly Dictionary<int, DateTime> _timestamps;

    public CompensationLineSeries(Dictionary<int, DateTime> timestamps)
    {
        _timestamps = timestamps;
    }

    public override string GetSecondaryToolTipText(ChartPoint point)
    {
        double x = point.Coordinate.SecondaryValue;
        int idx = (int)x;
        if (_timestamps.TryGetValue(idx, out var ts))
            return $"#{idx}  {ts:yyyy-MM-dd HH:mm:ss}";
        return $"#{idx}";
    }
}
