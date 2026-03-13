using System;
using System.Diagnostics;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MainWindowViewModel = PatchInstaller.ViewModels.MainWindowViewModel;

namespace PatchInstaller.Views;

public partial class MainWindow : Window
{
    private const string ProjectGithubUrl = "https://github.com/greepar/PatchInstaller";

    public MainWindow()
    {
        InitializeComponent();
        Title = InstallerBuildConfig.ProductName;
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
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择游戏目录",
            AllowMultiple = false
        });

        var folder = folders.FirstOrDefault();
        if (folder is null)
        {
            return;
        }

        viewModel.GamePath = folder.TryGetLocalPath() ?? folder.Path.LocalPath;
        viewModel.StatusText = "已手动选择游戏目录";
        viewModel.Step2Status = "已定位";
        viewModel.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 已手动选择游戏目录: {viewModel.GamePath}");
    }

    private async void ManualSelectPatch(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not MainWindowViewModel viewModel)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择补丁压缩包",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("支持的补丁格式")
                {
                    Patterns = ["*.7z", "*.zip", "*.rar"]
                }
            ]
        });

        var file = files.FirstOrDefault();
        if (file is null)
        {
            return;
        }

        viewModel.LocalPatchPath = file.TryGetLocalPath() ?? file.Path.LocalPath;
        viewModel.SelectedPatchSource = MainWindowViewModel.LocalSource;
        viewModel.StatusText = "已手动选择补丁";
        viewModel.Step1Status = "已选择";
        viewModel.Logs.Add($"[{DateTime.Now:HH:mm:ss}] 已手动选择补丁: {viewModel.LocalPatchPath}");
    }
}
