using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LiveChartsCore;
using LiveChartsCore.Defaults;
using LiveChartsCore.Kernel;
using LiveChartsCore.Kernel.Sketches;
using CrimpHeightAnalyzer.ViewModels;

namespace CrimpHeightAnalyzer.Views;

public partial class CompensationTooltip : UserControl, IChartTooltip
{
    public CompensationTooltip()
    {
        InitializeComponent();
    }

    public void Show(IEnumerable<ChartPoint> foundPoints, Chart chart)
    {
        TooltipContent.Children.Clear();

        // 从主窗口 DataContext 获取 ViewModel 以读取时间戳
        var vm = Application.Current.MainWindow?.DataContext as MainViewModel;

        int? sharedIndex = null;

        foreach (var point in foundPoints)
        {
            var series = point.Context.Series;

            double x = 0, y = 0;
            if (point.Context.DataSource is ObservablePoint op)
            {
                x = op.X ?? 0;
                y = op.Y ?? 0;
            }
            else
            {
                x = point.Coordinate.SecondaryValue;
                y = point.Coordinate.PrimaryValue;
            }

            int idx = (int)x;
            if (!sharedIndex.HasValue) sharedIndex = idx;

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 2, 0, 2) };

            var colorIndicator = new Border
            {
                Width = 12, Height = 3,
                CornerRadius = new CornerRadius(1),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Background = series.Name?.Contains("#1") == true
                    ? new SolidColorBrush(Color.FromRgb(66, 165, 245))
                    : new SolidColorBrush(Color.FromRgb(255, 183, 77))
            };

            var text = new TextBlock
            {
                Text = $"{series.Name}: {y:F5} mm",
                Foreground = new SolidColorBrush(Color.FromRgb(230, 230, 230)),
                FontSize = 12
            };

            row.Children.Add(colorIndicator);
            row.Children.Add(text);
            TooltipContent.Children.Add(row);
        }

        // 时间行
        if (sharedIndex.HasValue)
        {
            string timeStr = "--";
            if (vm != null && vm.CompensationTimestamps.TryGetValue(sharedIndex.Value, out var ts))
                timeStr = ts.ToString("yyyy-MM-dd HH:mm:ss");

            var timeRow = new TextBlock
            {
                Text = $"时间: {timeStr}",
                Foreground = new SolidColorBrush(Color.FromRgb(160, 160, 160)),
                FontSize = 11,
                Margin = new Thickness(0, 4, 0, 0)
            };
            TooltipContent.Children.Add(timeRow);
        }
    }

    public void Hide(Chart chart)
    {
        TooltipContent.Children.Clear();
    }
}
