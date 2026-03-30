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
using PatchInstaller.Services;
using SharpCompress.Archives;
using SteamLocator = PatchInstaller.Services.SteamLocator;

namespace PatchInstaller;

public partial class MainWindowViewModel : ObservableObject
{
    private const string DownloadSource = "下载链接";
    public const string LocalSource = "本地补丁";

    private static readonly string[] SupportedPatchExtensions = [".7z", ".zip", ".rar"];
    private static readonly string[] SupportedMultipartPatterns = ["*.zip.001", "*.rar.001"];
    private static readonly string DefaultPatchUrl = InstallerBuildConfig.DefaultPatchUrl;
    private const int ParallelDownloadSegments = 12;
    private const int DownloadRetryCount = 30;

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

    public ObservableCollection<string> PatchSourceOptions { get; } = [DownloadSource, LocalSource];

    public bool UseDownloadSource => string.Equals(SelectedPatchSource, DownloadSource, StringComparison.Ordinal);
    public bool UseLocalSource => string.Equals(SelectedPatchSource, LocalSource, StringComparison.Ordinal);
    public bool IsLocalPatchReady => IsSupportedPatchFile(LocalPatchPath) && File.Exists(NormalizePath(LocalPatchPath));
    public bool ShowDownloadInstallButton => UseDownloadSource;
    public bool ShowLocalInstallButton => UseLocalSource && IsLocalPatchReady;
    public bool ShowSelectPatchButton => UseLocalSource;
    public bool CanCancelInstall => IsBusy;
    public bool IsStep1Active => string.Equals(Step1Status, "定位中", StringComparison.Ordinal);
    public bool IsStep2Active => string.Equals(Step2Status, "下载中", StringComparison.Ordinal);
    public bool IsStep3Active => string.Equals(Step3Status, "解压中", StringComparison.Ordinal) || string.Equals(Step3Status, "安装中", StringComparison.Ordinal);
    public bool IsStep1Completed => string.Equals(Step1Status, "完成", StringComparison.Ordinal) || string.Equals(Step1Status, "已定位", StringComparison.Ordinal);
    public bool IsStep2Completed => string.Equals(Step2Status, "完成", StringComparison.Ordinal) || string.Equals(Step2Status, "已选择", StringComparison.Ordinal);
    public bool IsStep3Completed => string.Equals(Step3Status, "完成", StringComparison.Ordinal);

    public string ProductName => InstallerBuildConfig.ProductName;

    public MainWindowViewModel()
    {
        GamePath = NormalizePath(GetDetectedGamePath());
        if (!string.IsNullOrWhiteSpace(GamePath))
        {
            Step1Status = "已定位";
        }

        var autoPatch = FindAutoSelectedPatchPath();
        if (!string.IsNullOrWhiteSpace(autoPatch))
        {
            LocalPatchPath = autoPatch;
            SelectedPatchSource = LocalSource;
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

    partial void OnStep1StatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsStep1Active));
        OnPropertyChanged(nameof(IsStep1Completed));
    }

    partial void OnStep2StatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsStep2Active));
        OnPropertyChanged(nameof(IsStep2Completed));
    }

    partial void OnStep3StatusChanged(string value)
    {
        OnPropertyChanged(nameof(IsStep3Active));
        OnPropertyChanged(nameof(IsStep3Completed));
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
                return;
            }

            if (!Uri.TryCreate(PatchUrl, UriKind.Absolute, out patchUri) ||
                (patchUri.Scheme != Uri.UriSchemeHttp && patchUri.Scheme != Uri.UriSchemeHttps))
            {
                StatusText = "补丁链接无效";
                return;
            }
        }

        GamePath = NormalizePath(GamePath);
        if (!Directory.Exists(GamePath))
        {
            StatusText = "游戏目录不存在";
            Step1Status = "未找到";
            return;
        }

        _installCancellationTokenSource = new CancellationTokenSource();

        IsBusy = true;
        CanInstall = false;
        DownloadProgress = 0;
        DownloadText = "准备处理";
        DownloadSpeedText = string.Empty;
        Step1Status = "已定位";
        Step2Status = "下载中";
        Step3Status = "未执行";
        StatusText = "开始安装";

        var workingRoot = Path.Combine(Path.GetTempPath(), "PatchInstaller");
        var extractPath = Path.Combine(workingRoot, "extracted");
        var temporaryDownloadPath = string.Empty;
        string? archivePath = null;

        try
        {
            if (patchUri is not null)
            {
                var downloadFileName = await ResolveDownloadFileNameAsync(patchUri, _installCancellationTokenSource.Token);
                temporaryDownloadPath = Path.Combine(workingRoot, GetTemporaryDownloadFileName(downloadFileName, patchUri));
            }

            PrepareWorkingDirectory(workingRoot, extractPath);

            if (useLocalPatch)
            {
                archivePath = localPatch;
                LocalPatchPath = archivePath;
                Step2Status = "已选择";
                DownloadText = "使用本地补丁";
                DownloadProgress = 100;
            }
            else
            {
                archivePath = temporaryDownloadPath;
                await DownloadPatchAsync(patchUri!, archivePath, _installCancellationTokenSource.Token);
                LocalPatchPath = archivePath;
            }
            _installCancellationTokenSource.Token.ThrowIfCancellationRequested();

            // 验证压缩包是否能被正常读取，避免后续解压时才发现问题
            try
            {
                var archive = ArchiveFactory.OpenArchive(archivePath);
                foreach (var entry in archive.Entries)
                {
                    if (entry.IsDirectory) continue;
                    await using var stream = await entry.OpenEntryStreamAsync();
                    var buffer = new byte[8192];
                    while (await stream.ReadAsync(buffer) > 0)
                    {
                        // 只读，不需要处理数据
                    }
                }
            }
            catch
            {
                DownloadProgress = 0;
                DownloadText = "";
                DownloadSpeedText = string.Empty;
                throw new InvalidOperationException("无法读取补丁文件，可能是下载过程中发生了损坏。请重试。");
            }
            
            if (!useLocalPatch)
            {
                Step2Status = "完成";
            }
            StatusText = "正在解压补丁";
            Step3Status = "解压中";
            DownloadProgress = 0;
            DownloadText = "正在解压 0.0%";
            DownloadSpeedText = string.Empty;
            await ArchiveInstaller.ExtractAsync(archivePath, extractPath, ReportExtractProgress);

            _installCancellationTokenSource.Token.ThrowIfCancellationRequested();

            var sourceRoot = ResolveExtractedRoot(extractPath);
            Step3Status = "安装中";
            StatusText = "正在覆盖安装";

            var copied = await ElevationHelper.CopyWithElevationFallbackAsync(sourceRoot, GamePath);
            if (!copied)
            {
                throw new InvalidOperationException("覆盖安装失败，可能是权限不足或管理员授权被取消。");
            }

            Step3Status = "完成";
            StatusText = "补丁安装完成";
            DownloadText = "处理完成";
            DownloadSpeedText = string.Empty;
        }
        catch (OperationCanceledException)
        {
            if (IsStep2Active)
            {
                Step2Status = "已取消";
            }
            else if (IsStep3Active)
            {
                Step3Status = "已取消";
            }

            DeleteTemporaryDownloadArtifacts(temporaryDownloadPath);
            StatusText = "安装已停止";
            DownloadText = "已取消";
            DownloadSpeedText = string.Empty;
        }
        catch (Exception ex)
        {
            if (IsPatchAcquisitionFailure(ex, archivePath))
            {
                Step2Status = "失败";
                if (!IsStep3Completed)
                {
                    Step3Status = "未执行";
                }
            }
            else if (IsStep2Active)
            {
                Step2Status = "失败";
            }
            else if (IsStep3Active)
            {
                Step3Status = "失败";
            }

            DeleteTemporaryDownloadArtifacts(temporaryDownloadPath);
            StatusText = "";
            Debug.WriteLine(ex);
            await DialogService.ShowErrorAsync("安装失败", ex.Message);
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

        await PatchDownloader.DownloadAsync(
            patchUri,
            downloadPath,
            ParallelDownloadSegments,
            DownloadRetryCount,
            report => Dispatcher.UIThread.Post(() =>
            {
                DownloadProgress = report.ProgressPercent;
                DownloadText = report.TotalBytes is > 0
                    ? $"正在下载 {report.ProgressPercent:0.0}% ({FormatBytes(report.DownloadedBytes)} / {FormatBytesPrecise(report.TotalBytes.Value)})"
                    : $"正在下载 {FormatBytes(report.DownloadedBytes)}";
                DownloadSpeedText = report.BytesPerSecond > 0
                    ? $"速度 {FormatSpeed(report.BytesPerSecond)}/s"
                    : string.Empty;
            }),
            cancellationToken);
        
        DownloadProgress = 100;
        DownloadText = "下载完成";
        DownloadSpeedText = string.Empty;
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
        return ArchiveInstaller.IsSupportedArchivePath(fileName)
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
            // ignored
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
            // ignored
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

            var patternPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix;
            var archiveCandidates = SupportedPatchExtensions
                .SelectMany(extension => Directory.GetFiles(directory, $"{patternPrefix}*{extension}", SearchOption.TopDirectoryOnly))
                .Where(ArchiveInstaller.IsSupportedArchivePath);
            var multipartCandidates = SupportedMultipartPatterns
                .SelectMany(pattern => Directory.GetFiles(directory, $"{patternPrefix}{pattern}", SearchOption.TopDirectoryOnly))
                .Where(ArchiveInstaller.IsMultipartArchiveFirstSegment);

            var candidates = archiveCandidates
                .Concat(multipartCandidates)
                .Distinct(StringComparer.OrdinalIgnoreCase)
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
        ArchiveInstaller.IsSupportedArchivePath(path);

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

        return $"{value:0} {units[unitIndex]}";
    }

    private static string FormatSpeed(double bytesPerSecond)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = bytesPerSecond;
        var unitIndex = 0;

        while (value >= 1024 && unitIndex < units.Length - 1)
        {
            value /= 1024;
            unitIndex++;
        }

        return $"{value:0.00} {units[unitIndex]}";
    }

    private static string FormatBytesPrecise(long bytes)
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

    private static bool IsPatchAcquisitionFailure(Exception ex, string? archivePath)
    {
        if (ex is not FileNotFoundException fileNotFoundException)
        {
            return false;
        }

        if (ex.Message.Contains("缺少分片文件", StringComparison.Ordinal) ||
            ex.Message.Contains("找不到压缩包文件", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(archivePath) || string.IsNullOrWhiteSpace(fileNotFoundException.FileName))
        {
            return false;
        }

        return string.Equals(
            NormalizePath(fileNotFoundException.FileName),
            NormalizePath(archivePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private void PrepareWorkingDirectory(string workingRoot, string extractPath)
    {
        Directory.CreateDirectory(workingRoot);

        if (Directory.Exists(extractPath))
        {
            Directory.Delete(extractPath, true);
        }

        Directory.CreateDirectory(extractPath);
    }
}
