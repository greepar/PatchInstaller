using System;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SukiUI.Dialogs;

namespace PatchInstaller.Services;

public static class DialogService
{
    public static async Task ShowSuccessAsync()
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Success)
                .WithTitle(LocalizationService.Get("SuccessTitle"))
                .WithContent(new SelectableTextBlock
                {
                    Text = LocalizationService.Get("SuccessMessage"),
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                })
                .WithOkResult(LocalizationService.Get("Ok"))
                .TryShowAsync();
        });
    }
    
    public static async Task ShowErrorAsync(string title, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Error)
                .WithTitle(title)
                .WithContent(new SelectableTextBlock
                {
                    Text = message,
                    TextWrapping = TextWrapping.Wrap
                })
                .WithOkResult(LocalizationService.Get("Ok"))
                .TryShowAsync();
        });
    }

    public static async Task<bool> ShowUpdateAvailableAsync(string downloadUrl)
    {
        return await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var completion = new TaskCompletionSource<bool>();
            var isUpdating = false;
            CancellationTokenSource? updateCancellationTokenSource = null;

            var statusText = new SelectableTextBlock
            {
                Text = $"{LocalizationService.Get("UpdateAvailable")}\n{downloadUrl}",
                TextWrapping = TextWrapping.Wrap
            };

            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Height = 12,
                IsVisible = false
            };

            var progressText = new TextBlock
            {
                Text = string.Empty,
                IsVisible = false
            };

            var updateButton = new Button
            {
                Content = LocalizationService.Get("UpdateNow"),
                MinWidth = 96,
                Classes = { "Primary" }
            };

            var laterButton = new Button
            {
                Content = LocalizationService.Get("UpdateLater"),
                MinWidth = 96
            };

            var cancelButton = new Button
            {
                Content = LocalizationService.Get("Cancel"),
                MinWidth = 96,
                IsVisible = false
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children =
                {
                    laterButton,
                    updateButton,
                    cancelButton
                }
            };

            var content = new StackPanel
            {
                MinWidth = 420,
                Spacing = 12,
                Children =
                {
                    statusText,
                    progressBar,
                    progressText,
                    buttonPanel
                }
            };

            laterButton.Click += (_, _) =>
            {
                if (isUpdating) return;

                completion.TrySetResult(false);
                MainWindow.DialogManager.DismissDialog();
            };

            cancelButton.Click += (_, _) =>
            {
                if (!isUpdating) return;

                cancelButton.IsEnabled = false;
                statusText.Text = LocalizationService.Get("CancelingUpdate");
                updateCancellationTokenSource?.Cancel();
            };

            updateButton.Click += async (_, _) =>
            {
                if (isUpdating) return;
                isUpdating = true;

                updateButton.IsVisible = false;
                laterButton.IsVisible = false;
                cancelButton.IsVisible = true;
                progressBar.IsVisible = true;
                progressText.IsVisible = true;
                statusText.Text = LocalizationService.Get("DownloadingUpdate");
                updateCancellationTokenSource = new CancellationTokenSource();

                try
                {
                    var progress = new Progress<SelfUpdateProgress>(report =>
                    {
                        progressBar.Value = report.ProgressPercent;
                            progressText.Text = report.TotalBytes is > 0
                                ? $"{LocalizationService.Get("DownloadingBytes")} {report.ProgressPercent:0.0}% ({FormatBytes(report.DownloadedBytes)} / {FormatBytes(report.TotalBytes.Value)})"
                                : $"{LocalizationService.Get("DownloadingBytes")} {FormatBytes(report.DownloadedBytes)}";
                    });

                    await UpdateService.PrepareAndLaunchAsync(
                        downloadUrl,
                        progress,
                        updateCancellationTokenSource.Token);

                    statusText.Text = LocalizationService.Get("DownloadedUpdate");
                    completion.TrySetResult(true);
                    MainWindow.DialogManager.DismissDialog();
                }
                catch (OperationCanceledException)
                {
                    completion.TrySetResult(false);
                    MainWindow.DialogManager.DismissDialog();
                }
                catch (Exception ex)
                {
                    isUpdating = false;
                    statusText.Text = $"{LocalizationService.Get("UpdateFailed")}\n{ex.Message}";
                    progressText.Text = string.Empty;
                    progressBar.IsVisible = false;
                    cancelButton.IsVisible = false;
                    cancelButton.IsEnabled = true;
                    laterButton.IsVisible = true;
                    updateButton.IsVisible = true;
                }
                finally
                {
                    updateCancellationTokenSource?.Dispose();
                    updateCancellationTokenSource = null;
                }
            };

            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Information)
                .WithTitle(LocalizationService.Get("UpdateAvailableTitle"))
                .WithContent(content)
                .OnDismissed(_ =>
                {
                    updateCancellationTokenSource?.Cancel();
                    completion.TrySetResult(false);
                })
                .TryShowAsync();

            return await completion.Task;
        });
    }

    public static async Task ShowManualUpdateCheckAsync(string md5, string platform, UpdateCheckResult result)
    {
        var statusText = GetUpdateCheckStatusText(result);

        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Information)
                .WithTitle(LocalizationService.Get("ProgramUpdateCheck"))
                .WithContent(new SelectableTextBlock
                {
                    Text = $"{LocalizationService.Get("CurrentMd5")}\n{md5}\n\n{LocalizationService.Get("Platform")} {platform}\n\n{statusText}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                })
                .WithOkResult(LocalizationService.Get("Ok"))
                .TryShowAsync();
        });
    }

    public static async Task ShowManualUpdateInfoAsync(string md5, string platform)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Information)
                .WithTitle(LocalizationService.Get("ProgramUpdateCheck"))
                .WithContent(new SelectableTextBlock
                {
                    Text = $"{LocalizationService.Get("CurrentMd5")}\n{md5}\n\n{LocalizationService.Get("Platform")} {platform}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                })
                .WithOkResult(LocalizationService.Get("Ok"))
                .TryShowAsync();
        });
    }

    public static async Task ShowManualUpdateCheckFailedAsync(string md5, string platform, string message)
    {
        await Dispatcher.UIThread.InvokeAsync(async () =>
        {
            await MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Error)
                .WithTitle(LocalizationService.Get("ProgramUpdateCheck"))
                .WithContent(new SelectableTextBlock
                {
                    Text = $"{LocalizationService.Get("CurrentMd5")}\n{md5}\n\n{LocalizationService.Get("Platform")} {platform}\n\n{LocalizationService.Get("UpdateCheckFailed")}\n{message}",
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                })
                .WithOkResult(LocalizationService.Get("Ok"))
                .TryShowAsync();
        });
    }

    private static string GetUpdateCheckStatusText(UpdateCheckResult result)
    {
        if (!result.IsAvailable) return LocalizationService.Get("UpdateUnavailable");

        return string.IsNullOrWhiteSpace(result.DownloadUrl)
            ? LocalizationService.Get("LatestVersion")
            : $"{LocalizationService.Get("UpdateAvailable")}\n{result.DownloadUrl}";
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.##} {units[unitIndex]}";
    }
}
