using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using NegiCraftLauncher.Core.WallpaperEngine;
using NegiCraftLauncher.Pet.Debug;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// 自动化验证用的本地控制通道：设置页面 / 子页 / 弹窗状态并离屏出图，
/// 全程不动鼠标、不抢键盘焦点、不改窗口层级。只在 <c>--debug</c> 启动时打开。
///
/// <para><b>本文件是 <c>App/Services/DebugBridge.cs</c>（267 行）的 WPF 移植版。</b>
/// 邮箱名（<c>ncl-debug</c>）、动词名、回复格式逐条对齐，
/// 客户端 <c>design/_dbg.ps1</c> 与 <c>design/_smoke.ps1</c> 两个平台通用
/// （它们是本地私有脚本，不随源码分发）。</para>
///
/// <para>桌宠动词不在这里 —— 它们与独立桌宠的桥共用，实现在
/// <see cref="PetDebugCommands"/>。本类只管启动器专属动词（页面、账户、下载、托盘）与传输层。</para>
///
/// <para><b>与 Avalonia 版的差别</b>：两个托盘动词<b>行为已对齐</b>，只是内部机制不同 ——
/// Avalonia 的托盘弹窗是 <c>TrayPopupRoot</c>，得靠反射调 <c>TrayIcon._impl.OnRightClicked</c>
/// 才会出现；WPF 侧托盘菜单是自己弹的 WPF <c>ContextMenu</c>（不是 WinForms
/// <c>ContextMenuStrip</c>，那个画的是系统原生外观、跟深色主题对不上），
/// 直接 <c>ShowTrayMenu()</c> 即可，两边都能用 <c>RenderTargetBitmap</c> 抓图。</para>
///
/// <para><b>只有 WPF 侧有的动词</b>：<c>bgvideo</c>、<c>bgsound</c>、<c>bgvol</c>、<c>wallpaper-info</c> ——
/// 视频背景是 Windows 专属功能（Avalonia 侧没有视频面），Avalonia 的桥里刻意不加，
/// 免得留一个永远用不上的分支。</para>
/// </summary>
public sealed class DebugBridge
{
    /// <summary><c>%TEMP%</c> 下的邮箱目录名。独立桌宠用自己的（<c>ncl-pet-debug</c>）。</summary>
    public const string DefaultBoxName = "ncl-debug";

    private readonly Window _window;
    private readonly MainWindowViewModel _vm;

    /// <summary>
    /// 宿主顶层窗口的 HWND，<b>在 UI 线程上一次性取好</b>。
    ///
    /// <para>不能在 <see cref="Dispatch"/> 里现取：<c>WindowInteropHelper.Handle</c> 会碰
    /// <c>Window</c>（一个 <c>DispatcherObject</c>），而 <c>Dispatch</c> 跑在轮询线程上 ——
    /// 结果是每个动词都回 <c>ERR 调用线程无法访问此对象</c>。
    /// 这也解释了为什么这个值必须在 <c>Show()</c> 之后、在 UI 线程上算。</para>
    /// </summary>
    private readonly IntPtr _mainHwnd;

    private PetDebugMailbox? _mailbox;

    private DebugBridge(Window window, MainWindowViewModel vm, IntPtr mainHwnd)
    {
        _window = window;
        _vm = vm;
        _mainHwnd = mainHwnd;
    }

    public static void StartIfNeeded(Window window)
    {
        if (!PetDebugMailbox.IsEnabled) return;
        if (window.DataContext is not MainWindowViewModel vm) return;

        // 这里就在 UI 线程上（App.OnStartup → Show 之后），HWND 一定已经建好。
        var bridge = new DebugBridge(window, vm, new WindowInteropHelper(window).Handle);
        bridge._mailbox = new PetDebugMailbox(
            PetDebugMailbox.ResolveBoxName(DefaultBoxName), "ncl-debug", bridge.Dispatch);
        bridge._mailbox.Start();
    }

    private async Task<string> Dispatch(string line)
    {
        var parts = line.Split(' ', 2);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";

        try
        {
            // 桌宠动词与独立桌宠的桥共用。桌宠窗口可以关着，那种情况下共用的处理分支
            // 会回 "ERR no pet window" —— 与一直以来的行为一致。
            var pet = (_window as MainWindow)?.PetWindowInstance;
            if (PetDebugCommands.TryHandle(pet, _vm.IsPetActive, verb, arg, _mainHwnd) is { } shared)
            {
                return await shared;
            }

            switch (verb)
            {
                case "page":
                    await PetDebugMailbox.Ui(() => _vm.CurrentPage = arg);
                    return "OK";
                case "account":
                    await PetDebugMailbox.Ui(() =>
                    {
                        var target = _vm.Accounts.FirstOrDefault(a => a.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            _vm.SelectAccountCommand.Execute(target);
                        }
                    });
                    return "OK";
                case "tab":
                    await PetDebugMailbox.Ui(() => _vm.CurrentSettingsTab = arg);
                    return "OK";
                case "pop":
                    await PetDebugMailbox.Ui(() =>
                    {
                        _vm.IsAccPopOpen = arg == "acc";
                        _vm.IsInstPopOpen = arg == "inst";
                        _vm.IsDlPopOpen = arg == "dl";
                        // `pop vol` 开音量浮层：它平时是**悬停即出**的，脚本没法在不抢鼠标的前提下
                        // 让它亮着，而"对不对得齐喇叭"恰恰只能靠看图判。
                        _vm.IsVolumePopOpen = arg == "vol";
                        // `pop bg` 现在开的是**独立那扇**调节窗（弹层整个搬走了）。
                        // 动词名留着不改，是为了 design/ 里那批抓图脚本不用跟着改。
                        _vm.IsBgTuningOpen = arg == "bg";
                        if (arg == "cfg" && _vm.Instances.FirstOrDefault() is { } inst)
                        {
                            _vm.OpenInstanceConfigCommand.Execute(inst);
                        }
                        else if (arg != "cfg")
                        {
                            _vm.IsInstConfigOpen = false;
                        }
                    });
                    return "OK";
                case "cfg-inst":
                    await PetDebugMailbox.Ui(() =>
                    {
                        var target = _vm.Instances.FirstOrDefault();
                        if (target != null)
                        {
                            _vm.OpenInstanceConfigCommand.Execute(target);
                            if (arg.Contains("all"))
                            {
                                _vm.ConfigInstCustomJava = true;
                                _vm.ConfigInstCustomMemory = true;
                            }
                        }
                    });
                    return "OK";
                case "pet":
                    // 启动器专属：开关进程内托管的桌宠窗口。
                    await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is MainWindow host)
                        {
                            if (arg == "open") host.OpenPetWindow();
                            else if (arg == "close") host.ClosePetWindow();
                        }
                    });
                    return "OK";
                case "threads":
                    await PetDebugMailbox.Ui(() => _vm.DownloadThreads = int.Parse(arg));
                    return "OK";
                case "bg":
                    // 背景亮度/模糊：`bg <brightness> [blur]`。用来验这两个值是否真的落盘 ——
                    // 走的是和滑杆完全相同的 VM 属性，所以能复现"调了但没保存"。
                    await PetDebugMailbox.Ui(() =>
                    {
                        var nums = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nums.Length > 0) _vm.BgBrightness = double.Parse(nums[0], CultureInfo.InvariantCulture);
                        if (nums.Length > 1) _vm.BgBlur = double.Parse(nums[1], CultureInfo.InvariantCulture);
                    });
                    return "OK";
                case "bgframe":
                    // 取景：`bgframe <panX> <panY> <zoom>`。走的是和滑块/拖动完全相同的 VM 属性，
                    // 所以一条命令同时验了"参数 → 变换"和"参数 → 落盘"。
                    await PetDebugMailbox.Ui(() =>
                    {
                        var nums = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nums.Length > 0) _vm.BgPanX = double.Parse(nums[0], CultureInfo.InvariantCulture);
                        if (nums.Length > 1) _vm.BgPanY = double.Parse(nums[1], CultureInfo.InvariantCulture);
                        if (nums.Length > 2) _vm.BgZoom = double.Parse(nums[2], CultureInfo.InvariantCulture);
                    });
                    return "OK";
                case "bggrade":
                    // 调色：`bggrade <contrast> <saturation> <hue>`。同上走真属性；
                    // 算像素在后台线程，所以发完命令要等一下再抓图（结果看 state 的 bgart）。
                    await PetDebugMailbox.Ui(() =>
                    {
                        var nums = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nums.Length > 0) _vm.BgContrast = double.Parse(nums[0], CultureInfo.InvariantCulture);
                        if (nums.Length > 1) _vm.BgSaturation = double.Parse(nums[1], CultureInfo.InvariantCulture);
                        if (nums.Length > 2) _vm.BgHue = double.Parse(nums[2], CultureInfo.InvariantCulture);
                    });
                    return "OK";
                case "bgimage":
                    // 自选图片壁纸：`bgimage <绝对路径>` 挂上，`bgimage none` 卸掉。
                    // 走的是和「选择图片」完全相同的那条命令（含 IsReadableImage 校验、会跳首页开弹层），
                    // 但不弹文件选择框 —— 那个会抢焦点，本项目的验收一律不碰鼠标。
                    await PetDebugMailbox.Ui(() =>
                    {
                        if (arg.Length == 0 || arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            _vm.CustomBackgroundPath = null;
                        }
                        else
                        {
                            _vm.SetBackgroundCommand.Execute(arg);
                        }
                    });
                    return "OK";
                case "bgsnap":
                    // 把自选壁纸**当前上屏的那份像素**原样写成 PNG，不经过任何视图。
                    // 跨端比对要的是"两端解码+调色出来是不是同一份字节"，而截图里还叠着
                    // 合成器的重采样（两端滤波不同），量不到这一步 —— 所以从 buffer 直接落盘。
                    if (_vm.BgCustomArt is not { } art) return "no bg art";
                    File.WriteAllBytes(arg, PngCodec.Encode(art));
                    return $"OK {art.Width}x{art.Height}";
                case "bgvideo":
                    // 视频背景：`bgvideo <绝对路径>` 挂上，`bgvideo none` 卸掉。
                    // 走的是和「选择视频」按钮完全相同的 VM 命令 —— 所以能验到真实那条路，
                    // 又不必去点文件选择框（那个会弹模态窗口、抢焦点）。
                    await PetDebugMailbox.Ui(() =>
                    {
                        if (arg.Length == 0 || arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            _vm.VideoBackgroundPath = null;
                        }
                        else
                        {
                            _vm.SetVideoBackgroundCommand.Execute(arg);
                        }
                    });
                    return "OK";
                case "bgsound":
                    // 视频壁纸的声音开关：`bgsound on|off`。走的是和界面复选框相同的 VM 属性，
                    // 所以既验了持久化，也验了视图那条 PropertyChanged 分支有没有把值落到播放器上
                    // （结果看 `state` 里的 video=[muted=… volume=…]）。
                    await PetDebugMailbox.Ui(() =>
                        _vm.VideoBackgroundSound = arg.Equals("on", StringComparison.OrdinalIgnoreCase));
                    return "OK";
                case "bgvol":
                    // 视频壁纸音量：`bgvol 0..100`。同样走 VM 属性，结果看 `state` 里的 video=[… want=…]。
                    await PetDebugMailbox.Ui(() =>
                        _vm.VideoBackgroundVolume = int.TryParse(arg, out var v) ? v : 100);
                    return "OK";
                case "wallpaper-info":
                    // Wallpaper Engine 探测结果。纯静态调用、不碰 VM，所以不用回 UI 线程。
                    return await Task.Run(() =>
                    {
                        var install = WallpaperEngineLocator.FindInstallDirectory(refresh: true);
                        var current = WallpaperEngineLocator.GetCurrent(refresh: true);
                        if (current is null) return $"install={install ?? "n/a"} current=none";

                        return $"install={install ?? "n/a"} kind={current.Kind} title={current.Title ?? "n/a"} " +
                               $"video={current.VideoPath ?? "n/a"} source={current.SourcePath}";
                    });
                case "wallpaper-sync":
                    // 跑一遍「同步 Wallpaper Engine」，把结果横幅原文回出来 ——
                    // 这条路上真正会变的就两样：背景换成了什么、提示说了什么。
                    return await PetDebugMailbox.Ui(() =>
                    {
                        _vm.SyncWallpaperEngineCommand.Execute(null);
                        return $"banner={_vm.BannerText} bgimage={_vm.CustomBackgroundPath ?? "n/a"} " +
                               $"bgvideo={_vm.VideoBackgroundPath ?? "n/a"}";
                    });
                case "demo":
                    // 示例任务行，用来检查任务行模板（按钮、进度条、状态）而不必真的等下几百 MB。
                    await PetDebugMailbox.Ui(() =>
                    {
                        _vm.Downloads.Clear();
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "安装 1.21.11", Status = "下载中", Progress = 42, PercentText = "42%", Detail = "client.jar" });
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "下载 某模组", Status = "已暂停", Progress = 61, PercentText = "61%", Detail = "点「继续」接着下，已完成的文件会直接跳过。", IsFinished = true });
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "安装 1.20.1", Status = "已完成", Progress = 100, PercentText = "100%", IsFinished = true });
                        _vm.RefreshHasDownloads();
                    });
                    return "OK";
                case "skin":
                    // 把任意皮肤 PNG 塞进主页的实时预览，用来 A/B 宽臂与细臂。
                    return await PetDebugMailbox.Ui(() =>
                    {
                        var preview = (_window as MainWindow)?.SkinPreview;
                        if (preview is null) return "ERR no SkinPreview";
                        preview.ApplySkin(File.ReadAllBytes(arg));
                        return "OK " + arg;
                    });
                case "state":
                    return await PetDebugMailbox.Ui(State);
                case "skinsnap":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        var preview = (_window as MainWindow)?.SkinPreview;
                        if (preview is null) return "ERR no preview";
                        preview.SaveSnapshot(arg);
                        return "OK " + arg;
                    });
                case "shot":
                    return await PetDebugMailbox.Ui(() => Shot(arg));
                case "bgtuningshot":
                    // 背景调节是主窗口**外面**的一扇窗，`shot` 抓不到它，所以单开一个动词。
                    return await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is not MainWindow host) return "ERR not main window";
                        if (host.BackgroundTuningWindowForDebug is not { } win) return "ERR tuning window closed";
                        // 顺带报位置：贴边摆位对不对，光看内容渲染是看不出来的。
                        return ShotWindow(win, arg) + $" @({win.Left:0.#},{win.Top:0.#}) side={(win.Left < host.Left + host.Width / 2 ? "left" : "right")}";
                    });
                case "tray-right":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is not MainWindow host) return "ERR not main window";
                        return host.ShowTrayMenuForDebug()
                            ? "OK"
                            : "ERR no tray menu";
                    });
                case "shot-tray":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is not MainWindow host) return "ERR not main window";
                        return host.ShotTrayMenuForDebug(arg);
                    });
                case "quit":
                    // 回复落盘之后再退出，客户端才能看到 OK 而不是超时。
                    _mailbox?.ExitAfterReply(() =>
                    {
                        if (_window is MainWindow host)
                        {
                            host.ExitApplication();
                        }
                        else
                        {
                            _window.Close();
                        }
                    });
                    return "OK";
                default:
                    return $"ERR unknown verb {verb}";
            }
        }
        catch (Exception ex)
        {
            return $"ERR {ex.Message}";
        }
    }

    private string State()
    {
        var sb = new StringBuilder();
        sb.Append($"page={_vm.CurrentPage} tab={_vm.CurrentSettingsTab} petActive={_vm.IsPetActive} ");
        sb.Append($"pops=[acc={_vm.IsAccPopOpen} inst={_vm.IsInstPopOpen} dl={_vm.IsDlPopOpen} bg={_vm.IsBgTuningOpen}] ");
        sb.Append($"threads={_vm.DownloadThreads} ");
        sb.Append($"bg=[brightness={_vm.BgBrightness} blur={_vm.BgBlur}] ");
        sb.Append($"bgframe=[pan={_vm.BgPanX:0.#},{_vm.BgPanY:0.#} zoom={_vm.BgZoom:0.#}] ");
        sb.Append($"bggrade=[con={_vm.BgContrast:0.#} sat={_vm.BgSaturation:0.#} hue={_vm.BgHue:0.#}] ");
        // 自选壁纸真正上屏的那份像素 + 为什么没上屏（解码失败是静默回落的，这里是第一现场）。
        sb.Append($"bgart={(_vm.BgCustomArt is { } art ? $"{art.Width}x{art.Height}" : "n/a")} ");
        sb.Append($"bgwhy=[{_vm.BackgroundDecodeState}] ");
        // 取景是按元素自己的尺寸算余量的，所以这条链上每一层的尺寸都得能在验收时看到
        // （差 20px 宽就是 5px 的平移量，光看截图量不出来是谁在溢出）。
        if (_window is MainWindow mw)
        {
            sb.Append($"bgel=[shell={mw.ShellSize} bgroot={mw.BgRootSize} layer={mw.BgLayerSize} img={mw.BgImageSize}] ");
            sb.Append($"bgchip=[{mw.CoverageDebug}] ");
        }
        sb.Append($"bgimage={_vm.CustomBackgroundPath ?? "n/a"} ");
        sb.Append($"bgvideo={_vm.VideoBackgroundPath ?? "n/a"} ");
        sb.Append($"bgsound={_vm.VideoBackgroundSound} ");
        sb.Append($"bgvol={_vm.VideoBackgroundVolume} ");
        sb.Append($"video=[{(_window as MainWindow)?.VideoBackgroundDebug ?? "n/a"}] ");
        sb.Append($"speaker=[{(_window as MainWindow)?.SpeakerDebug ?? "n/a"}] ");
        sb.Append($"tone=[{(_window as MainWindow)?.WindowToneDebug ?? "n/a"}] ");

        var preview = (_window as MainWindow)?.SkinPreview;
        sb.Append($"player={preview?.PlayerName ?? "n/a"} user={preview?.CurrentLoadedUser ?? "n/a"} ");
        sb.Append($"skintype={preview?.LiveSkinType.ToString() ?? "n/a"} top={preview?.LiveTopLayer.ToString() ?? "n/a"} stats=[{preview?.RenderStats ?? "n/a"}] ");

        sb.Append($"downloads={_vm.Downloads.Count}");
        foreach (var t in _vm.Downloads)
        {
            sb.Append($" | {t.Name} [{t.Status} {t.PercentText} run={t.IsRunning} resume={t.CanResume}]");
        }

        return sb.ToString();
    }

    /// <summary>离屏渲染实时可视树；不需要窗口可见、也不需要焦点。</summary>
    private string Shot(string path) => ShotWindow(_window, path);

    /// <summary>
    /// 把任意一扇 WPF 窗口的内容 1:1 渲染成 PNG。
    /// 背景调节搬出主窗口之后它是**另一扇窗**，抓主窗口看不见它，所以抓图要能指名道姓。
    /// </summary>
    private static string ShotWindow(Window window, string path)
    {
        if (window.Content is not FrameworkElement content) return "ERR no content";

        // 按可视元素自己的 DIP 尺寸 1:1 渲染；在这里预缩放会让离屏那趟用与实窗不同的
        // 可用尺寸重新排布整棵树。
        var size = content.RenderSize;
        if (size.Width <= 0 || size.Height <= 0) size = new Size(window.Width, window.Height);

        var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
            (int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height), 96, 96,
            System.Windows.Media.PixelFormats.Pbgra32);
        rtb.Render(content);

        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
        using (var stream = File.Create(path))
        {
            encoder.Save(stream);
        }

        return $"OK {path}";
    }
}
