using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.VisualTree;
using NegiCraftLauncher.ViewModels;
using NegiCraftLauncher.Raster;
using NegiCraftLauncher.App.Avalonia.Views;
using NegiCraftLauncher.Pet.Avalonia.Debug;
using NegiCraftLauncher.Skin.Avalonia.Controls;

namespace NegiCraftLauncher.App.Avalonia.Services;

/// <summary>
/// A local control channel for automated verification: set page/tab/popover state and render an
/// offscreen screenshot without ever touching the mouse, keyboard focus, or window z-order.
/// Only opened when the app is launched with --debug, so normal runs expose nothing.
///
/// The pet verbs are not handled here — they are shared with the standalone pet's bridge and live
/// in <see cref="PetDebugCommands"/>. This class owns the launcher-only verbs (pages, accounts,
/// downloads, tray) and the transport.
/// </summary>
public sealed class DebugBridge
{
    /// <summary>Mailbox directory under <c>%TEMP%</c>. The standalone pet uses its own (ncl-pet-debug).</summary>
    public const string DefaultBoxName = "ncl-debug";

    private readonly Window _window;
    private readonly MainWindowViewModel _vm;
    private PetDebugMailbox? _mailbox;

    private DebugBridge(Window window, MainWindowViewModel vm)
    {
        _window = window;
        _vm = vm;
    }

    public static void StartIfNeeded(Window window)
    {
        if (!PetDebugMailbox.IsEnabled) return;
        if (window.DataContext is not MainWindowViewModel vm) return;

        var bridge = new DebugBridge(window, vm);
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
            // Pet verbs are shared with the standalone pet's bridge. The pet window can be closed,
            // in which case the shared handler answers "ERR no pet window" — as it always did.
            var pet = (_window as MainWindow)?.PetWindowInstance;
            if (PetDebugCommands.TryHandle(pet, _vm.IsPetActive, verb, arg, _window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero) is { } shared)
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
                        // `pop bg` 现在开的是**独立那扇**调节窗（弹层整个搬走了）。
                        // 动词名留着不改，是为了 design/ 里那批抓图脚本不用跟着改。
                        _vm.IsBgTuningOpen = arg == "bg";
                        _vm.IsLoaderPopOpen = arg == "loader";
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
                // 实例页的过滤/排序和配置弹窗的 tab —— 这些都是**只能靠看图判**的布局，
                // 而脚本不能去点鼠标，所以给它们各留一个能拨的入口。
                // 语法：`inst-filter search:zzz` / `sort:名称` / `loader:Fabric` / `tab:Java`。
                case "inst-filter":
                    await PetDebugMailbox.Ui(() =>
                    {
                        var head = arg.Split(':', 2);
                        var value = head.Length > 1 ? head[1] : "";
                        switch (head[0])
                        {
                            case "search":
                                _vm.InstanceSearch = value;
                                break;
                            case "sort":
                                _vm.InstanceSort = string.IsNullOrEmpty(value) ? "最近游玩" : value;
                                break;
                            case "loader":
                                _vm.InstanceLoader = string.IsNullOrEmpty(value) ? LoaderOptionModel.AllName : value;
                                break;
                            case "tab":
                                _vm.ConfigTab = string.IsNullOrEmpty(value) ? "概览" : value;
                                break;
                        }
                    });
                    return "OK";
                // 关闭策略的三条出口都得能被脚本走一遍：真去点 ✕ 会抢焦点，而"退出"那条会把进程
                // 一起带走，所以这里直接拨 VM —— behavior 改设置、request 走真策略、answer 走真命令。
                case "close":
                    await PetDebugMailbox.Ui(() =>
                    {
                        if (arg.StartsWith("behavior:", StringComparison.Ordinal))
                        {
                            _vm.CloseWindowBehavior = arg["behavior:".Length..];
                        }
                        else if (arg.StartsWith("answer:", StringComparison.Ordinal))
                        {
                            _vm.AnswerCloseCommand.Execute(arg["answer:".Length..]);
                        }
                        else if (arg == "remember:on")
                        {
                            _vm.ClosePromptRemember = true;
                        }
                        else if (arg == "remember:off")
                        {
                            _vm.ClosePromptRemember = false;
                        }
                        else if (arg == "request")
                        {
                            _vm.RequestClose();
                        }
                        else if (arg == "prompt")
                        {
                            _vm.IsClosePromptOpen = true;
                        }
                    });
                    return $"OK behavior={_vm.CloseWindowBehavior} prompt={_vm.IsClosePromptOpen} remember={_vm.ClosePromptRemember}";
                case "bgimage":
                    // 自选图片壁纸：`bgimage <绝对路径>` 挂上，`bgimage none` 卸掉。
                    // 与 WPF 侧同名动词同一套语义，这样"两端同色"才量得出来（走真命令，含校验）。
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
                    // 与 WPF 侧同名动词配对：跨端要比的是"解码+调色出来的字节"，
                    // 而截图里还叠着各自合成器的重采样，量不到这一步。
                    if (_vm.BgCustomArt is not { } art) return "no bg art";
                    File.WriteAllBytes(arg, PngCodec.Encode(art));
                    return $"OK {art.Width}x{art.Height}";
                case "bgframe":
                    // 取景：`bgframe <panX> <panY> <zoom>`。写的是和滑块/拖动同一组 VM 属性。
                    await PetDebugMailbox.Ui(() =>
                    {
                        var nums = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nums.Length > 0) _vm.BgPanX = double.Parse(nums[0], CultureInfo.InvariantCulture);
                        if (nums.Length > 1) _vm.BgPanY = double.Parse(nums[1], CultureInfo.InvariantCulture);
                        if (nums.Length > 2) _vm.BgZoom = double.Parse(nums[2], CultureInfo.InvariantCulture);
                    });
                    return "OK";
                case "bggrade":
                    // 调色：`bggrade <contrast> <saturation> <hue>`。像素在后台算，发完等一下再抓图。
                    await PetDebugMailbox.Ui(() =>
                    {
                        var nums = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (nums.Length > 0) _vm.BgContrast = double.Parse(nums[0], CultureInfo.InvariantCulture);
                        if (nums.Length > 1) _vm.BgSaturation = double.Parse(nums[1], CultureInfo.InvariantCulture);
                        if (nums.Length > 2) _vm.BgHue = double.Parse(nums[2], CultureInfo.InvariantCulture);
                    });
                    return "OK";
                case "pet":
                    // Launcher-only: open/close the embedded pet window.
                    await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is MainWindow mw)
                        {
                            if (arg == "open") mw.OpenPetWindow();
                            else if (arg == "close") mw.ClosePetWindow();
                        }
                    });
                    return "OK";
                case "threads":
                    await PetDebugMailbox.Ui(() => _vm.DownloadThreads = int.Parse(arg));
                    return "OK";
                case "home-skin":
                {
                    // 首页 3D 模型的开关与位置（与 WPF 侧同一条动词）：不搬鼠标也能把"拖出来的位置
                    // 对不对、记不记得住"验出来。drag 喂一对 DIP 增量，走左键拖动的同一条路。
                    if (_window is not MainWindow host) return "ERR not main window";
                    var words = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (words.Length == 0) return "ERR usage: home-skin on|off|reset|drag <dx> <dy>|state";

                    return await PetDebugMailbox.Ui(() =>
                    {
                        switch (words[0])
                        {
                            case "on": _vm.HomeSkinModelVisible = true; break;
                            case "off": _vm.HomeSkinModelVisible = false; break;
                            case "reset": _vm.ResetHomeSkinModelPositionCommand.Execute(null); break;
                            case "drag":
                                if (words.Length < 3) return "ERR usage: home-skin drag <dx> <dy>";
                                host.DragHomeSkinPreviewForDebug(
                                    double.Parse(words[1], CultureInfo.InvariantCulture),
                                    double.Parse(words[2], CultureInfo.InvariantCulture));
                                break;
                        }

                        static string Num(double? value) =>
                            value?.ToString("0.####", CultureInfo.InvariantCulture) ?? "null";

                        return $"OK visible={_vm.HomeSkinModelVisible} " +
                               $"frac=({Num(_vm.HomeSkinModelX)},{Num(_vm.HomeSkinModelY)}) " +
                               $"canvas=({Canvas.GetLeft(host.SkinPreview):0.#}," +
                               $"{Canvas.GetTop(host.SkinPreview):0.#}) page=" +
                               $"{host.PageHome.Bounds.Width:0}x{host.PageHome.Bounds.Height:0} " +
                               host.HomeSkinKeepOutForDebug;
                    });
                }
                case "demo":
                    // Sample rows so the task-row template (buttons, bar, states) can be checked
                    // without waiting on a real multi-hundred-MB download.
                    await PetDebugMailbox.Ui(() =>
                    {
                        _vm.Downloads.Clear();
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "安装 1.21.11" , Status = "下载中", Progress = 42, PercentText = "42%", Detail = "client.jar" });
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "下载 某模组", Status = "已暂停", Progress = 61, PercentText = "61%", Detail = "点「继续」接着下，已完成的文件会直接跳过。", IsFinished = true });
                        _vm.Downloads.Add(new DownloadTaskModel { Name = "安装 1.20.1", Status = "已完成", Progress = 100, PercentText = "100%", IsFinished = true });
                        _vm.RefreshHasDownloads();
                    });
                    return "OK";
                case "skin":
                    // Force an arbitrary skin PNG into the live 3D preview, to A/B wide vs slim.
                    return await PetDebugMailbox.Ui(() =>
                    {
                        if (_window.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault()
                            is not { } preview) return "ERR no SkinPreview";
                        preview.ApplySkin(File.ReadAllBytes(arg));
                        return "OK " + arg;
                    });
                case "state":
                    return await PetDebugMailbox.Ui(State);
                case "skinsnap":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        var preview = _window.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no preview";
                        preview.SaveSnapshot(arg);
                        return "OK " + arg;
                    });
                case "shot":
                    return await PetDebugMailbox.Ui(() => Shot(arg));
                case "bgtuningshot":
                    // 背景调节是主窗口**外面**的一扇窗，`shot` 抓不到它，所以单开一个动词。
                    return await PetDebugMailbox.Ui(() =>
                    {
                        if (_window is not Views.MainWindow host) return "ERR not main window";
                        if (host.BackgroundTuningWindowForDebug is not { } win) return "ERR tuning window closed";
                        // 报** DIP **位置，不是 win.Position 的原值：Avalonia 的 Position 带它自己那套
                        // 缩放单位（本机 96dpi 下仍报 1.5×，实测 GetWindowRect 1878 而 Position 2817），
                        // 直接打出来会和 WPF 侧差 1.5 倍，看着像摆错边。
                        var s = host.DesktopScaling;
                        return ShotWindow(win, arg)
                            + $" @({win.Position.X / s:0.#},{win.Position.Y / s:0.#}) side={(win.Position.X / s < host.Position.X / s + host.Bounds.Width / 2 ? "left" : "right")}";
                    });
                case "tray-right":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        var firstTray = TrayIcon.GetIcons(Application.Current!)?.FirstOrDefault();
                        var implField = typeof(TrayIcon).GetField("_impl", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        var impl = implField?.GetValue(firstTray);
                        if (impl == null) return "ERR no impl";
                        var method = impl.GetType().GetMethod("OnRightClicked", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
                        if (method == null) return "ERR no OnRightClicked method";
                        method.Invoke(impl, null);

                        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                        var trayWin = desktop?.Windows.FirstOrDefault(w => w.GetType().Name.Contains("TrayPopupRoot"));
                        if (trayWin == null) return "OK (no window found)";

                        if (!string.IsNullOrWhiteSpace(arg))
                        {
                            var size = trayWin.Bounds.Size;
                            if (size.Width <= 0 || size.Height <= 0) size = new Size(180, 140);
                            using var rtb = new RenderTargetBitmap(
                                new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
                                new Vector(96, 96));
                            rtb.Render(trayWin);
                            rtb.Save(arg, new PngBitmapEncoderOptions());
                        }
                        return $"OK bounds={trayWin.Bounds.Width}x{trayWin.Bounds.Height} descendants={trayWin.GetVisualDescendants().Count()}";
                    });
                case "shot-tray":
                    return await PetDebugMailbox.Ui(() =>
                    {
                        var desktop = Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;
                        var trayWin = desktop?.Windows.FirstOrDefault(w => w.GetType().Name.Contains("TrayPopupRoot"));
                        if (trayWin == null) return "ERR no tray window found";
                        var size = trayWin.Bounds.Size;
                        if (size.Width <= 0 || size.Height <= 0) size = new Size(180, 140);
                        using var rtb = new RenderTargetBitmap(
                            new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
                            new Vector(96, 96));
                        rtb.Render(trayWin);
                        rtb.Save(arg, new PngBitmapEncoderOptions());
                        return $"OK {arg} w={trayWin.Bounds.Width} h={trayWin.Bounds.Height}";
                    });
                case "quit":
                    // Exit only once the reply is on disk, so the client sees OK rather than timing out.
                    _mailbox?.ExitAfterReply(() =>
                    {
                        if (_window is MainWindow mw)
                        {
                            mw.ExitApplication();
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
        // chip 画出来的几何：两端读同一个数，才谈得上"一致"（截图里卡片半透，壁纸在漏，量不出来）。
        if (_window is Views.MainWindow mw) sb.Append($"bgchip=[{mw.CoverageDebug}] ");
        sb.Append($"bgimage={_vm.CustomBackgroundPath ?? "n/a"} ");

        var preview = _window.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
        sb.Append($"player={preview?.PlayerName ?? "n/a"} user={preview?.CurrentLoadedUser ?? "n/a"} ");
        sb.Append($"skintype={preview?.LiveSkinType?.ToString() ?? "n/a"} top={preview?.LiveTopLayer?.ToString() ?? "n/a"} stats=[{preview?.RenderStats ?? "n/a"}] ");

        sb.Append($"downloads={_vm.Downloads.Count}");
        foreach (var t in _vm.Downloads)
        {
            sb.Append($" | {t.Name} [{t.Status} {t.PercentText} run={t.IsRunning} resume={t.CanResume}]");
        }

        return sb.ToString();
    }

    /// <summary>Renders the live visual tree offscreen; no window visibility or focus required.</summary>
    private string Shot(string path) => ShotWindow(_window, path);

    /// <summary>
    /// Render any window's content to a PNG. 背景调节搬出主窗口之后它是**另一扇窗**，
    /// 抓主窗口看不见它，所以抓图要能指名道姓（与 WPF 侧同名同语义）。
    /// </summary>
    private static string ShotWindow(Window window, string path)
    {
        if (window.Content is not Visual content) return "ERR no content";

        // Render 1:1 in the visual's own DIP units; pre-scaling here made the offscreen pass
        // re-arrange the tree at a different available size than the live window.
        var size = content.Bounds.Size;
        using var rtb = new RenderTargetBitmap(
            new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
            new Vector(96, 96));
        rtb.Render(content);
        rtb.Save(path, new PngBitmapEncoderOptions());
        return $"OK {path}";
    }
}
