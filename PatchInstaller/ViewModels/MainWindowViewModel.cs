using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Avalonia_NativeAOT_SingleFile.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    public const string DownloadSource = "下载链接";
    public const string LocalSource = "本地补丁";

    private static readonly string[] SupportedPatchExtensions = [".7z", ".zip", ".rar"];
    private static readonly string DefaultPatchUrl = InstallerBuildConfig.DefaultPatchUrl;
    private const int ParallelDownloadSegments = 8;
    private const int DownloadRetryCount = 3;
    private const double MinWindowHeightValue = 480;
    private const double MinWindowWidthValue = 640;

    private CancellationTokenSource? _installCancellationTokenSource;

    [ObservableProperty] private string _patchUrl = DefaultPatchUrl;
    [ObservableProperty] private string _gamePath = string.Empty;
    [NotifyPropertyChangedFor(nameof(IsLocalPatchReady))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowLocalInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowSelectPatchButton))]
    [ObservableProperty] private string _localPatchPath = string.Empty;
    [ObservableProperty] private string _selectedPatchSource = DownloadSource;
    [ObservableProperty] private string _statusText = "准备就绪";
    [ObservableProperty] private string _downloadText = "等待开始";
    [ObservableProperty] private string _downloadSpeedText = string.Empty;
    [ObservableProperty] private string _step1Status = "未执行";
    [ObservableProperty] private string _step2Status = "未执行";
    [ObservableProperty] private string _step3Status = "未执行";
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _canInstall = true;

    public ObservableCollection<string> Logs { get; } = [];
    public ObservableCollection<string> PatchSourceOptions { get; } = [DownloadSource, LocalSource];

    public bool UseDownloadSource => string.Equals(SelectedPatchSource, DownloadSource, StringComparison.Ordinal);
    public bool UseLocalSource => string.Equals(SelectedPatchSource, LocalSource, StringComparison.Ordinal);
    public bool IsLocalPatchReady => IsSupportedPatchFile(LocalPatchPath) && File.Exists(NormalizePath(LocalPatchPath));
    public bool ShowDownloadInstallButton => UseDownloadSource;
    public bool ShowLocalInstallButton => UseLocalSource && IsLocalPatchReady;
    public bool ShowSelectPatchButton => UseLocalSource;
    public bool CanCancelInstall => IsBusy;

    public string ProductName => InstallerBuildConfig.ProductName;
    public static double MinWindowHeight => MinWindowHeightValue;
    public static double MinWindowWidth => MinWindowWidthValue;

    public MainWindowViewModel()
    {
        GamePath = NormalizePath(GetDetectedGamePath());
        if (!string.IsNullOrWhiteSpace(GamePath))
        {
            Step2Status = "已定位";
            AddLog($"已自动定位游戏目录: {GamePath}");
        }
        else
        {
            AddLog("未自动定位到游戏目录，请手动确认目录。");
        }

        var autoPatch = FindAutoSelectedPatchPath();
        if (!string.IsNullOrWhiteSpace(autoPatch))
        {
            LocalPatchPath = autoPatch;
            SelectedPatchSource = LocalSource;
            AddLog($"检测到本地补丁文件: {LocalPatchPath}");
        }
    }

    private static string? GetDetectedGamePath()
    {
        return OperatingSystem.IsWindows()
            ? SteamLocator.FindGamePath()
            : null;
    }

    partial void OnSelectedPatchSourceChanged(string value)
    {
        OnPropertyChanged(nameof(UseDownloadSource));
        OnPropertyChanged(nameof(UseLocalSource));
        OnPropertyChanged(nameof(IsLocalPatchReady));
        OnPropertyChanged(nameof(ShowDownloadInstallButton));
        OnPropertyChanged(nameof(ShowLocalInstallButton));
        OnPropertyChanged(nameof(ShowSelectPatchButton));
    }

    partial void OnLocalPatchPathChanged(string value)
    {
        OnPropertyChanged(nameof(IsLocalPatchReady));
        OnPropertyChanged(nameof(ShowLocalInstallButton));
    }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCancelInstall));
    }

    [RelayCommand]
    private void CancelInstall()
    {
        if (!IsBusy)
        {
            return;
        }

        _installCancellationTokenSource?.Cancel();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy)
        {
            return;
        }

        var localPatch = NormalizePath(LocalPatchPath);
        var useLocalPatch = UseLocalSource && IsSupportedPatchFile(localPatch) && File.Exists(localPatch);
        Uri? patchUri = null;

        if (!useLocalPatch)
        {
            if (!UseDownloadSource)
            {
                StatusText = "请先选择本地补丁";
                AddLog("本地补丁模式下尚未选择有效压缩包。");
                return;
            }

            if (!Uri.TryCreate(PatchUrl, UriKind.Absolute, out patchUri) ||
                (patchUri.Scheme != Uri.UriSchemeHttp && patchUri.Scheme != Uri.UriSchemeHttps))
            {
                StatusText = "补丁链接无效";
                AddLog($"补丁链接无效: {PatchUrl}");
                return;
            }
        }

        GamePath = NormalizePath(GamePath);
        if (!Directory.Exists(GamePath))
        {
            StatusText = "游戏目录不存在";
            Step2Status = "未找到";
            AddLog($"游戏目录不存在: {GamePath}");
            return;
        }

        _installCancellationTokenSource = new CancellationTokenSource();

        IsBusy = true;
        CanInstall = false;
        DownloadProgress = 0;
        DownloadText = "准备处理";
        DownloadSpeedText = string.Empty;
        Step1Status = "进行中";
        Step2Status = "已定位";
        Step3Status = "未执行";
        StatusText = "开始安装";

        var workingRoot = Path.Combine(Path.GetTempPath(), "PatchInstaller");
        var extractPath = Path.Combine(workingRoot, "extracted");
        var downloadFileName = patchUri is null
            ? string.Empty
            : await ResolveDownloadFileNameAsync(patchUri, _installCancellationTokenSource.Token);
        var temporaryDownloadPath = patchUri is null
            ? string.Empty
            : Path.Combine(workingRoot, GetTemporaryDownloadFileName(downloadFileName, patchUri));

        try
        {
            PrepareWorkingDirectory(workingRoot, extractPath);

            string archivePath;
            if (useLocalPatch)
            {
                archivePath = localPatch;
                LocalPatchPath = archivePath;
                Step1Status = "完成";
                DownloadText = "使用本地补丁";
                DownloadProgress = 100;
                AddLog($"使用本地补丁: {archivePath}");
            }
            else
            {
                archivePath = temporaryDownloadPath;
                await DownloadPatchAsync(patchUri!, archivePath, _installCancellationTokenSource.Token);
                LocalPatchPath = archivePath;
                Step1Status = "完成";
            }

            _installCancellationTokenSource.Token.ThrowIfCancellationRequested();

            StatusText = "正在解压补丁";
            Step2Status = "解压中";
            DownloadProgress = 0;
            DownloadText = "正在解压 0.0%";
            DownloadSpeedText = string.Empty;
            AddLog($"开始解压补丁: {archivePath}");
            await ArchiveInstaller.ExtractAsync(archivePath, extractPath, ReportExtractProgress);

            _installCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var sourceRoot = ResolveExtractedRoot(extractPath);
            Step2Status = "完成";
            Step3Status = "安装中";
            StatusText = "正在覆盖安装";
            AddLog($"开始安装到游戏目录: {GamePath}");

            var copied = await ElevationHelper.CopyWithElevationFallbackAsync(sourceRoot, GamePath);
            if (!copied)
            {
                throw new InvalidOperationException("覆盖安装失败，可能是权限不足或管理员授权被取消。");
            }

            Step3Status = "完成";
            StatusText = "补丁安装完成";
            DownloadText = "处理完成";
            DownloadSpeedText = string.Empty;
            AddLog("补丁安装完成。");
        }
        catch (OperationCanceledException)
        {
            DeleteTemporaryDownloadArtifacts(temporaryDownloadPath);
            StatusText = "已取消安装";
            DownloadText = "已取消";
            DownloadSpeedText = string.Empty;
            AddLog("已取消当前安装任务。");
        }
        catch (Exception ex)
        {
            DeleteTemporaryDownloadArtifacts(temporaryDownloadPath);
            StatusText = "安装失败";
            AddLog($"安装失败: {ex.Message}");
            Debug.WriteLine(ex);
        }
        finally
        {
            if (!useLocalPatch)
            {
                DeleteTemporaryDownloadArtifacts(temporaryDownloadPath);
            }

            CleanupWorkingDirectory(workingRoot);

            _installCancellationTokenSource.Dispose();
            _installCancellationTokenSource = null;
            IsBusy = false;
            CanInstall = true;
        }
    }

    private async Task DownloadPatchAsync(Uri patchUri, string downloadPath, CancellationToken cancellationToken)
    {
        DownloadProgress = 0;
        DownloadText = "正在连接服务器";
        DownloadSpeedText = string.Empty;
        AddLog($"开始下载补丁: {patchUri}");

        await PatchDownloader.DownloadAsync(
            patchUri,
            downloadPath,
            ParallelDownloadSegments,
            DownloadRetryCount,
            report => Dispatcher.UIThread.Post(() =>
            {
                DownloadProgress = report.ProgressPercent;
                DownloadText = report.TotalBytes is > 0
                    ? $"正在下载 {report.ProgressPercent:0.0}% ({FormatBytes(report.DownloadedBytes)} / {FormatBytes(report.TotalBytes.Value)})"
                    : $"正在下载 {FormatBytes(report.DownloadedBytes)}";
                DownloadSpeedText = report.BytesPerSecond > 0
                    ? $"速度 {FormatBytes((long)report.BytesPerSecond)}/s"
                    : string.Empty;
            }),
            cancellationToken);

        DownloadProgress = 100;
        DownloadText = "下载完成";
        DownloadSpeedText = string.Empty;
        AddLog($"下载完成: {downloadPath}");
    }

    private static string GetTemporaryDownloadFileName(string? suggestedFileName, Uri patchUri)
    {
        var fileName = string.IsNullOrWhiteSpace(suggestedFileName) ? GetFileNameFromUri(patchUri) : suggestedFileName;
        return EnsureSupportedPatchFileName(fileName);
    }

    private static async Task<string> ResolveDownloadFileNameAsync(Uri patchUri, CancellationToken cancellationToken)
    {
        var suggestedFileName = await PatchDownloader.GetSuggestedFileNameAsync(patchUri, cancellationToken);
        var fileName = string.IsNullOrWhiteSpace(suggestedFileName)
            ? GetFileNameFromUri(patchUri)
            : suggestedFileName;
        return EnsureSupportedPatchFileName(fileName);
    }

    private static string GetFileNameFromUri(Uri patchUri)
    {
        var fileName = Path.GetFileName(Uri.UnescapeDataString(patchUri.LocalPath.TrimEnd('/')));
        return string.IsNullOrWhiteSpace(fileName) ? "patch" : fileName;
    }

    private static string EnsureSupportedPatchFileName(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        return SupportedPatchExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".7z";
    }

    private static void DeleteTemporaryDownloadArtifacts(string? temporaryPath)
    {
        if (string.IsNullOrWhiteSpace(temporaryPath))
        {
            return;
        }

        DeleteIfExists(temporaryPath);
        DeleteIfExists(temporaryPath + ".meta");
    }

    private static void CleanupWorkingDirectory(string? workingRoot)
    {
        if (string.IsNullOrWhiteSpace(workingRoot) || !Directory.Exists(workingRoot))
        {
            return;
        }

        try
        {
            Directory.Delete(workingRoot, true);
        }
        catch
        {
        }
    }

    private void ReportExtractProgress(int completedEntries, int totalEntries, string currentEntry)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var percent = totalEntries <= 0 ? 0d : completedEntries * 100d / totalEntries;
            DownloadProgress = percent;
            DownloadText = totalEntries <= 0
                ? $"正在解压 {completedEntries}"
                : $"正在解压 {percent:0.0}%  {completedEntries} / {totalEntries}";
            DownloadSpeedText = string.IsNullOrWhiteSpace(currentEntry)
                ? string.Empty
                : $"当前文件: {Path.GetFileName(currentEntry)}";
        });
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static string? FindAutoSelectedPatchPath()
    {
        var prefix = InstallerBuildConfig.PatchFilePrefix;
        var directories = new[]
        {
            AppContext.BaseDirectory,
            Environment.CurrentDirectory
        }.Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            var patternPrefix = string.IsNullOrWhiteSpace(prefix) ? "*" : prefix;
            var candidates = SupportedPatchExtensions
                .SelectMany(extension => Directory.GetFiles(directory, $"{patternPrefix}*{extension}", SearchOption.TopDirectoryOnly))
                .OrderBy(path => path)
                .ToArray();

            if (candidates.Length > 0)
            {
                return candidates[0];
            }
        }

        return null;
    }

    private static bool IsSupportedPatchFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        SupportedPatchExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        return path.Trim().Replace('/', Path.DirectorySeparatorChar);
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

    private static string ResolveExtractedRoot(string extractPath)
    {
        var directories = Directory.GetDirectories(extractPath);
        return directories.Length == 1 ? directories[0] : extractPath;
    }

    private void PrepareWorkingDirectory(string workingRoot, string extractPath)
    {
        Directory.CreateDirectory(workingRoot);

        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        Directory.CreateDirectory(extractPath);
        AddLog($"工作目录: {workingRoot}");
    }

    private void AddLog(string message)
    {
        Dispatcher.UIThread.Post(() => Logs.Add($"[{DateTime.Now:HH:mm:ss}] {message}"));
    }
}
