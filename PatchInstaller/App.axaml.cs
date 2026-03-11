using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia_NativeAOT_SingleFile.ViewModels;
using Avalonia_NativeAOT_SingleFile.Views;

namespace Avalonia_NativeAOT_SingleFile;

public partial class App : Application
{
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.MainWindow = new MainWindow
            {
                DataContext = new MainWindowViewModel()
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
