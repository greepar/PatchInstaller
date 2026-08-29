using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PatchInstaller.Services;
using SteamLocator = PatchInstaller.Services.SteamLocator;

namespace PatchInstaller;

public partial class MainWindowViewModel : ObservableObject
{
    private const string DownloadSource = "download";
    private const string CustomSource = "custom";
    public const string LocalSource = "local";
    private static readonly string[] SupportedPatchExtensions = [".7z", ".zip", ".rar"];
    private static readonly string[] SupportedMultipartPatterns = ["*.zip.001", "*.rar.001"];

    private static readonly BuiltInPatchSourceDefinition[] BuiltInPatchSources =
        ParseBuiltInPatchSources(InstallerBuildConfig.DefaultPatchUrl);

    [ObservableProperty] private bool _canInstall = true;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _downloadSpeedText = string.Empty;
    [ObservableProperty] private string _downloadText = T("Waiting");
    [ObservableProperty] private string _gamePath = string.Empty;
    private CancellationTokenSource? _installCancellationTokenSource;
    private int _installOperationId;
    [ObservableProperty] private bool _isAutoBuiltInSourceSelected;
    [ObservableProperty] private bool _isBusy;

    [NotifyPropertyChangedFor(nameof(IsLocalPatchReady))]
    [NotifyPropertyChangedFor(nameof(ShowDownloadInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowLocalInstallButton))]
    [NotifyPropertyChangedFor(nameof(ShowSelectPatchButton))]
    [ObservableProperty]
    private string _localPatchPath = string.Empty;

    [ObservableProperty]
    private string _patchUrl = BuiltInPatchSources.Length == 1 ? BuiltInPatchSources[0].Url : string.Empty;

    [ObservableProperty]
    private string _selectedBuiltInPatchUrl = BuiltInPatchSources.FirstOrDefault()?.Url ?? string.Empty;

    [ObservableProperty] private PatchSourceOption? _selectedPatchSourceOption;
    [ObservableProperty] private string _statusText = T("Ready");
    [ObservableProperty] private string _step1Status = T("NotStarted");
    [ObservableProperty] private string _step2Status = T("NotStarted");
    [ObservableProperty] private string _step3Status = T("NotStarted");

    public MainWindowViewModel()
    {
        foreach (var source in BuiltInPatchSources)
            BuiltInDownloadOptions.Add(new BuiltInPatchSourceOption(this, source.Name, source.Url));

        if (BuiltInPatchSources.Length > 1) PatchSourceOptions.Add(CreatePatchSourceOption(DownloadSource));

        PatchSourceOptions.Add(CreatePatchSourceOption(CustomSource));
        PatchSourceOptions.Add(CreatePatchSourceOption(LocalSource));

        if (HasMultipleBuiltInPatchUrls)
        {
            IsAutoBuiltInSourceSelected = true;
            SelectBuiltInPatchSource(string.Empty);
            SelectedPatchSourceOption = GetPatchSourceOption(DownloadSource);
        }
        else
        {
            SyncBuiltInPatchSourceSelection();
            SelectedPatchSourceOption = GetPatchSourceOption(CustomSource);
        }

        GamePath = NormalizePath(GetDetectedGamePath());
        if (!string.IsNullOrWhiteSpace(GamePath)) Step1Status = T("Located");

        var autoPatch = FindAutoSelectedPatchPath();
        if (!string.IsNullOrWhiteSpace(autoPatch))
        {
            LocalPatchPath = autoPatch;
            SelectedPatchSourceOption = GetPatchSourceOption(LocalSource);
        }

        _ = CheckProgramUpdateAsync();
    }

    public ObservableCollection<PatchSourceOption> PatchSourceOptions { get; } = [];
    public ObservableCollection<BuiltInPatchSourceOption> BuiltInDownloadOptions { get; } = [];

    public bool UseDownloadSource => string.Equals(SelectedPatchSourceOption?.Key, DownloadSource, StringComparison.Ordinal);
    public bool UseCustomSource => string.Equals(SelectedPatchSourceOption?.Key, CustomSource, StringComparison.Ordinal);
    public bool UseRemoteSource => UseDownloadSource || UseCustomSource;
    public bool UseLocalSource => string.Equals(SelectedPatchSourceOption?.Key, LocalSource, StringComparison.Ordinal);
    private bool HasMultipleBuiltInPatchUrls => BuiltInDownloadOptions.Count > 1;
    public bool ShowDownloadUrlInput => UseCustomSource || (UseDownloadSource && !HasMultipleBuiltInPatchUrls);
    public bool ShowBuiltInSourceSelector => UseDownloadSource && HasMultipleBuiltInPatchUrls;
    public bool IsLocalPatchReady => IsSupportedPatchFile(LocalPatchPath) && File.Exists(NormalizePath(LocalPatchPath));
    public bool ShowDownloadInstallButton => UseDownloadSource || UseCustomSource;
    public bool ShowLocalInstallButton => UseLocalSource && IsLocalPatchReady;
    public bool ShowSelectPatchButton => UseLocalSource;
    public bool CanCancelInstall => IsBusy;
    public bool IsStep1Active => string.Equals(Step1Status, T("Locating"), StringComparison.Ordinal);
    public bool IsStep2Active => string.Equals(Step2Status, T("Downloading"), StringComparison.Ordinal);

    public bool IsStep3Active => string.Equals(Step3Status, T("Extracting"), StringComparison.Ordinal) ||
                                 string.Equals(Step3Status, T("Installing"), StringComparison.Ordinal);

    public bool IsStep1Completed => string.Equals(Step1Status, T("Completed"), StringComparison.Ordinal) ||
                                    string.Equals(Step1Status, T("Located"), StringComparison.Ordinal);

    public bool IsStep2Completed => string.Equals(Step2Status, T("Completed"), StringComparison.Ordinal) ||
                                    string.Equals(Step2Status, T("Selected"), StringComparison.Ordinal);

    public bool IsStep3Completed => string.Equals(Step3Status, T("Completed"), StringComparison.Ordinal);

    public static string ProductName => InstallerBuildConfig.ProductName;
    public static string DisplayVersion => InstallerBuildConfig.DisplayVersion;
    public string SubtitleText => T("Subtitle");
    public string DownloadUrlWatermarkText => T("DownloadUrlWatermark");
    public string AutoText => T("Auto");
    public string InstallText => T("Install");
    public string PatchWatermarkText => T("PatchWatermark");
    public string SelectPatchText => T("SelectPatch");
    public string GamePathText => T("GamePath");
    public string GamePathWatermarkText => T("GamePathWatermark");
    public string LocateText => T("Locate");
    public string InstallProgressText => T("InstallProgress");
    public string CancelDownloadText => T("CancelDownload");
    public string LocateGameText => T("LocateGame");
    public string GetPatchText => T("GetPatch");
    public string ExtractInstallText => T("ExtractInstall");

    private static string? GetDetectedGamePath()
    {
        return SteamLocator.FindGamePath();
    }

    public void SelectLocalPatchSource()
    {
        SelectedPatchSourceOption = GetPatchSourceOption(LocalSource);
    }

    private static string T(string key)
    {
        return LocalizationService.Get(key);
    }

    private static PatchSourceOption CreatePatchSourceOption(string key)
    {
        return new PatchSourceOption(key, key switch
        {
            DownloadSource => T("DownloadSource"),
            LocalSource => T("LocalSource"),
            _ => T("CustomSource")
        });
    }

    private PatchSourceOption? GetPatchSourceOption(string key)
    {
        return PatchSourceOptions.FirstOrDefault(option => string.Equals(option.Key, key, StringComparison.Ordinal));
    }

    partial void OnSelectedPatchSourceOptionChanged(PatchSourceOption? value)
    {
        OnPropertyChanged(nameof(UseDownloadSource));
        OnPropertyChanged(nameof(UseCustomSource));
        OnPropertyChanged(nameof(UseRemoteSource));
        OnPropertyChanged(nameof(UseLocalSource));
        OnPropertyChanged(nameof(ShowDownloadUrlInput));
        OnPropertyChanged(nameof(ShowBuiltInSourceSelector));
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

    partial void OnSelectedBuiltInPatchUrlChanged(string value)
    {
        if (BuiltInDownloadOptions.Any(option => string.Equals(option.Url, value, StringComparison.Ordinal)))
            IsAutoBuiltInSourceSelected = false;

        SelectBuiltInPatchSource(value);
    }

    partial void OnIsAutoBuiltInSourceSelectedChanged(bool value)
    {
        if (!value) return;

        SelectBuiltInPatchSource(string.Empty);
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
        if (!IsBusy) return;

        _installCancellationTokenSource?.Cancel();
    }

    [RelayCommand]
    private async Task InstallAsync()
    {
        if (IsBusy) return;

        var localPatch = NormalizePath(LocalPatchPath);
        var useLocalPatch = UseLocalSource && IsSupportedPatchFile(localPatch) && File.Exists(localPatch);
        DownloadCandidate[] patchCandidates = [];

        if (!useLocalPatch)
        {
            if (!UseRemoteSource)
            {
                StatusText = "请先选择本地补丁";
                return;
            }

            patchCandidates = GetEffectivePatchCandidates();
            if (patchCandidates.Length == 0)
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
        var cancellationToken = _installCancellationTokenSource.Token;
        var operationId = ++_installOperationId;

        IsBusy = true;
        CanInstall = false;
        DownloadProgress = 0;
        DownloadText = "准备处理";
        DownloadSpeedText = string.Empty;
        Step1Status = T("Located");
        Step2Status = T("Downloading");
        Step3Status = T("NotStarted");
        StatusText = "开始安装";

        var workingRoot = Path.Combine(GamePath, ".temp", "PatchInstaller");
        var extractPath = Path.Combine(workingRoot, "extracted");
        string? archivePath = null;
        var installationSucceeded = false;

        try
        {
            PrepareWorkingDirectory(workingRoot, extractPath);

            if (useLocalPatch)
            {
                archivePath = localPatch;
                LocalPatchPath = archivePath;
                Step2Status = T("Selected");
                DownloadText = "使用本地补丁";
                DownloadProgress = 100;
            }
            else
            {
                archivePath = await DownloadPatchAsync(
                    patchCandidates,
                    Path.Combine(workingRoot, "downloads"),
                    cancellationToken,
                    operationId);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (useLocalPatch)
            {
                await ArchiveInstaller.ValidateAsync(archivePath, cancellationToken, workingRoot);
            }

            if (!useLocalPatch) Step2Status = T("Completed");
            StatusText = T("ExtractingPatch");
            Step3Status = T("Extracting");
            DownloadProgress = 0;
            DownloadText = string.Format(T("ExtractingProgress"), 0d, 0, 0);
            DownloadSpeedText = string.Empty;
            await ArchiveInstaller.ExtractAsync(
                archivePath,
                extractPath,
                (completedEntries, totalEntries, currentEntry) => ReportExtractProgress(
                    completedEntries,
                    totalEntries,
                    currentEntry,
                    operationId,
                    cancellationToken),
                cancellationToken,
                workingRoot);

            cancellationToken.ThrowIfCancellationRequested();

            var sourceRoot = ResolveExtractedRoot(extractPath);
            Step3Status = T("Installing");
            StatusText = T("InstallingPatch");

            var copied = await ElevationHelper.CopyWithElevationFallbackAsync(sourceRoot, GamePath, cancellationToken);
            if (!copied) throw new InvalidOperationException("覆盖安装失败，可能是权限不足或管理员授权被取消。");

            cancellationToken.ThrowIfCancellationRequested();

            Step3Status = T("Completed");
            StatusText = "补丁安装完成";
            DownloadText = "处理完成";
            DownloadSpeedText = string.Empty; 
            await DialogService.ShowSuccessAsync();
            installationSucceeded = true;
        }
        catch (OperationCanceledException)
        {
            if (IsStep2Active)
                Step2Status = T("Canceled");
            else if (IsStep3Active) Step3Status = T("Canceled");

            StatusText = "安装已停止";
            DownloadProgress = 0;
            DownloadText = T("Canceled");
            DownloadSpeedText = string.Empty;
        }
        catch (Exception ex)
        {
            if (_installCancellationTokenSource?.IsCancellationRequested == true)
            {
                if (IsStep2Active)
                    Step2Status = T("Canceled");
                else if (IsStep3Active) Step3Status = T("Canceled");

                StatusText = "安装已停止";
                DownloadProgress = 0;
                DownloadText = T("Canceled");
                DownloadSpeedText = string.Empty;
                Debug.WriteLine(ex);
                return;
            }

            if (IsPatchAcquisitionFailure(ex, archivePath))
            {
                Step2Status = T("Failed");
                if (!IsStep3Completed) Step3Status = T("NotStarted");
            }
            else if (IsStep2Active)
            {
                Step2Status = T("Failed");
            }
            else if (IsStep3Active)
            {
                Step3Status = T("Failed");
            }

            StatusText = "";
            Debug.WriteLine(ex);
            await DialogService.ShowErrorAsync("安装失败", GetDisplayErrorMessage(ex));
        }
        finally
        {
            if (installationSucceeded)
            {
                CleanupWorkingDirectory(workingRoot);
            }

            _installCancellationTokenSource?.Dispose();
            _installCancellationTokenSource = null;
            IsBusy = false;
            CanInstall = true;
        }
    }

    private async Task<string> DownloadPatchAsync(
        IReadOnlyList<DownloadCandidate> patchCandidates,
        string downloadRoot,
        CancellationToken cancellationToken,
        int operationId)
    {
        DownloadProgress = 0;
        DownloadText = T("ConnectingServer");
        DownloadSpeedText = string.Empty;

        Exception? lastError = null;

        for (var index = 0; index < patchCandidates.Count; index++)
        {
            var candidate = patchCandidates[index];
            var patchUri = candidate.Uri;
            var sourceLabel = string.IsNullOrWhiteSpace(candidate.Name) ? patchUri.Host : candidate.Name;

            try
            {
                if (ArchiveInstaller.IsMultipartArchiveFirstSegment(patchUri.AbsolutePath))
                {
                    throw new NotSupportedException("暂不支持从网络下载分卷压缩包，请先下载全部 .001、.002 等分片后使用本地补丁安装。");
                }

                DownloadText = string.Format(T("ConnectingSource"), sourceLabel);
                var destinationDirectory = Path.Combine(downloadRoot, index.ToString());
                var result = await PatchDownloader.DownloadAsync(
                    patchUri,
                    destinationDirectory,
                    report => Dispatcher.UIThread.Post(() =>
                    {
                        if (operationId != _installOperationId || cancellationToken.IsCancellationRequested)
                        {
                            return;
                        }

                        DownloadProgress = report.ProgressPercentage;
                        DownloadText = report.TotalBytes > 0
                            ? string.Format(T("DownloadingFromSourceProgress"), sourceLabel, report.ProgressPercentage, FormatBytes(report.DownloadedBytes), FormatBytesPrecise(report.TotalBytes))
                            : string.Format(T("DownloadingFromSourceBytes"), sourceLabel, FormatBytes(report.DownloadedBytes));
                        DownloadSpeedText = report.Speed > 0
                            ? string.Format(T("DownloadSpeed"), FormatSpeed(report.Speed))
                            : string.Empty;
                    }),
                    cancellationToken);

                await ArchiveInstaller.ValidateAsync(result.FilePath, cancellationToken, destinationDirectory);
                DownloadProgress = 100;
                DownloadText = string.Format(T("DownloadedFromSource"), sourceLabel);
                DownloadSpeedText = string.Empty;
                return result.FilePath;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;

                if (index < patchCandidates.Count - 1)
                {
                    DownloadProgress = 0;
                    var nextCandidate = patchCandidates[index + 1];
                    var nextLabel = string.IsNullOrWhiteSpace(nextCandidate.Name)
                        ? nextCandidate.Uri.Host
                        : nextCandidate.Name;
                    DownloadText = $"{sourceLabel} 失败，正在尝试 {nextLabel}";
                    DownloadSpeedText = string.Empty;
                }
            }
        }

        throw lastError ?? new InvalidOperationException("没有可用的补丁下载源。");
    }

    private DownloadCandidate[] GetEffectivePatchCandidates()
    {
        if (UseCustomSource)
            return TryCreateHttpUri(PatchUrl.Trim(), out var customUri)
                ? [new DownloadCandidate("自定义源", customUri)]
                : [];

        if (HasMultipleBuiltInPatchUrls)
        {
            if (IsAutoBuiltInSourceSelected)
                return BuiltInDownloadOptions
                    .Select(option => new DownloadCandidate(option.Name, new Uri(option.Url, UriKind.Absolute)))
                    .ToArray();

            var selectedSource = BuiltInPatchSources.FirstOrDefault(source =>
                string.Equals(source.Url, SelectedBuiltInPatchUrl, StringComparison.Ordinal));
            return TryCreateHttpUri(SelectedBuiltInPatchUrl, out var selectedUri)
                ? [new DownloadCandidate(selectedSource?.Name ?? selectedUri.Host, selectedUri)]
                : [];
        }

        var builtInName = BuiltInPatchSources.FirstOrDefault()?.Name ?? "内置源";
        return TryCreateHttpUri(PatchUrl.Trim(), out var patchUri)
            ? [new DownloadCandidate(builtInName, patchUri)]
            : [];
    }

    private void SyncBuiltInPatchSourceSelection()
    {
        foreach (var option in BuiltInDownloadOptions)
            option.SetSelected(string.Equals(option.Url, SelectedBuiltInPatchUrl, StringComparison.Ordinal));
    }

    private void SelectBuiltInPatchSource(string url)
    {
        foreach (var option in BuiltInDownloadOptions)
            option.SetSelected(string.Equals(option.Url, url, StringComparison.Ordinal));
    }

    private static BuiltInPatchSourceDefinition[] ParseBuiltInPatchSources(string? patchUrl)
    {
        if (string.IsNullOrWhiteSpace(patchUrl)) return [];

        return patchUrl
            .Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(ParseBuiltInPatchSource)
            .Where(source => source is not null)
            .Select(source => source!)
            .Where(source => Uri.TryCreate(source.Url, UriKind.Absolute, out var uri) &&
                             (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            .ToArray();
    }

    private static BuiltInPatchSourceDefinition? ParseBuiltInPatchSource(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry)) return null;

        var separatorIndex = entry.IndexOf('|');
        if (separatorIndex <= 0 || separatorIndex >= entry.Length - 1)
        {
            var url = entry.Trim();
            return string.IsNullOrWhiteSpace(url)
                ? null
                : new BuiltInPatchSourceDefinition(url, url);
        }

        var name = entry[..separatorIndex].Trim();
        var urlText = entry[(separatorIndex + 1)..].Trim();
        if (string.IsNullOrWhiteSpace(urlText)) return null;

        return new BuiltInPatchSourceDefinition(
            string.IsNullOrWhiteSpace(name) ? urlText : name,
            urlText);
    }

    private static bool TryCreateHttpUri(string? url, out Uri uri)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out uri!) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
            return true;

        uri = null!;
        return false;
    }

    private static void CleanupWorkingDirectory(string? workingRoot)
    {
        if (string.IsNullOrWhiteSpace(workingRoot) || !Directory.Exists(workingRoot)) return;

        try
        {
            Directory.Delete(workingRoot, true);
        }
        catch
        {
            // ignored
        }
    }

    private void ReportExtractProgress(
        int completedEntries,
        int totalEntries,
        string currentEntry,
        int operationId,
        CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (operationId != _installOperationId || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            var percent = totalEntries <= 0 ? 0d : completedEntries * 100d / totalEntries;
            DownloadProgress = percent;
            DownloadText = totalEntries <= 0
                ? string.Format(T("ExtractingCount"), completedEntries)
                : string.Format(T("ExtractingProgress"), percent, completedEntries, totalEntries);
            DownloadSpeedText = string.IsNullOrWhiteSpace(currentEntry)
                ? string.Empty
                : string.Format(T("CurrentFile"), Path.GetFileName(currentEntry));
        });
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
            if (!Directory.Exists(directory)) continue;

            var patternPrefix = string.IsNullOrWhiteSpace(prefix) ? string.Empty : prefix;
            var archiveCandidates = SupportedPatchExtensions
                .SelectMany(extension =>
                    Directory.GetFiles(directory, $"{patternPrefix}*{extension}", SearchOption.TopDirectoryOnly))
                .Where(ArchiveInstaller.IsSupportedArchivePath);
            var multipartCandidates = SupportedMultipartPatterns
                .SelectMany(pattern =>
                    Directory.GetFiles(directory, $"{patternPrefix}{pattern}", SearchOption.TopDirectoryOnly))
                .Where(ArchiveInstaller.IsMultipartArchiveFirstSegment);

            var candidates = archiveCandidates
                .Concat(multipartCandidates)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(path => path)
                .ToArray();

            if (candidates.Length > 0) return candidates[0];
        }

        return null;
    }

    private static bool IsSupportedPatchFile(string? path)
    {
        return !string.IsNullOrWhiteSpace(path) &&
               ArchiveInstaller.IsSupportedArchivePath(path);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

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
        if (ex is not FileNotFoundException fileNotFoundException) return false;

        if (ex.Message.Contains("缺少分片文件", StringComparison.Ordinal) ||
            ex.Message.Contains("找不到压缩包文件", StringComparison.Ordinal))
            return true;

        if (string.IsNullOrWhiteSpace(archivePath) ||
            string.IsNullOrWhiteSpace(fileNotFoundException.FileName)) return false;

        return string.Equals(
            NormalizePath(fileNotFoundException.FileName),
            NormalizePath(archivePath),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string GetDisplayErrorMessage(Exception ex)
    {
        for (Exception? current = ex; current is not null; current = current.InnerException)
        {
            if (!string.IsNullOrWhiteSpace(current.Message))
                return current.Message;
        }

        return "安装失败，未能获取到具体错误信息。请重试。";
    }

    private void PrepareWorkingDirectory(string workingRoot, string extractPath)
    {
        Directory.CreateDirectory(workingRoot);

        if (Directory.Exists(extractPath)) Directory.Delete(extractPath, true);

        Directory.CreateDirectory(extractPath);
    }

    private async Task CheckProgramUpdateAsync()
    {
        if (!InstallerBuildConfig.HasCheckUpdateApi) return;

        try
        {
            var result = await UpdateService.CheckAsync(CancellationToken.None);
            if (result.IsAvailable && !string.IsNullOrWhiteSpace(result.DownloadUrl))
            {
                await InstallProgramUpdateAsync(result.DownloadUrl);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private async Task InstallProgramUpdateAsync(string downloadUrl)
    {
        var shouldUpdate = await DialogService.ShowUpdateAvailableAsync(downloadUrl);
        if (!shouldUpdate) return;

        StatusText = "正在下载程序更新";

        Dispatcher.UIThread.Post(() =>
        {
            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.Shutdown();
                return;
            }

            Environment.Exit(0);
        });
    }

    private sealed record BuiltInPatchSourceDefinition(string Name, string Url);

    private sealed record DownloadCandidate(string Name, Uri Uri);

    public sealed record PatchSourceOption(string Key, string DisplayName);

    public partial class BuiltInPatchSourceOption(MainWindowViewModel owner, string name, string url) : ObservableObject
    {
        [ObservableProperty] private bool _isSelected;

        public string Name { get; } = name;
        public string Url { get; } = url;

        partial void OnIsSelectedChanged(bool value)
        {
            if (!value || string.Equals(owner.SelectedBuiltInPatchUrl, Url, StringComparison.Ordinal)) return;
            owner.SelectedBuiltInPatchUrl = Url;
            owner.SelectBuiltInPatchSource(Url);
        }

        public void SetSelected(bool value)
        {
            if (IsSelected != value) IsSelected = value;
        }
    }
}
