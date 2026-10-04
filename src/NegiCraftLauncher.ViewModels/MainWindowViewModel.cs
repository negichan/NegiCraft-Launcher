using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NegiCraftLauncher.Core;
using NegiCraftLauncher.Core.Auth;
using NegiCraftLauncher.Core.Instances;
using NegiCraftLauncher.Core.Java;
using NegiCraftLauncher.Core.Launch;
using NegiCraftLauncher.Core.Modrinth;
using NegiCraftLauncher.Core.Net;
using NegiCraftLauncher.Core.Versions;
using NegiCraftLauncher.Core.WallpaperEngine;
using NegiCraftLauncher.Raster;
using SourceKind = NegiCraftLauncher.Core.Settings.DownloadSource;

namespace NegiCraftLauncher.ViewModels;

public partial class MainWindowViewModel : ViewModelBase
{
    private static readonly string[] BlockIcons = { "grass", "stone", "tnt", "dirt", "log" };

    private readonly Launcher _launcher;
    private readonly ModrinthClient _modrinth = new();
    private ModrinthInstaller _modrinthInstaller = new();
    private readonly Dictionary<string, PixelBuffer> _iconCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ResourceModel> _gameResources = new();

    private bool _applyingSettings;
    private CancellationTokenSource? _saveDebounce;
    private CancellationTokenSource? _resourceLoad;
    private CancellationTokenSource? _bannerTimer;
    private TaskCompletionSource<string?>? _dialogTcs;

    /// <summary>The view hides and restores itself; the VM only says when.</summary>
    public event Action? HideWindowRequested;
    public event Action? ShowWindowRequested;
    public event Action? OpenPetRequested;
    public event Action? RecallPetRequested;

    /// <summary>
    /// Raised whenever the light/dark choice changes. The VM does not know how a given UI
    /// framework switches themes, so the view subscribes and applies it.
    /// </summary>
    public event Action<bool>? ThemeChanged;

    [ObservableProperty]
    private bool _isPetActive;

    public string? PetCustomName
    {
        get => _launcher.Settings.PetCustomName;
        set
        {
            var trimmed = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            if (_launcher.Settings.PetCustomName != trimmed)
            {
                _launcher.Settings.PetCustomName = trimmed;
                PersistSettings();
                OnPropertyChanged();
                OnPropertyChanged(nameof(EffectivePetName));
            }
        }
    }

    public string EffectivePetName =>
        !string.IsNullOrWhiteSpace(PetCustomName)
            ? PetCustomName
            : (CurrentAccount?.Name ?? "pingplus");

    /// <summary>
    /// 桌宠是否用 GPU 硬件渲染。设置页与桌宠右键菜单改的是同一个值（走 <c>IPetHost.UseGpu</c>）。
    ///
    /// <para>只有 Windows(WPF) 侧有这条后端；Avalonia 侧读到了也只是忽略，但值照存不误 ——
    /// 两边共用一份 settings.json，不存的话在 mac/Linux 上跑一次就会把 Windows 侧的开关抹掉。</para>
    /// </summary>
    public bool PetUseGpu
    {
        get => _launcher.Settings.PetUseGpu;
        set
        {
            if (_launcher.Settings.PetUseGpu == value) return;

            _launcher.Settings.PetUseGpu = value;
            PersistSettings();
            OnPropertyChanged();
        }
    }

    [RelayCommand]
    private void OpenPet() => OpenPetRequested?.Invoke();

    [RelayCommand]
    private void RecallPet() => RecallPetRequested?.Invoke();

    public MainWindowViewModel()
    {
        _launcher = new Launcher();
        _launcher.Initialize();

        _bgArt = PixelArt.CreateBackground(_launcher.Settings.IsDark);

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

    /// <summary>The generated pixel scene, platform-neutral pixels. Null once a photo is picked.</summary>
    [ObservableProperty]
    private PixelBuffer? _bgArt;

    // Drives which background is shown: the generated pixel scene is stretched to fill, a picked
    // photo keeps its own aspect and is cropped instead. The photo itself is decoded by the view,
    // so the VM stays free of any image codec.
    [ObservableProperty]
    private string? _customBackgroundPath;

    public bool IsCustomBackground => CustomBackgroundPath is not null;

    /// <summary>
    ///     背景是否被用户改过（选了图或选了视频）。"调整 / 恢复默认"两个按钮看的是它 ——
    ///     只按 <see cref="IsCustomBackground" /> 的话，换成视频壁纸后按钮会整排消失。
    /// </summary>
    public bool HasCustomBackground => IsCustomBackground || IsVideoBackground;

    partial void OnCustomBackgroundPathChanged(string? value)
    {
        // 背景图与视频壁纸互斥：后写的赢。这里只在"真的选了图"时清视频，
        // 清空（value is null）不触发，否则 ApplySettingsToUi 读回设置时会互相清空。
        if (value is not null && VideoBackgroundPath is not null) VideoBackgroundPath = null;

        OnPropertyChanged(nameof(IsCustomBackground));
        OnPropertyChanged(nameof(HasCustomBackground));
        PersistSettings();
    }

    /// <summary>
    /// 视频壁纸的文件路径。<b>VM 只持有路径，不持有任何播放器</b> ——
    /// <c>MediaPlayer</c> 是 WPF 类型，而本工程被 Avalonia 侧共用。
    /// 视图订阅这个属性，自己去建播放器和画刷（见 App 的 <c>Media/VideoBackgroundController</c>）。
    /// </summary>
    [ObservableProperty]
    private string? _videoBackgroundPath;

    public bool IsVideoBackground => VideoBackgroundPath is not null;

    partial void OnVideoBackgroundPathChanged(string? value)
    {
        // 视频优先：一旦有视频就把背景图让出来（见 LauncherSettings.VideoBackgroundPath 的注释）。
        if (value is not null && CustomBackgroundPath is not null) CustomBackgroundPath = null;

        OnPropertyChanged(nameof(IsVideoBackground));
        OnPropertyChanged(nameof(HasCustomBackground));
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
        if (!IsReadableImage(path))
        {
            ShowBanner("无法读取这张图片，请换一张试试。");
            return;
        }

        CustomBackgroundPath = path;

        // The art only shows on home, so jump there and open the tuning popup.
        CurrentPage = "home";
        IsBgPopOpen = true;
    }

    /// <summary>
    /// The picker filter only checks the extension. PNGs (the only format the shared layer can
    /// decode) are validated up front; other formats are accepted on the file being readable,
    /// and the view falls back to the generated art if it cannot decode them.
    /// </summary>
    private static bool IsReadableImage(string path)
    {
        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0) return false;
            if (!PngCodec.LooksLikePng(bytes)) return true;
            PngCodec.Decode(bytes);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 视频壁纸的候选扩展名。这里只做粗筛，真正的判据是"能不能播"——
    /// 播不了由视图侧的 <c>MediaFailed</c> 兜底报错。
    /// </summary>
    private static readonly string[] VideoExtensions =
        [".mp4", ".webm", ".avi", ".mkv", ".mov", ".m4v", ".wmv", ".mpg", ".mpeg"];

    [RelayCommand]
    private void SetVideoBackground(string path)
    {
        if (!IsPlayableVideo(path))
        {
            ShowBanner("这不是一个可播放的视频文件。");
            return;
        }

        VideoBackgroundPath = path;

        // 和选图片一样：背景只在首页可见，跳过去并把调节浮层打开。
        CurrentPage = "home";
        IsBgPopOpen = true;
    }

    private static bool IsPlayableVideo(string path)
    {
        try
        {
            return File.Exists(path)
                   && VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 Wallpaper Engine 当前选中的壁纸搬过来。
    ///
    /// <para><b>只搬视频壁纸。</b>场景 / 网页 / 应用三类要靠 WE 自己的引擎渲染（场景是实时 3D 管线、
    /// 网页是内嵌浏览器），我们渲染不了；也刻意<b>不做</b>「拿 preview.jpg 当静态背景」的兜底 ——
    /// 那等于把用户的动态壁纸悄悄换成一张截图，比什么都不做更糟。这几类一律只报类型、不动背景。</para>
    /// </summary>
    [RelayCommand]
    private void SyncWallpaperEngine()
    {
        var current = WallpaperEngineLocator.GetCurrent(refresh: true);
        if (current is null)
        {
            ShowBanner("没找到 Wallpaper Engine 的壁纸设置，确认它已经装好并选过壁纸。");
            return;
        }

        var title = string.IsNullOrWhiteSpace(current.Title) ? "当前壁纸" : current.Title!;

        if (current.Kind != WallpaperEngineKind.Video)
        {
            ShowBanner($"Wallpaper Engine 的「{title}」是{DescribeKind(current.Kind)}，目前只支持视频壁纸。");
            return;
        }

        if (current.VideoPath is null)
        {
            ShowBanner($"Wallpaper Engine 的「{title}」是视频，但文件已经不在原处了。");
            return;
        }

        SetVideoBackground(current.VideoPath);
        ShowBanner($"已同步 Wallpaper Engine 的视频壁纸：{title}");
    }

    /// <summary>壁纸类型的中文说法，直接接在「是……」后面用，所以自带「壁纸」二字。</summary>
    private static string DescribeKind(WallpaperEngineKind kind) => kind switch
    {
        WallpaperEngineKind.Scene => "场景壁纸",
        WallpaperEngineKind.Web => "网页壁纸",
        WallpaperEngineKind.Application => "应用壁纸",
        _ => "未知类型壁纸",
    };

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
        BgArt = PixelArt.CreateBackground(IsDark);
        VideoBackgroundPath = null;
        CustomBackgroundPath = null;
        BgBlur = 0;
        BgBrightness = 0;
        IsBgPopOpen = false;
    }

    // ==========================================================
    // Accounts
    // ==========================================================

    [ObservableProperty]
    private PixelBuffer? _avatarBitmap;

    public ObservableCollection<AccountModel> Accounts { get; } = new();

    public bool HasAccounts => Accounts.Count > 0;

    [ObservableProperty]
    private AccountModel? _currentAccount;

    /// <summary>Drives the home 3D preview: with no account there is nobody to render.</summary>
    public bool HasAccount => CurrentAccount is not null;

    [ObservableProperty]
    private bool _isAccPopOpen;

    partial void OnCurrentAccountChanged(AccountModel? value)
    {
        foreach (var a in Accounts) a.IsCurrent = ReferenceEquals(a, value);
        AvatarBitmap = value?.AvatarBitmap ?? NoAccountAvatar;
        OnPropertyChanged(nameof(HasAccount));
        OnPropertyChanged(nameof(EffectivePetName));
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
            var isSlim = SkinRepository.IsSlimForPlayerName(account.Name);
            var initialAvatar = isSlim ? NoAccountAvatar : DefaultAvatar;
            var model = new AccountModel(account) { AvatarBitmap = initialAvatar };
            Accounts.Add(model);
            _ = LoadAvatarAsync(model);
        }

        CurrentAccount = Accounts.FirstOrDefault(a => a.Id == currentId) ?? Accounts.FirstOrDefault();
        AvatarBitmap = CurrentAccount?.AvatarBitmap ?? NoAccountAvatar;
        OnPropertyChanged(nameof(HasAccounts));
    }

    private static PixelBuffer DefaultAvatar { get; } = AvatarComposer.Create(DefaultSkins.Pixels(slim: false));

    /// <summary>Shown in the sidebar while there is no account at all, or as the initial Alex placeholder.</summary>
    private static PixelBuffer NoAccountAvatar { get; } = AvatarComposer.Create(DefaultSkins.Pixels(slim: true));

    private async Task LoadAvatarAsync(AccountModel model)
    {
        var skinData = await SkinRepository.GetOrFetchAsync(model.Name);

        var pixels = skinData is not null
            ? SkinTexture.Decode(skinData.Bytes)?.ToRenderPixels()
            : null;

        pixels ??= DefaultSkins.Pixels(skinData?.IsSlim ?? SkinRepository.IsSlimForPlayerName(model.Name));

        var avatar = AvatarComposer.Create(pixels);
        await AppDispatcher.Current.InvokeAsync(() =>
        {
            model.AvatarBitmap = avatar;
            if (ReferenceEquals(model, CurrentAccount))
            {
                AvatarBitmap = avatar;
            }
        });
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
        IsInstConfigOpen = false;
    }

    // ==========================================================
    // Instance Configuration Dialog
    // ==========================================================

    [ObservableProperty]
    private bool _isInstConfigOpen;

    [ObservableProperty]
    private InstanceModel? _editingInstance;

    [ObservableProperty]
    private string _configInstName = "";

    [ObservableProperty]
    private bool _configInstIsolated = true;

    [ObservableProperty]
    private bool _configInstCustomJava;

    [ObservableProperty]
    private string? _configInstJavaPath;

    [ObservableProperty]
    private string _configInstJavaLabel = "跟随全局设置";

    [ObservableProperty]
    private bool _isInstJavaChooserOpen;

    [ObservableProperty]
    private bool _configInstCustomMemory;

    [ObservableProperty]
    private int _configInstMemoryGb = 4;

    public ObservableCollection<JavaOptionModel> InstJavaOptions { get; } = new();

    public string GlobalMemoryGbSummary => $"{MaxMemoryGb} GB";
    public string GlobalJavaSummary => string.IsNullOrWhiteSpace(JavaLabel) ? "自动" : JavaLabel;

    [RelayCommand]
    private void OpenInstanceConfig(InstanceModel instance)
    {
        EditingInstance = instance;
        ConfigInstName = instance.Name;
        ConfigInstIsolated = instance.Isolated;
        ConfigInstJavaPath = instance.JavaPath;
        ConfigInstCustomJava = !string.IsNullOrWhiteSpace(instance.JavaPath);

        ConfigInstCustomMemory = instance.MaxMemoryMb.HasValue;
        ConfigInstMemoryGb = instance.MaxMemoryMb.HasValue
            ? Math.Clamp(instance.MaxMemoryMb.Value / 1024, 1, 32)
            : MaxMemoryGb;

        RefreshInstJavaOptions();
        IsInstJavaChooserOpen = false;
        IsInstConfigOpen = true;
    }

    [RelayCommand]
    private void CloseInstanceConfig()
    {
        IsInstConfigOpen = false;
        EditingInstance = null;
        IsInstJavaChooserOpen = false;
    }

    [RelayCommand]
    private void SaveInstanceConfig()
    {
        if (EditingInstance is null) return;

        var name = ConfigInstName.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            ShowBanner("实例名称不能为空。");
            return;
        }

        var javaPath = ConfigInstCustomJava ? ConfigInstJavaPath : null;
        var maxMemoryMb = ConfigInstCustomMemory ? (int?)(ConfigInstMemoryGb * 1024) : null;

        _launcher.Instances.Update(EditingInstance.Id, name, ConfigInstIsolated, javaPath, maxMemoryMb);
        EditingInstance.Refresh();

        ApplyInstanceFilter();
        if (CurrentInstance?.Id == EditingInstance.Id)
        {
            CurrentInstance.Refresh();
        }

        IsInstConfigOpen = false;
        EditingInstance = null;
        ShowBanner($"实例「{name}」配置已保存。");
    }

    [RelayCommand]
    private void ToggleInstJavaChooser() => IsInstJavaChooserOpen = !IsInstJavaChooserOpen;

    [RelayCommand]
    private void SelectInstJava(JavaOptionModel option)
    {
        foreach (var o in InstJavaOptions) o.IsSelected = ReferenceEquals(o, option);
        ConfigInstJavaPath = option.Path;
        ConfigInstJavaLabel = option.Label;
        IsInstJavaChooserOpen = false;
    }

    [RelayCommand]
    public void SetInstJavaPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;

        var runtime = JavaRuntimeLocator.Probe(path, Path.GetFileName(Path.GetDirectoryName(path)!));
        if (runtime is null)
        {
            ShowBanner("指定的路径不是有效的 Java 运行时。");
            return;
        }

        ConfigInstJavaPath = runtime.ExecutablePath;
        ConfigInstCustomJava = true;
        RefreshInstJavaOptions();
        IsInstJavaChooserOpen = false;
    }

    private void RefreshInstJavaOptions()
    {
        InstJavaOptions.Clear();

        InstJavaOptions.Add(new JavaOptionModel
        {
            Label = "自动（按版本要求挑选）",
            Path = null,
            IsSelected = string.IsNullOrEmpty(ConfigInstJavaPath)
        });

        foreach (var runtime in JavaRuntimeLocator.FindAll())
        {
            InstJavaOptions.Add(new JavaOptionModel
            {
                Label = $"Java {runtime.MajorVersion} · {runtime.Label}",
                Path = runtime.ExecutablePath,
                IsSelected = string.Equals(ConfigInstJavaPath, runtime.ExecutablePath, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (string.IsNullOrEmpty(ConfigInstJavaPath))
        {
            ConfigInstJavaLabel = "自动（按版本要求挑选）";
        }
        else
        {
            var matched = InstJavaOptions.FirstOrDefault(o => o.IsSelected);
            ConfigInstJavaLabel = matched?.Label ?? Path.GetFileName(ConfigInstJavaPath);
        }
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

    private PixelBuffer IconFor(Instance instance)
    {
        if (!string.IsNullOrEmpty(instance.IconPath) && File.Exists(instance.IconPath))
        {
            if (_iconCache.TryGetValue(instance.IconPath, out var cached)) return cached;

            try
            {
                // Instance icons are icon.png, so the shared PNG decoder covers them.
                var loaded = PngCodec.Decode(File.ReadAllBytes(instance.IconPath)).ToPixelBuffer();
                _iconCache[instance.IconPath] = loaded;
                return loaded;
            }
            catch
            {
                // A corrupt (or non-PNG) icon falls through to the generated block below.
            }
        }

        // GetHashCode is randomised per process, so hash by hand to keep the icon stable.
        var hash = instance.Id.Aggregate(0, (acc, c) => acc * 31 + c);
        return PixelArt.CreateBlock(BlockIcons[Math.Abs(hash) % BlockIcons.Length]);
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
            IsAccPopOpen = true;
            IsInstPopOpen = false;
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
        new CallbackProgress<InstallProgress>(p => AppDispatcher.Current.Post(() => Apply(task, p)));

    /// <summary>The launch button's progress card mirrors the same reports the download page shows.</summary>
    private IProgress<InstallProgress> TrackLaunch(DownloadTaskModel task) =>
        new CallbackProgress<InstallProgress>(p => AppDispatcher.Current.Post(() =>
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

            var icon = PixelArt.CreateBlock("grass");

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

            var icon = PixelArt.CreateBlock(category switch
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
                new CallbackProgress<DownloadProgress>(p => AppDispatcher.Current.Post(() =>
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

    partial void OnMaxMemoryGbChanged(int value)
    {
        OnPropertyChanged(nameof(GlobalMemoryGbSummary));
        PersistSettings();
    }

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
            BgArt = PixelArt.CreateBackground(IsDark);
        }

        PersistSettings();
    }

    private void ApplyTheme() => ThemeChanged?.Invoke(IsDark);

    [RelayCommand]
    private void OpenGameRoot() => OpenFolder(GameRoot);

    // ---- Java ----

    public ObservableCollection<JavaOptionModel> JavaOptions { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GlobalJavaSummary))]
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
        // ⚠️ 这里每一个 VM 属性赋值都会触发它自己的 PersistSettings。**必须全部落在
        // _applyingSettings 守卫内**，否则 PersistSettings 会拿"还没读回来的默认值"把
        // settings 里的真值覆盖掉。
        //
        // 曾经的排布是把 CustomBackgroundPath / BgBlur / BgBrightness 放在 finally 之后：
        // 一有自定义背景图，CustomBackgroundPath 的 setter 就先跑一次 PersistSettings，
        // 那一刻 BgBlur/BgBrightness 还是默认 0 ⇒ 把 settings 的亮度/模糊清零，
        // 紧接着的两行"读回"读到的自然也是 0。症状 = 调好的亮度/模糊每次启动被重置。
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

            if (!string.IsNullOrEmpty(settings.CustomBackgroundPath) &&
                File.Exists(settings.CustomBackgroundPath))
            {
                CustomBackgroundPath = settings.CustomBackgroundPath;
            }

            // 视频排在图片后面读：两个 setter 的互斥是"后写的赢"，这样设置里万一两个都有，
            // 结果稳定地按视频算（见 LauncherSettings.VideoBackgroundPath）。
            if (!string.IsNullOrEmpty(settings.VideoBackgroundPath) &&
                File.Exists(settings.VideoBackgroundPath))
            {
                VideoBackgroundPath = settings.VideoBackgroundPath;
            }

            BgBlur = settings.BackgroundBlur;
            BgBrightness = settings.BackgroundBrightness;
        }
        finally
        {
            _applyingSettings = false;
        }

        ApplyTheme();
        OnPropertyChanged(nameof(SourceHint));
        JavaLabel = settings.JavaPath is null ? "自动" : Path.GetFileName(settings.JavaPath);
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
        settings.VideoBackgroundPath = VideoBackgroundPath;
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

    /// <summary>
    /// 视频后端出问题时由视图回调进来。<b>VM 里没有播放器</b>（<c>MediaPlayer</c> 是 WPF 类型，
    /// 本工程被 Avalonia 共用），所以"能不能播"只有视图知道。
    /// 这里顺手把视频清掉，让背景回落到生成图 —— 否则会留一块一直不动的空背景。
    /// </summary>
    public void ReportVideoBackgroundFailure(string message)
    {
        VideoBackgroundPath = null;
        ShowBanner(message);
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
