using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace CrimpHeightAnalyzer;

public partial class MainWindow : Window
{
    // Win32 常量
    private const int WM_NCHITTEST = 0x0084;
    private const int HTLEFT = 10;
    private const int HTRIGHT = 11;
    private const int HTBOTTOM = 15;
    private const int HTBOTTOMLEFT = 16;
    private const int HTBOTTOMRIGHT = 17;

    private const int ResizeBorder = 6;

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (s, e) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WndProc);
        };

        Loaded += (s, e) =>
        {
            // 根据屏幕工作区自适应窗口大小
            var workArea = SystemParameters.WorkArea;
            double targetWidth = Math.Min(1400, workArea.Width * 0.9);
            double targetHeight = Math.Min(900, workArea.Height * 0.9);

            Width = targetWidth;
            Height = targetHeight;

            // 确保窗口居中
            Left = workArea.Left + (workArea.Width - targetWidth) / 2;
            Top = workArea.Top + (workArea.Height - targetHeight) / 2;

            // 强制刷新布局
            InvalidateMeasure();
            InvalidateArrange();
            UpdateLayout();
        };

        Closing += (s, e) =>
        {
            // 清理 ViewModel 资源
            if (DataContext is ViewModels.MainViewModel viewModel)
            {
                viewModel.CleanupCommand.Execute(null);
            }
        };
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_NCHITTEST)
            return IntPtr.Zero;

        // 最大化时不处理缩放
        if (WindowState == WindowState.Maximized)
            return IntPtr.Zero;

        // lParam 的低 16 位 = 屏幕 X，高 16 位 = 屏幕 Y（物理像素）
        int screenX = (short)(lParam.ToInt32() & 0xFFFF);
        int screenY = (short)(lParam.ToInt32() >> 16);

        // 窗口边界（WPF DIP），需要乘以 DPI 转成物理像素
        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return IntPtr.Zero;

        double dpiX = source.CompositionTarget.TransformToDevice.M11;
        double dpiY = source.CompositionTarget.TransformToDevice.M22;

        // 窗口物理像素边界
        int winLeft = (int)(Left * dpiX);
        int winTop = (int)(Top * dpiY);
        int winRight = (int)((Left + ActualWidth) * dpiX);
        int winBottom = (int)((Top + ActualHeight) * dpiY);

        // 只处理底部和两侧，**不处理顶部**（顶部是我们的标题栏）
        bool onLeft = screenX <= winLeft + ResizeBorder;
        bool onRight = screenX >= winRight - ResizeBorder;
        bool onBottom = screenY >= winBottom - ResizeBorder;

        if (onBottom && onLeft) { handled = true; return new IntPtr(HTBOTTOMLEFT); }
        if (onBottom && onRight) { handled = true; return new IntPtr(HTBOTTOMRIGHT); }
        if (onBottom) { handled = true; return new IntPtr(HTBOTTOM); }
        if (onLeft) { handled = true; return new IntPtr(HTLEFT); }
        if (onRight) { handled = true; return new IntPtr(HTRIGHT); }

        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        // 最大化状态下拖拽：先还原窗口，再定位到鼠标位置
        if (WindowState == WindowState.Maximized)
        {
            // 1. 记录鼠标在最大化窗口内的相对位置和窗口宽度
            var relPos = e.GetPosition(this);
            double maxLeft = Left;
            double maxTop = Top;
            double maxWidth = ActualWidth;

            // 2. 还原窗口（Left/Top/ActualWidth 会变回还原值）
            WindowState = WindowState.Normal;

            // 3. 计算还原后窗口位置：鼠标保持在标题栏同一相对位置
            //    ratioX = 鼠标在标题栏的相对比例
            //    还原后 Left = 最大化Left + 鼠标相对X - 还原宽度 × ratioX
            double ratioX = relPos.X / maxWidth;
            Left = maxLeft + relPos.X - ActualWidth * ratioX;
            Top = maxTop; // 顶部紧贴屏幕顶部
        }

        DragMove();
    }

    private void MinimizeBtn_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void MaximizeBtn_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void CloseBtn_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }
}
