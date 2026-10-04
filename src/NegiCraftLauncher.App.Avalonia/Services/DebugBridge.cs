using System;
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
                        _vm.IsBgPopOpen = arg == "bg";
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
        sb.Append($"pops=[acc={_vm.IsAccPopOpen} inst={_vm.IsInstPopOpen} dl={_vm.IsDlPopOpen} bg={_vm.IsBgPopOpen}] ");
        sb.Append($"threads={_vm.DownloadThreads} ");

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
    private string Shot(string path)
    {
        if (_window.Content is not Visual content) return "ERR no content";

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
