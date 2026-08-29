using System.Diagnostics;
using System.Linq;
using System;
using System.Globalization;
using System.Threading.Tasks;
using Avalonia.Animation.Easings;
using Avalonia.Interactivity;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using PatchInstaller.Services;
using SukiUI.Controls;
using SukiUI.Dialogs;

namespace PatchInstaller;

public partial class MainWindow : SukiWindow
{
    private const string ProjectGithubUrl = "https://github.com/greepar/PatchInstaller";
    private int _versionClickCount;
    private bool _isManualUpdateCheckRunning;
    private DateTime _lastVersionClickAt = DateTime.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        DialogHost.Manager = DialogManager;
        LanguageComboBox.SelectedIndex = GetDefaultLanguageIndex();
    }

    public static ISukiDialogManager DialogManager { get; } = new SukiDialogManager();

    private async void ConfirmLanguageSelection(object? sender, RoutedEventArgs e)
    {
        LanguageComboBox.IsEnabled = false;
        if (sender is Avalonia.Controls.Control control) control.IsEnabled = false;

        var language = LanguageComboBox.SelectedIndex switch
        {
            1 => AppLanguage.TraditionalChinese,
            2 => AppLanguage.English,
            _ => AppLanguage.SimplifiedChinese
        };

        await SelectLanguageAsync(language);
    }

    private static int GetDefaultLanguageIndex()
    {
        var cultureName = CultureInfo.CurrentUICulture.Name;
        if (cultureName.Equals("zh-TW", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-HK", StringComparison.OrdinalIgnoreCase) ||
            cultureName.Equals("zh-MO", StringComparison.OrdinalIgnoreCase) ||
            cultureName.StartsWith("zh-Hant", StringComparison.OrdinalIgnoreCase))
        {
            return 1;
        }

        return cultureName.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 2;
    }

    private async Task SelectLanguageAsync(AppLanguage language)
    {
        LocalizationService.CurrentLanguage = language;
        DataContext = new MainWindowViewModel();
        LanguageOverlay.IsHitTestVisible = false;

        const int steps = 26;
        var easing = new CubicEaseIn();
        for (var i = 1; i <= steps; i++)
        {
            var progress = i / (double)steps;
            LanguageOverlay.Opacity = 1d - easing.Ease(progress);
            await Task.Delay(24);
        }

        LanguageOverlay.Opacity = 0;
        LanguageOverlay.IsVisible = false;
        MainContent.Opacity = 1;
        MainContent.IsHitTestVisible = true;
    }

    private void OpenProjectGithub(object? sender, RoutedEventArgs e)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = ProjectGithubUrl,
            UseShellExecute = true
        });
    }

    private async void ManualLocateGamePath(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = LocalizationService.Get("GamePathWatermark"),
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null) return;

        viewModel.GamePath = folder.TryGetLocalPath() ?? folder.Path.LocalPath;
        viewModel.StatusText = LocalizationService.Get("Located");
        viewModel.Step1Status = LocalizationService.Get("Located");
    }

    private async void ManualSelectPatch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = LocalizationService.Get("SelectPatch"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(LocalizationService.Get("PatchWatermark"))
                {
                    Patterns = ["*.7z", "*.zip", "*.rar", "*.zip.001", "*.rar.001"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null) return;

        viewModel.LocalPatchPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        viewModel.SelectLocalPatchSource();
        viewModel.StatusText = LocalizationService.Get("Selected");
        viewModel.Step2Status = LocalizationService.Get("Selected");
    }

    private async void VersionTextPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (now - _lastVersionClickAt > TimeSpan.FromSeconds(2))
        {
            _versionClickCount = 0;
        }

        _lastVersionClickAt = now;
        _versionClickCount++;
        e.Handled = true;

        if (_versionClickCount < 3 || _isManualUpdateCheckRunning) return;

        _versionClickCount = 0;
        _isManualUpdateCheckRunning = true;

        var md5 = UpdateService.GetCurrentMd5() ?? LocalizationService.Get("Failed");
        var platform = UpdateService.GetPlatformName();

        if (!InstallerBuildConfig.HasCheckUpdateApi)
        {
            await DialogService.ShowManualUpdateInfoAsync(md5, platform);
            _isManualUpdateCheckRunning = false;
            return;
        }

        var updateCheckDialog = await DialogService.ShowManualUpdateCheckProgressAsync(md5, platform);

        try
        {
            var result = await UpdateService.CheckAsync(default);
            await updateCheckDialog.SetResultAsync(result);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
            await updateCheckDialog.SetFailedAsync(ex.Message);
        }
        finally
        {
            _isManualUpdateCheckRunning = false;
        }
    }
}
