using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using MainWindow = PatchInstaller.Views.MainWindow;
using MainWindowViewModel = PatchInstaller.ViewModels.MainWindowViewModel;

namespace PatchInstaller;

public class App : Application
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
