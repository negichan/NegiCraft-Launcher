using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NegiCraftLauncher.App.Models;
using NegiCraftLauncher.App.Services;
using NegiCraftLauncher.Core;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Java;
using NegiCraftLauncher.Core.Launch;
using NegiCraftLauncher.Core.Modrinth;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Versions;
using SourceKind = NegiCraftLauncher.Core.Settings.DownloadSource;

namespace NegiCraftLauncher.App.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] BlockIcons = { "grass", "stone", "tnt", "dirt", "log" };

    private readonly Launcher _launcher;
    private readonly ModrinthClient _modrinth = new();
    private ModrinthInstaller _modrinthInstaller = new();
    private readonly Dictionary<string, IImage> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceModel> _gameResources = new();

    private Bitmap? _customBg;
    private bool _applyingSettings;
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _resourceLoad;
    private CancellationTokenSource? _bannerTimer;
    private TaskCompletionSource<string?>? _dialogTcs;

    /// <summary>The view hides and restores itself; the VM only says when.</summary>
    public event Action? HideWindowRequested;
    public event Action? ShowWindowRequested;

    public MainWindowViewModel()
    {
        _launcher = new Launcher();
        _launcher.Initialize();

        _bgBitmap = PixelArtService.CreateBackgroundBitmap(_launcher.Settings.IsDark);

        ApplySettingsToUi(_launcher.Settings);
        RefreshAccounts();
        RefreshInstances();
        RefreshJavaOptions();

        _ = LoadGameVersionsAsync();
    }

    public string LauncherVersion => _launcher.Version;

    // ==========================================================
    // Page
    // ==========================================================

    [ObservableProperty]
    private string _currentPage = "home";

    public bool IsOnHome => CurrentPage == "home";

    partial void OnCurrentPageChanged(string value)
    {
        OnPropertyChanged(nameof(IsOnHome));
        CloseAllPopovers();
    }

    [RelayCommand]
    private void Go(string page) => CurrentPage = page;

    // ==========================================================
    // Background & appearance
    // ==========================================================

    [ObservableProperty]
    private IImage? _bgBitmap;

    // Drives which background <Image> is shown: the generated pixel scene is stretched to fill,
    // a picked photo keeps its own aspect and is cropped instead.
    [ObservableProperty]
    private string? _customBackgroundPath;

    public bool IsCustomBackground => CustomBackgroundPath is not null;

    partial void OnCustomBackgroundPathChanged(string? value)
    {
        OnPropertyChanged(nameof(IsCustomBackground));
        PersistSettings();
    }

    [ObservableProperty]
    private double _bgBlur;

    // -100 (darkest) .. 100 (brightest); half the range maps to 0.5 alpha so the art stays readable
    [ObservableProperty]
    private double _bgBrightness;

    [ObservableProperty]
    private bool _isBgPopOpen;

    public double BgDarkOpacity => BgBrightness < 0 ? -BgBrightness / 200 : 0;

    public double BgLightOpacity => BgBrightness > 0 ? BgBrightness / 200 : 0;

    partial void OnBgBrightnessChanged(double value)
    {
        // The number box writes straight through, so snap typed values back into range.
        var clamped = Math.Clamp(value, -100, 100);
        if (!clamped.Equals(value))
        {
            BgBrightness = clamped;
            return;
        }

        OnPropertyChanged(nameof(BgDarkOpacity));
        OnPropertyChanged(nameof(BgLightOpacity));
        PersistSettings();
    }

    partial void OnBgBlurChanged(double value)
    {
        var clamped = Math.Clamp(value, 0, 40);
        if (!clamped.Equals(value))
        {
            BgBlur = clamped;
            return;
        }

        PersistSettings();
    }

    [RelayCommand]
    private void SetBackground(string path)
    {
        Bitmap bitmap;
        try
        {
            bitmap = new Bitmap(path);
        }
        // The picker filter only checks the extension; an unreadable file must not take the app down.
        catch (Exception)
        {
            ShowBanner("无法读取这张图片，请换一张试试。");
            return;
        }

        var previous = _customBg;
        _customBg = bitmap;
        BgBitmap = bitmap;
        CustomBackgroundPath = path;
        previous?.Dispose();

        // The art only shows on home, so jump there and open the tuning popup.
        CurrentPage = "home";
        IsBgPopOpen = true;
    }

    [RelayCommand]
    private void OpenBgPop()
    {
        // The popup previews the home art, which only renders on that page.
        CurrentPage = "home";
        IsBgPopOpen = true;
    }

    [RelayCommand]
    private void CloseBgPop() => IsBgPopOpen = false;

    [RelayCommand]
    private void ResetBackground()
    {
        var previous = _customBg;
        _customBg = null;
        BgBitmap = PixelArtService.CreateBackgroundBitmap(IsDark);
        CustomBackgroundPath = null;
        BgBlur = 0;
        BgBrightness = 0;
        IsBgPopOpen = false;
        previous?.Dispose();
    }

    // ==========================================================
    // Accounts
    // ==========================================================

    [ObservableProperty]
    private IImage? _avatarBitmap;

    public ObservableCollection<AccountModel> Accounts { get; } = new();

    [ObservableProperty]
    private AccountModel? _currentAccount;

    [ObservableProperty]
    private bool _isAccPopOpen;

    partial void OnCurrentAccountChanged(AccountModel? value)
    {
        foreach (var a in Accounts) a.IsCurrent = ReferenceEquals(a, value);
        AvatarBitmap = value?.AvatarBitmap;
    }

    [RelayCommand]
    private void ToggleAccPop()
    {
        IsAccPopOpen = !IsAccPopOpen;
        IsInstPopOpen = false;
    }

    [RelayCommand]
    private void SelectAccount(AccountModel account)
    {
        CurrentAccount = account;
        _launcher.Accounts.SetCurrent(account.Source);
        IsAccPopOpen = false;
    }

    [RelayCommand]
    private async Task AddOfflineAccountAsync()
    {
        IsAccPopOpen = false;

        var name = await ShowDialogAsync("添加离线账户", "离线账户只在这台电脑上有效，名字可以随时改。",
            "玩家名", "", "添加");
        if (name is null) return;

        name = name.Trim();
        if (!OfflineAccountFactory.IsValidPlayerName(name))
        {
            ShowBanner("玩家名需要是 3-16 位的字母、数字或下划线。");
            return;
        }

        _launcher.Accounts.AddOffline(name);
        RefreshAccounts();
    }

    [RelayCommand]
    private void AddMicrosoftAccount()
    {
        IsAccPopOpen = false;
        // Reserved: the OAuth flow needs an Azure application (client) id before it can be wired up.
        ShowBanner("微软账户登录尚未开放：需要在 Azure 注册应用并填入 Client ID 后才能启用。");
    }

    [RelayCommand]
    private async Task RemoveAccountAsync(AccountModel account)
    {
        IsAccPopOpen = false;

        var ok = await ShowDialogAsync("删除账户", $"确定要删除「{account.Name}」吗？", null, null, "删除");
        if (ok is null) return;

        _launcher.Accounts.Remove(account.Source);
        RefreshAccounts();
    }

    private void RefreshAccounts()
    {
        var currentId = _launcher.Accounts.Current?.Id;

        Accounts.Clear();
        foreach (var account in _launcher.Accounts.Accounts)
        {
            var model = new AccountModel(account) { AvatarBitmap = DefaultAvatar };
            Accounts.Add(model);
            _ = LoadAvatarAsync(model);
        }

        CurrentAccount = Accounts.FirstOrDefault(a => a.Id == currentId) ?? Accounts.FirstOrDefault();
        AvatarBitmap = CurrentAccount?.AvatarBitmap;
    }

    private static IImage DefaultAvatar { get; } = PixelArtService.CreateAvatarBitmap();

    private async Task LoadAvatarAsync(AccountModel model)
    {
        // Microsoft accounts carry a real profile; an offline name usually resolves to nothing and
        // the service already falls back to the default skin.
        var pixels = await SkinService.FetchSkinOnlineAsync(model.Name) ?? SkinService.CreateDefaultSteveSkin();
        model.AvatarBitmap = SkinService.CreateAvatarFromSkin(pixels);

        if (ReferenceEquals(model, CurrentAccount)) AvatarBitmap = model.AvatarBitmap;
    }

    // ==========================================================
    // Instances
    // ==========================================================

    /// <summary>Every instance, shown in the dock popover.</summary>
    public ObservableCollection<InstanceModel> Instances { get; } = new();

    /// <summary>The instances page grid, narrowed by the search box.</summary>
    public ObservableCollection<InstanceModel> VisibleInstances { get; } = new();

    [ObservableProperty]
    private InstanceModel? _currentInstance;

    [ObservableProperty]
    private bool _isInstPopOpen;

    [ObservableProperty]
    private string _instanceSearch = "";

    public bool HasInstances => Instances.Count > 0;

    /// <summary>Drives the transparent overlay that closes a popover when the user clicks elsewhere.</summary>
    public bool HasOpenPopover => IsAccPopOpen || IsInstPopOpen || IsDlPopOpen;

    partial void OnIsAccPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnIsInstPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnIsDlPopOpenChanged(bool value) => OnPropertyChanged(nameof(HasOpenPopover));

    partial void OnCurrentInstanceChanged(InstanceModel? value)
    {
        foreach (var i in Instances) i.IsCurrent = ReferenceEquals(i, value);
        OnPropertyChanged(nameof(CanLaunch));
    }

    partial void OnInstanceSearchChanged(string value) => ApplyInstanceFilter();

    [RelayCommand]
    private void ToggleInstPop()
    {
        IsInstPopOpen = !IsInstPopOpen;
        IsAccPopOpen = false;
    }

    [RelayCommand]
    private void SelectInstance(InstanceModel instance)
    {
        CurrentInstance = instance;
        _launcher.SelectInstance(instance.Source);
        IsInstPopOpen = false;
        CurrentPage = "home";
    }

    [RelayCommand]
    private void CloseAllPopovers()
    {
        IsAccPopOpen = false;
        IsInstPopOpen = false;
        IsDlPopOpen = false;
    }

    /// <summary>
    /// The version browser already lists everything that can be installed, so "new" is a jump there
    /// rather than a second copy of the same list.
    /// </summary>
    [RelayCommand]
    private void NewInstance()
    {
        CurrentResCategory = "game";
        CurrentPage = "download";
        ShowBanner("选择一个版本开始安装，装好后会自动成为当前实例。");
    }

    [RelayCommand]
    private async Task RenameInstanceAsync(InstanceModel instance)
    {
        var name = await ShowDialogAsync("重命名实例", instance.Name, "新名字", instance.Name, "保存");
        if (name is null || string.IsNullOrWhiteSpace(name)) return;

        _launcher.Instances.Rename(instance.Id, name);
        RefreshInstances();
    }

    [RelayCommand]
    private async Task DeleteInstanceAsync(InstanceModel instance)
    {
        var ok = await ShowDialogAsync("删除实例",
            $"「{instance.Name}」的版本文件夹会一并删除，存档也在里面。确定吗？", null, null, "删除");
        if (ok is null) return;

        if (CurrentInstance is { } current && current.Id == instance.Id)
        {
            _launcher.SelectInstance(null);
        }

        _launcher.Instances.Delete(instance.Id);
        RefreshInstances();
    }

    [RelayCommand]
    private void OpenInstanceFolder(InstanceModel instance) =>
        OpenFolder(_launcher.Instances.GameDirectoryFor(instance.Source));

    private void RefreshInstances()
    {
        var currentId = _launcher.CurrentInstance?.Id;

        Instances.Clear();
        foreach (var instance in _launcher.Instances.Instances)
        {
            Instances.Add(new InstanceModel(instance) { IconBitmap = IconFor(instance) });
        }

        CurrentInstance = Instances.FirstOrDefault(i => i.Id == currentId) ?? Instances.FirstOrDefault();
        OnPropertyChanged(nameof(HasInstances));
        ApplyInstanceFilter();
    }

    private void ApplyInstanceFilter()
    {
        var query = InstanceSearch.Trim();

        VisibleInstances.Clear();
        foreach (var model in Instances)
        {
            if (query.Length == 0 ||
                model.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                model.MetaText.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                VisibleInstances.Add(model);
            }
        }
    }

    private IImage IconFor(Instance instance)
    {
        if (!string.IsNullOrEmpty(instance.IconPath) && File.Exists(instance.IconPath))
        {
            if (_iconCache.TryGetValue(instance.IconPath, out var cached)) return cached;

            try
            {
                var loaded = new Bitmap(instance.IconPath);
                _iconCache[instance.IconPath] = loaded;
                return loaded;
            }
            catch (Exception)
            {
                // A corrupt icon.png falls through to the generated block below.
            }
        }

        // GetHashCode is randomised per process, so hash by hand to keep the icon stable.
        var hash = instance.Id.Aggregate(0, (acc, c) => acc * 31 + c);
        return PixelArtService.CreateBlockBitmap(BlockIcons[Math.Abs(hash) % BlockIcons.Length]);
    }

    private void OpenFolder(string path)
    {
        try
        {
            NclPaths.EnsureDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ShowBanner($"无法打开文件夹：{ex.Message}");
        }
    }

    // ==========================================================
    // Launch
    // ==========================================================

    [ObservableProperty]
    private bool _isLaunching;

    [ObservableProperty]
    private bool _isGameRunning;

    [ObservableProperty]
    private string _launchBtnText = "启动游戏";

    [ObservableProperty]
    private double _launchProgress;

    [ObservableProperty]
    private string _launchStepText = "";

    [ObservableProperty]
    private string _launchPctText = "0%";

    public bool CanLaunch => !IsLaunching && !IsGameRunning && CurrentInstance is not null;

    partial void OnIsLaunchingChanged(bool value) => OnPropertyChanged(nameof(CanLaunch));

    partial void OnIsGameRunningChanged(bool value)
    {
        OnPropertyChanged(nameof(CanLaunch));
        LaunchBtnText = value ? "游戏运行中" : "启动游戏";
    }

    [RelayCommand]
    private async Task LaunchGameAsync()
    {
        if (IsLaunching || IsGameRunning) return;

        if (CurrentInstance is not { } instance)
        {
            ShowBanner("还没有实例。先到「资源 → 游戏」安装一个版本。");
            return;
        }

        if (CurrentAccount is not { } account)
        {
            ShowBanner("请先添加一个账户。");
            return;
        }

        IsLaunching = true;
        LaunchProgress = 0;
        LaunchPctText = "0%";
        LaunchStepText = "准备中";
        LaunchBtnText = "启动中";

        var task = BeginTask($"启动 {instance.Name}");
        var hidden = false;

        try
        {
            var session = await _launcher.LaunchAsync(instance.Source, account.Source, TrackLaunch(task));

            FinishTask(task, "已启动");
            IsLaunching = false;
            IsGameRunning = true;

            if (HideOnLaunch)
            {
                hidden = true;
                HideWindowRequested?.Invoke();
            }

            var exitCode = await session.WaitForExitAsync();
            if (exitCode != 0 && session.Diagnose() is { } diagnosis)
            {
                ShowBanner($"游戏异常退出（{exitCode}）：{diagnosis}");
            }
        }
        catch (Exception ex)
        {
            FailTask(task, ex);
            ShowBanner(ex is LaunchException ? ex.Message : $"启动失败：{ex.Message}");
        }
        finally
        {
            IsLaunching = false;
            IsGameRunning = _launcher.IsGameRunning;
            LaunchBtnText = IsGameRunning ? "游戏运行中" : "启动游戏";
            if (hidden) ShowWindowRequested?.Invoke();
            RefreshInstances();
        }
    }

    // ==========================================================
    // Downloads
    // ==========================================================

    public ObservableCollection<DownloadTaskModel> Downloads { get; } = new();

    public bool HasDownloads => Downloads.Count > 0;

    public void RefreshHasDownloads() => OnPropertyChanged(nameof(HasDownloads));

    /// <summary>The task list hangs off the download page as a popover instead of owning a page.</summary>
    [ObservableProperty]
    private bool _isDlPopOpen;

    [RelayCommand]
    private void ToggleDlPop()
    {
        IsDlPopOpen = !IsDlPopOpen;
        IsAccPopOpen = false;
        IsInstPopOpen = false;
    }

    [RelayCommand]
    private void ClearDownloads()
    {
        foreach (var task in Downloads.Where(t => t.IsFinished).ToList()) Downloads.Remove(task);
        OnPropertyChanged(nameof(HasDownloads));
    }

    private DownloadTaskModel BeginTask(string name)
    {
        var task = new DownloadTaskModel { Name = name, Cts = new CancellationTokenSource() };
        Downloads.Insert(0, task);
        OnPropertyChanged(nameof(HasDownloads));
        return task;
    }

    private static void FinishTask(DownloadTaskModel task, string status)
    {
        task.Status = status;
        task.Progress = 100;
        task.PercentText = "100%";
        task.Detail = "";
        task.IsFinished = true;
    }

    private static void FailTask(DownloadTaskModel task, Exception ex)
    {
        task.Status = "失败";
        task.Detail = ex.Message;
        task.IsFinished = true;
    }

    /// <summary>An aborted run is not a failure: keep the row resumable instead of red.</summary>
    private static void EndTask(DownloadTaskModel task, Exception ex)
    {
        if (task.Cts?.IsCancellationRequested == true)
        {
            task.Status = task.PauseRequested ? "已暂停" : "已取消";
            task.Detail = task.PauseRequested ? "点「继续」接着下，已完成的文件会直接跳过。" : "";
            task.IsFinished = true;
            return;
        }

        FailTask(task, ex);
    }

    [RelayCommand]
    private void PauseDownloadTask(DownloadTaskModel task)
    {
        task.PauseRequested = true;
        task.Cts?.Cancel();
    }

    [RelayCommand]
    private void CancelDownloadTask(DownloadTaskModel task)
    {
        task.PauseRequested = false;
        task.Cts?.Cancel();
    }

    [RelayCommand]
    private async Task ResumeDownloadTaskAsync(DownloadTaskModel task)
    {
        if (task.Retry is not { } retry) return;

        task.Cts?.Dispose();
        task.Cts = new CancellationTokenSource();
        task.PauseRequested = false;
        task.IsFinished = false;
        task.Status = "等待中";
        task.Detail = "";

        try
        {
            await retry(task.Cts.Token);
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
        }
    }

    /// <summary>Progress reports arrive on downloader threads, so they are posted to the UI thread.</summary>
    private static IProgress<InstallProgress> Track(DownloadTaskModel task) =>
        new CallbackProgress<InstallProgress>(p => Dispatcher.UIThread.Post(() => Apply(task, p)));

    /// <summary>The launch button's progress card mirrors the same reports the download page shows.</summary>
    private IProgress<InstallProgress> TrackLaunch(DownloadTaskModel task) =>
        new CallbackProgress<InstallProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Apply(task, p);
            LaunchStepText = p.Stage;
            LaunchProgress = Math.Clamp(p.Fraction * 100, 0, 100);
            LaunchPctText = $"{(int)LaunchProgress}%";
        }));

    private static void Apply(DownloadTaskModel task, InstallProgress progress)
    {
        task.Status = progress.Stage;
        task.Progress = Math.Clamp(progress.Fraction * 100, 0, 100);
        task.PercentText = $"{(int)task.Progress}%";
        task.Detail = progress.Detail ?? "";
    }

    // ==========================================================
    // Resources
    // ==========================================================

    [ObservableProperty]
    private string _currentResCategory = "game";

    public ObservableCollection<ResourceModel> FilteredResources { get; } = new();

    [ObservableProperty]
    private string _resourceSearch = "";

    [ObservableProperty]
    private bool _isLoadingResources;

    [ObservableProperty]
    private string _resourceEmptyText = "暂无内容";

    public bool HasResources => FilteredResources.Count > 0;

    partial void OnCurrentResCategoryChanged(string value) => UpdateFilteredResources();

    partial void OnResourceSearchChanged(string value) => UpdateFilteredResources();

    [RelayCommand]
    private void SelectResCategory(string category) => CurrentResCategory = category;

    private void UpdateFilteredResources()
    {
        _resourceLoad?.Cancel();
        var token = (_resourceLoad = new CancellationTokenSource()).Token;

        if (CurrentResCategory == "game")
        {
            IsLoadingResources = false;
            ResourceEmptyText = _gameResources.Count == 0 ? "正在加载版本列表…" : "没有匹配的版本";
            FillFiltered(_gameResources);
            return;
        }

        _ = LoadModrinthAsync(CurrentResCategory, ResourceSearch.Trim(), token);
    }

    private void FillFiltered(IEnumerable<ResourceModel> source)
    {
        var query = ResourceSearch.Trim();

        FilteredResources.Clear();
        foreach (var item in source)
        {
            if (CurrentResCategory == "game" && query.Length > 0 &&
                !item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredResources.Add(item);
        }

        OnPropertyChanged(nameof(HasResources));
    }

    private async Task LoadGameVersionsAsync()
    {
        try
        {
            var source = await _launcher.ResolveSourceAsync().ConfigureAwait(true);
            var manifest = await _launcher.Manifests.GetAsync(source).ConfigureAwait(true);

            var installed = new HashSet<string>(
                _launcher.Instances.Instances.Select(i => i.VersionId), StringComparer.OrdinalIgnoreCase);

            var icon = PixelArtService.CreateBlockBitmap("grass");

            _gameResources.Clear();
            foreach (var version in manifest.Versions.Where(v => v.IsRelease).Take(200))
            {
                var known = installed.Contains(version.Id);
                _gameResources.Add(new ResourceModel
                {
                    Name = version.Id,
                    Desc = version.Description,
                    Category = "game",
                    VersionId = version.Id,
                    IconBitmap = icon,
                    ActionText = known ? "已安装" : "安装",
                    IsActionEnabled = !known,
                });
            }
        }
        catch (Exception ex)
        {
            ResourceEmptyText = $"版本列表加载失败：{ex.Message}";
        }

        if (CurrentResCategory == "game") UpdateFilteredResources();
    }

    private async Task LoadModrinthAsync(string category, string query, CancellationToken ct)
    {
        IsLoadingResources = true;
        ResourceEmptyText = "正在搜索…";
        FilteredResources.Clear();

        try
        {
            // Let typing settle before spending a request on every keystroke.
            await Task.Delay(350, ct).ConfigureAwait(true);

            if (CurrentInstance is not { } instance)
            {
                ResourceEmptyText = "先选择一个实例，才能按它的版本和加载器筛选。";
                return;
            }

            var projectType = category switch
            {
                "mod" => ModrinthClient.ProjectTypeMod,
                "rp" => ModrinthClient.ProjectTypeResourcePack,
                "shader" => ModrinthClient.ProjectTypeShader,
                _ => null,
            };

            if (projectType is null)
            {
                ResourceEmptyText = "整合包安装即将支持。";
                return;
            }

            var loader = LoaderFacet(instance.Source.Loader);
            var hits = await _modrinth.SearchAsync(projectType, query, 30, 0,
                instance.Source.VersionId, loader, ct).ConfigureAwait(true);

            if (ct.IsCancellationRequested || CurrentResCategory != category) return;

            var icon = PixelArtService.CreateBlockBitmap(category switch
            {
                "mod" => "dirt",
                "shader" => "log",
                _ => "stone",
            });

            FilteredResources.Clear();
            foreach (var hit in hits)
            {
                FilteredResources.Add(new ResourceModel
                {
                    Name = hit.Title,
                    Desc = hit.Description,
                    Stat = FormatCount(hit.Downloads),
                    Category = category,
                    ProjectId = hit.Id,
                    IconBitmap = icon,
                });
            }

            ResourceEmptyText = query.Length == 0 ? "没有找到内容" : $"没有和「{query}」匹配的内容";
        }
        catch (OperationCanceledException)
        {
            // A newer search replaced this one.
        }
        catch (Exception ex)
        {
            ResourceEmptyText = $"加载失败：{ex.Message}";
        }
        finally
        {
            if (!ct.IsCancellationRequested)
            {
                IsLoadingResources = false;
                OnPropertyChanged(nameof(HasResources));
            }
        }
    }

    /// <summary>Only mods care which loader the instance uses; packs and shaders do not.</summary>
    private static string? LoaderFacet(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Quilt" => "quilt",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        _ => null,
    };

    private static string FormatCount(int downloads) => downloads switch
    {
        >= 100_000_000 => $"{downloads / 100_000_000.0:0.#}亿",
        >= 10_000 => $"{downloads / 10_000.0:0.#}万",
        _ => downloads.ToString(),
    };

    [RelayCommand]
    private async Task InstallResourceAsync(ResourceModel resource)
    {
        if (resource.Category == "game")
        {
            await InstallVersionAsync(resource);
        }
        else
        {
            await InstallModrinthAsync(resource);
        }
    }

    private async Task InstallVersionAsync(ResourceModel resource)
    {
        var versionId = resource.VersionId!;

        var name = await ShowDialogAsync("新建实例", $"将安装 Minecraft {versionId}。", "实例名称", versionId, "安装");
        if (name is null) return;

        var task = BeginTask($"安装 {versionId}");
        task.Retry = ct => RunVersionInstallAsync(resource, name, task, ct);
        await RunVersionInstallAsync(resource, name, task, task.Cts!.Token);
    }

    private async Task RunVersionInstallAsync(ResourceModel resource, string name, DownloadTaskModel task, CancellationToken ct)
    {
        var versionId = resource.VersionId!;

        resource.IsActionEnabled = false;
        resource.ActionText = "安装中";

        try
        {
            var instance = await _launcher.CreateInstanceAsync(versionId, name, Track(task), ct);

            FinishTask(task, "已完成");
            resource.ActionText = "已安装";
            _launcher.SelectInstance(instance);
            RefreshInstances();
            ShowBanner($"「{instance.DisplayName}」已经可以启动了。");
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
            resource.ActionText = "重试";
            resource.IsActionEnabled = true;
            if (task.Status is not ("已暂停" or "已取消")) ShowBanner($"安装 {versionId} 失败：{ex.Message}");
        }
    }

    private async Task InstallModrinthAsync(ResourceModel resource)
    {
        if (CurrentInstance is null)
        {
            ShowBanner("先在首页选择一个实例。");
            return;
        }

        var task = BeginTask($"下载 {resource.Name}");
        task.Retry = ct => RunModrinthInstallAsync(resource, task, ct);
        await RunModrinthInstallAsync(resource, task, task.Cts!.Token);
    }

    private async Task RunModrinthInstallAsync(ResourceModel resource, DownloadTaskModel task, CancellationToken ct)
    {
        if (resource.ProjectId is not { } projectId) return;

        if (CurrentInstance is not { } instance)
        {
            ShowBanner("先在首页选择一个实例。");
            return;
        }

        resource.IsActionEnabled = false;
        resource.ActionText = "下载中";

        try
        {
            var source = await _launcher.ResolveSourceAsync(ct);
            var loader = resource.Category == "mod" ? LoaderFacet(instance.Source.Loader) : null;
            var version = await _modrinth.FindVersionAsync(projectId, instance.Source.VersionId, loader, ct);

            if (version is null)
            {
                throw new InvalidOperationException($"没有适配 {instance.Source.VersionId} 的文件");
            }

            var gameDirectory = _launcher.Instances.GameDirectoryFor(instance.Source);
            await _modrinthInstaller.InstallAsync(version, gameDirectory, resource.Category, source,
                new CallbackProgress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
                {
                    task.Status = "下载中";
                    task.Progress = Math.Clamp(p.Percent * 100, 0, 100);
                    task.PercentText = $"{(int)task.Progress}%";
                    task.Detail = p.CurrentItem ?? "";
                })), ct);

            FinishTask(task, "已完成");
            resource.ActionText = "已安装";
            ShowBanner($"「{resource.Name}」已放进 {instance.Name} 的{CategoryName(resource.Category)}。");

            if (resource.Category == "mod" && instance.Source.Loader == "原版")
            {
                ShowBanner("这个实例没有安装 Mod 加载器，Mod 不会生效。");
            }
        }
        catch (Exception ex)
        {
            EndTask(task, ex);
            resource.ActionText = "重试";
            resource.IsActionEnabled = true;
            if (task.Status is not ("已暂停" or "已取消")) ShowBanner($"下载失败：{ex.Message}");
        }
    }

    private static string CategoryName(string category) => category switch
    {
        "mod" => "mods 文件夹",
        "rp" => "资源包文件夹",
        "shader" => "光影文件夹",
        _ => "游戏目录",
    };

    // ==========================================================
    // Settings
    // ==========================================================

    [ObservableProperty]
    private string _currentSettingsTab = "游戏";

    [ObservableProperty]
    private int _maxMemoryGb = 6;

    [ObservableProperty]
    private bool _versionIsolation = true;

    [ObservableProperty]
    private bool _hideOnLaunch;

    [ObservableProperty]
    private string _downloadSource = "自动";

    [ObservableProperty]
    private int _downloadThreads = 16;

    [ObservableProperty]
    private bool _isDark = true;

    /// <summary>Shown read-only: moving it mid-session would leave the loaded instances pointing elsewhere.</summary>
    public string GameRoot => _launcher.Settings.EffectiveGameRoot;

    public string SourceHint => DownloadSource switch
    {
        "官方" => "直连 Mojang，国内速度较慢。",
        "BMCLAPI" => "国内镜像，通常最快。",
        _ => "启动时各测一次，用更快的那个。",
    };

    [RelayCommand]
    private void SelectSettingsTab(string tab) => CurrentSettingsTab = tab;

    partial void OnCurrentSettingsTabChanged(string value)
    {
        if (value == "Java") RefreshJavaOptions();
    }

    partial void OnMaxMemoryGbChanged(int value) => PersistSettings();

    partial void OnVersionIsolationChanged(bool value) => PersistSettings();

    partial void OnHideOnLaunchChanged(bool value) => PersistSettings();

    partial void OnDownloadThreadsChanged(int value)
    {
        if (_applyingSettings) return;

        // Both downloaders cap connections by this count; rebuild so the next task picks it up.
        _launcher.Settings.DownloadThreads = value;
        _modrinthInstaller = new ModrinthInstaller(concurrency: value);
        PersistSettings();
    }

    partial void OnDownloadSourceChanged(string value)
    {
        OnPropertyChanged(nameof(SourceHint));
        if (_applyingSettings) return;

        _launcher.Settings.DownloadSource = ParseSource(value);
        _launcher.InvalidateSource();
        PersistSettings();
    }

    [RelayCommand]
    private void SetDownloadSource(string source) => DownloadSource = source;

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDark = !IsDark;
        ApplyTheme();
        if (!IsCustomBackground)
        {
            BgBitmap = PixelArtService.CreateBackgroundBitmap(IsDark);
        }

        PersistSettings();
    }

    private void ApplyTheme()
    {
        if (Application.Current is not null)
        {
            Application.Current.RequestedThemeVariant = IsDark ? ThemeVariant.Dark : ThemeVariant.Light;
        }
    }

    [RelayCommand]
    private void OpenGameRoot() => OpenFolder(GameRoot);

    // ---- Java ----

    public ObservableCollection<JavaOptionModel> JavaOptions { get; } = new();

    [ObservableProperty]
    private string _javaLabel = "自动";

    [ObservableProperty]
    private bool _isJavaChooserOpen;

    [RelayCommand]
    private void ToggleJavaChooser() => IsJavaChooserOpen = !IsJavaChooserOpen;

    private void RefreshJavaOptions()
    {
        var selected = _launcher.Settings.JavaPath;

        JavaOptions.Clear();
        JavaOptions.Add(new JavaOptionModel { Label = "自动（按版本挑选）", Path = null, IsSelected = selected is null });

        foreach (var runtime in JavaRuntimeLocator.FindAll())
        {
            JavaOptions.Add(new JavaOptionModel
            {
                Label = $"Java {runtime.MajorVersion} · {runtime.Label}",
                Path = runtime.ExecutablePath,
                IsSelected = string.Equals(selected, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase),
            });
        }

        JavaLabel = JavaOptions.FirstOrDefault(o => o.IsSelected)?.Label ?? "自动";
    }

    [RelayCommand]
    private void SelectJava(JavaOptionModel option)
    {
        foreach (var o in JavaOptions) o.IsSelected = ReferenceEquals(o, option);

        _launcher.Settings.JavaPath = option.Path;
        JavaLabel = option.Label;
        IsJavaChooserOpen = false;
        PersistSettings();
    }

    [RelayCommand]
    private void SetJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var runtime = JavaRuntimeLocator.Probe(path, Path.GetFileName(Path.GetDirectoryName(path)!));
        if (runtime is null)
        {
            ShowBanner("这个文件不是可用的 Java 运行时，请选择 javaw.exe。");
            return;
        }

        _launcher.Settings.JavaPath = runtime.ExecutablePath;
        RefreshJavaOptions();
        IsJavaChooserOpen = false;
        PersistSettings();
        ShowBanner($"已选择 Java {runtime.MajorVersion}。");
    }

    // ---- persistence ----

    private void ApplySettingsToUi(Core.Settings.LauncherSettings settings)
    {
        _applyingSettings = true;
        try
        {
            MaxMemoryGb = Math.Clamp(settings.MaxMemoryMb / 1024, 1, 32);
            VersionIsolation = settings.VersionIsolation;
            HideOnLaunch = settings.HideOnLaunch;
            DownloadThreads = settings.DownloadThreads;
            DownloadSource = settings.DownloadSource switch
            {
                SourceKind.Official => "官方",
                SourceKind.Bmclapi => "BMCLAPI",
                _ => "自动",
            };
            IsDark = settings.IsDark;
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplyTheme();
        OnPropertyChanged(nameof(SourceHint));
        JavaLabel = settings.JavaPath is null ? "自动" : Path.GetFileName(settings.JavaPath);

        if (!string.IsNullOrEmpty(settings.CustomBackgroundPath) &&
            File.Exists(settings.CustomBackgroundPath))
        {
            try
            {
                _customBg?.Dispose();
                _customBg = new Bitmap(settings.CustomBackgroundPath);
                BgBitmap = _customBg;
                CustomBackgroundPath = settings.CustomBackgroundPath;
            }
            catch (Exception)
            {
                _customBg = null;
                CustomBackgroundPath = null;
            }
        }

        BgBlur = settings.BackgroundBlur;
        BgBrightness = settings.BackgroundBrightness;
    }

    private static SourceKind ParseSource(string label) => label switch
    {
        "官方" => SourceKind.Official,
        "BMCLAPI" => SourceKind.Bmclapi,
        _ => SourceKind.Auto,
    };

    private void PersistSettings()
    {
        if (_applyingSettings) return;

        var settings = _launcher.Settings;
        settings.MaxMemoryMb = MaxMemoryGb * 1024;
        settings.VersionIsolation = VersionIsolation;
        settings.HideOnLaunch = HideOnLaunch;
        settings.DownloadThreads = DownloadThreads;
        settings.IsDark = IsDark;
        settings.CustomBackgroundPath = CustomBackgroundPath;
        settings.BackgroundBlur = BgBlur;
        settings.BackgroundBrightness = BgBrightness;

        // Sliders fire on every tick; write once they settle.
        _saveDebounce?.Cancel();
        var debounce = new CancellationTokenSource();
        _saveDebounce = debounce;
        _ = SaveAfterDelayAsync(debounce.Token);
    }

    private async Task SaveAfterDelayAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(400, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        _launcher.SaveSettings();
    }

    // ==========================================================
    // Dialog & banner
    // ==========================================================

    [ObservableProperty]
    private bool _isDialogOpen;

    [ObservableProperty]
    private string _dialogTitle = "";

    [ObservableProperty]
    private string _dialogMessage = "";

    [ObservableProperty]
    private string _dialogInput = "";

    [ObservableProperty]
    private string _dialogPlaceholder = "";

    [ObservableProperty]
    private string _dialogConfirmText = "确定";

    [ObservableProperty]
    private bool _dialogHasInput;

    public bool HasDialogMessage => DialogMessage.Length > 0;

    partial void OnDialogMessageChanged(string value) => OnPropertyChanged(nameof(HasDialogMessage));

    /// <summary>
    /// Shows the in-window prompt. Returns the entered text, or "" for a confirm-only dialog, or
    /// null when the user cancelled.
    /// </summary>
    private Task<string?> ShowDialogAsync(string title, string message, string? placeholder,
        string? initial, string confirmText)
    {
        _dialogTcs?.TrySetResult(null);

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _dialogTcs = tcs;

        DialogTitle = title;
        DialogMessage = message;
        DialogHasInput = placeholder is not null;
        DialogPlaceholder = placeholder ?? "";
        DialogInput = initial ?? "";
        DialogConfirmText = confirmText;
        IsDialogOpen = true;

        return tcs.Task;
    }

    [RelayCommand]
    private void ConfirmDialog()
    {
        var tcs = _dialogTcs;
        _dialogTcs = null;
        IsDialogOpen = false;
        tcs?.TrySetResult(DialogHasInput ? DialogInput : "");
    }

    [RelayCommand]
    private void CancelDialog()
    {
        var tcs = _dialogTcs;
        _dialogTcs = null;
        IsDialogOpen = false;
        tcs?.TrySetResult(null);
    }

    [ObservableProperty]
    private string _bannerText = "";

    public bool IsBannerOpen => BannerText.Length > 0;

    partial void OnBannerTextChanged(string value) => OnPropertyChanged(nameof(IsBannerOpen));

    private void ShowBanner(string text)
    {
        BannerText = text;

        _bannerTimer?.Cancel();
        var timer = new CancellationTokenSource();
        _bannerTimer = timer;
        _ = DismissBannerAsync(timer.Token);
    }

    private async Task DismissBannerAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(5000, ct).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        BannerText = "";
    }

    [RelayCommand]
    private void DismissBanner()
    {
        _bannerTimer?.Cancel();
        BannerText = "";
    }
}
