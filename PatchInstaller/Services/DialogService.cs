using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.Notifications;
using Avalonia.Threading;
using SukiUI.Dialogs;

namespace PatchInstaller.Services;

public static class DialogService
{
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
                    TextWrapping = Avalonia.Media.TextWrapping.Wrap
                })
                .WithOkResult("确定")
                .TryShowAsync();
        });
    }
}
