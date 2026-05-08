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
    public sealed class ManualUpdateCheckDialog(
        SelectableTextBlock statusText,
        ProgressBar progressBar,
        TextBlock progressText,
        Button updateButton,
        Button okButton,
        string md5,
        string platform)
    {
        private string? _downloadUrl;
        private CancellationTokenSource? _updateCancellationTokenSource;
        private bool _isUpdating;

        public void AttachHandlers()
        {
            okButton.Click += (_, _) =>
            {
                if (_isUpdating) return;
                MainWindow.DialogManager.DismissDialog();
            };

            updateButton.Click += async (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(_downloadUrl)) return;

                if (_isUpdating)
                {
                    updateButton.IsEnabled = false;
                    statusText.Text = LocalizationService.Get("CancelingUpdate");
                    _updateCancellationTokenSource?.Cancel();
                    return;
                }

                _isUpdating = true;
                okButton.IsVisible = false;
                updateButton.Content = LocalizationService.Get("Cancel");
                progressBar.IsVisible = true;
                progressText.IsVisible = true;
                statusText.Text = GetManualUpdateCheckText(md5, platform, LocalizationService.Get("DownloadingUpdate"));
                _updateCancellationTokenSource = new CancellationTokenSource();

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
                        _downloadUrl,
                        progress,
                        _updateCancellationTokenSource.Token);

                    statusText.Text = GetManualUpdateCheckText(md5, platform, LocalizationService.Get("DownloadedUpdate"));
                    MainWindow.DialogManager.DismissDialog();
                }
                catch (OperationCanceledException)
                {
                    MainWindow.DialogManager.DismissDialog();
                }
                catch (Exception ex)
                {
                    _isUpdating = false;
                    statusText.Text = GetManualUpdateCheckText(md5, platform, $"{LocalizationService.Get("UpdateFailed")}\n{ex.Message}");
                    progressBar.IsVisible = false;
                    progressText.IsVisible = false;
                    progressText.Text = string.Empty;
                    updateButton.Content = LocalizationService.Get("UpdateNow");
                    updateButton.IsEnabled = true;
                    okButton.IsVisible = true;
                }
                finally
                {
                    _updateCancellationTokenSource?.Dispose();
                    _updateCancellationTokenSource = null;
                }
            };
        }

        public void CancelUpdate()
        {
            _updateCancellationTokenSource?.Cancel();
        }

        public async Task SetResultAsync(UpdateCheckResult result)
        {
            var updateStatusText = GetUpdateCheckStatusText(result);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                statusText.Text = GetManualUpdateCheckText(md5, platform, updateStatusText);
                _downloadUrl = result.IsAvailable ? result.DownloadUrl : null;
                updateButton.IsVisible = !string.IsNullOrWhiteSpace(_downloadUrl);
                okButton.IsVisible = true;
            });
        }

        public async Task SetFailedAsync(string message)
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                statusText.Text = GetManualUpdateCheckText(
                    md5,
                    platform,
                    $"{LocalizationService.Get("UpdateCheckFailed")}\n{message}");
                _downloadUrl = null;
                updateButton.IsVisible = false;
                okButton.IsVisible = true;
            });
        }
    }

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

    public static async Task<ManualUpdateCheckDialog> ShowManualUpdateCheckProgressAsync(string md5, string platform)
    {
        return await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var statusText = new SelectableTextBlock
            {
                Text = GetManualUpdateCheckText(md5, platform, LocalizationService.Get("CheckingUpdate")),
                TextWrapping = Avalonia.Media.TextWrapping.Wrap
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

            var okButton = new Button
            {
                Content = LocalizationService.Get("Ok"),
                MinWidth = 96
            };

            var updateButton = new Button
            {
                Content = LocalizationService.Get("UpdateNow"),
                MinWidth = 96,
                IsVisible = false,
                Classes = { "Primary" }
            };

            var buttonPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Spacing = 10,
                Children =
                {
                    okButton,
                    updateButton
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

            var dialog = new ManualUpdateCheckDialog(statusText, progressBar, progressText, updateButton, okButton, md5, platform);
            dialog.AttachHandlers();

            _ = MainWindow.DialogManager
                .CreateDialog()
                .OfType(NotificationType.Information)
                .WithTitle(LocalizationService.Get("ProgramUpdateCheck"))
                .WithContent(content)
                .OnDismissed(_ => dialog.CancelUpdate())
                .TryShowAsync();

            return dialog;
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

    private static string GetManualUpdateCheckText(string md5, string platform, string statusText)
    {
        return $"{LocalizationService.Get("CurrentMd5")}\n{md5}\n\n{LocalizationService.Get("Platform")} {platform}\n\n{statusText}";
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
