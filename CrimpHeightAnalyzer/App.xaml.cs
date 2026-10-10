using System.Configuration;
using System.Data;
using System.Windows;

namespace CrimpHeightAnalyzer;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnExit(ExitEventArgs e)
    {
        // 清理主窗口的 ViewModel 资源
        if (MainWindow?.DataContext is ViewModels.MainViewModel viewModel)
        {
            viewModel.CleanupCommand.Execute(null);
        }

        base.OnExit(e);
    }
}

