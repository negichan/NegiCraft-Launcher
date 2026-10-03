using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using NegiCraftLauncher.App.Controls;
using NegiCraftLauncher.App.Models;
using NegiCraftLauncher.App.ViewModels;
using NegiCraftLauncher.App.Views;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// A local control channel for automated verification: set page/tab/popover state and render an
/// offscreen screenshot without ever touching the mouse, keyboard focus, or window z-order.
/// Only opened when the app is launched with --debug, so normal runs expose nothing.
/// </summary>
public sealed class DebugBridge
{
    private readonly Window _window;
    private readonly MainWindowViewModel _vm;

    private DebugBridge(Window window, MainWindowViewModel vm)
    {
        _window = window;
        _vm = vm;
    }

    public static void StartIfNeeded(Window window)
    {
        if (Array.IndexOf(Environment.GetCommandLineArgs(), "--debug") < 0) return;
        if (window.DataContext is not MainWindowViewModel vm) return;

        var bridge = new DebugBridge(window, vm);
        var loop = new Thread(bridge.RunLoop) { IsBackground = true, Name = "ncl-debug" };
        loop.Start();
    }

    private void RunLoop()
    {
        // A file mailbox instead of a named pipe: no persistent handles to leak or lose, and it
        // keeps working no matter what else on the machine intercepts or covers the UI.
        var dir = Path.Combine(Path.GetTempPath(), "ncl-debug");
        Directory.CreateDirectory(dir);
        var cmdPath = Path.Combine(dir, "cmd.txt");
        var replyPath = Path.Combine(dir, "reply.txt");

        while (true)
        {
            try
            {
                if (!File.Exists(cmdPath))
                {
                    Thread.Sleep(80);
                    continue;
                }

                var line = File.ReadAllText(cmdPath);
                File.Delete(cmdPath);

                var reply = Dispatch(line).GetAwaiter().GetResult();
                var tmp = replyPath + ".tmp";
                File.WriteAllText(tmp, reply);
                File.Move(tmp, replyPath, overwrite: true);
            }
            catch (Exception)
            {
                Thread.Sleep(80);   // e.g. client still mid-write; retry next tick
            }
        }
    }

    private async Task<string> Dispatch(string line)
    {
        var parts = line.Split(' ', 2);
        var verb = parts[0];
        var arg = parts.Length > 1 ? parts[1] : "";

        try
        {
            switch (verb)
            {
                case "page":
                    await Ui(() => _vm.CurrentPage = arg);
                    return "OK";
                case "account":
                    await Ui(() =>
                    {
                        var target = _vm.Accounts.FirstOrDefault(a => a.Name.Equals(arg, StringComparison.OrdinalIgnoreCase));
                        if (target != null)
                        {
                            _vm.SelectAccountCommand.Execute(target);
                        }
                    });
                    return "OK";
                case "tab":
                    await Ui(() => _vm.CurrentSettingsTab = arg);
                    return "OK";
                case "pop":
                    await Ui(() =>
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
                    await Ui(() =>
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
                    await Ui(() =>
                    {
                        if (_window is MainWindow mw)
                        {
                            if (arg == "open") mw.OpenPetWindow();
                            else if (arg == "close") mw.ClosePetWindow();
                        }
                    });
                    return "OK";
                case "pet-dangle":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        preview.IsDangling = arg == "on" || arg == "true" || arg == "1";
                        return "OK " + preview.IsDangling;
                    });
                case "pet-rotate":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        if (float.TryParse(arg, out var deg))
                        {
                            preview.RotateModel(deg);
                            return "OK " + deg;
                        }
                        return "ERR invalid deg";
                    });
                case "pet-sneak":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        preview.Sneaking = arg == "on" || arg == "true" || arg == "1";
                        return "OK " + preview.Sneaking;
                    });
                case "pet-control":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "on" || arg == "true" || arg == "1") pet.IsControlMode = true;
                        else if (arg == "off" || arg == "false" || arg == "0") pet.IsControlMode = false;
                        else if (arg == "toggle") pet.IsControlMode = !pet.IsControlMode;
                        return "OK " + pet.IsControlMode;
                    });
                case "pet-key":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length == 0) return "ERR missing key";
                        var keyName = parts[0];
                        bool down = parts.Length < 2 || parts[1] == "down" || parts[1] == "1" || parts[1] == "true";
                        pet.SetSimulatedKey(keyName, down);
                        return $"OK key={keyName} down={down}";
                    });
                case "pet-follow":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "on" || arg == "true" || arg == "1") pet.IsFollowMouseMode = true;
                        else if (arg == "off" || arg == "false" || arg == "0") pet.IsFollowMouseMode = false;
                        else if (arg == "toggle") pet.IsFollowMouseMode = !pet.IsFollowMouseMode;
                        return "OK " + pet.IsFollowMouseMode;
                    });
                case "pet-interact":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        // pet-interact [on|off|toggle] | pet-interact swallow [on|off|toggle]
                        var parts = arg.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && parts[0] == "swallow")
                        {
                            if (parts[1] == "on") pet.InteractSwallowClicks = true;
                            else if (parts[1] == "off") pet.InteractSwallowClicks = false;
                            else if (parts[1] == "toggle") pet.InteractSwallowClicks = !pet.InteractSwallowClicks;
                        }
                        else if (parts.Length >= 1)
                        {
                            if (parts[0] == "on") pet.IsInteractMode = true;
                            else if (parts[0] == "off") pet.IsInteractMode = false;
                            else if (parts[0] == "toggle") pet.IsInteractMode = !pet.IsInteractMode;
                        }
                        return $"OK interact={pet.IsInteractMode} swallow={pet.InteractSwallowClicks} hook={pet.IsInteractHookInstalled}";
                    });
                case "pet-attack":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        // A progress argument parks the swing there for photographing; bare plays it.
                        if (double.TryParse(arg, out double t))
                        {
                            preview.TriggerAttack(Math.Clamp(t, 0.0, 1.0));
                            return "OK parked " + t;
                        }
                        preview.TriggerAttack();
                        return "OK played";
                    });
                case "pet-coord":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var parts = arg.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy))
                        {
                            pet.SetNavigationTarget(cx, cy);
                            for (int i = 2; i + 1 < parts.Length; i += 2)
                            {
                                if (int.TryParse(parts[i], out int nx) && int.TryParse(parts[i + 1], out int ny))
                                {
                                    pet.AddNavigationTarget(nx, ny);
                                }
                            }
                            return $"OK waypoints={pet.RemainingWaypointCount}";
                        }
                        return "ERR invalid coords";
                    });
                case "pet-coord-add":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var parts = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int cx) && int.TryParse(parts[1], out int cy))
                        {
                            pet.AddNavigationTarget(cx, cy);
                            return $"OK waypoints={pet.RemainingWaypointCount}";
                        }
                        return "ERR invalid coords";
                    });
                case "pet-mode":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "free") pet.CurrentMode = PetWindow.PetInteractionMode.Free;
                        else if (arg == "control") pet.CurrentMode = PetWindow.PetInteractionMode.Control;
                        else if (arg == "follow") pet.CurrentMode = PetWindow.PetInteractionMode.FollowMouse;
                        return "OK mode=" + pet.CurrentMode;
                    });
                case "pet-menu":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "close") pet.PetContextMenu?.Close();
                        else pet.OpenPetContextMenu();
                        return "OK IsOpen=" + pet.PetContextMenu?.IsOpen;
                    });
                case "pet-walk":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        preview.IsWalking = arg == "on" || arg == "true" || arg == "1";
                        return "OK " + preview.IsWalking;
                    });
                case "pet-jump":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        preview.IsJumping = arg == "on" || arg == "true" || arg == "1";
                        return "OK " + preview.IsJumping;
                    });
                case "pet-jump-offset":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        if (double.TryParse(arg, out double off))
                        {
                            preview.SetJumpOffset(off);
                            return "OK " + off;
                        }
                        return "ERR invalid offset";
                    });
                case "pet-yaw":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        var preview = pet.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no pet preview";
                        if (float.TryParse(arg, out float deg))
                        {
                            preview.RotateModel(deg - preview.CurrentYawDeg);
                        }
                        return "OK " + preview.CurrentYawDeg;
                    });
                case "pet-track":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        return "OK " + pet.TrackDebugInfo;
                    });
                case "pet-mouse":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "reset" || arg == "clear")
                        {
                            pet.ClearVirtualCursor();
                            return "OK clear";
                        }
                        var parts = arg.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
                        if (parts.Length >= 2 && int.TryParse(parts[0], out int mx) && int.TryParse(parts[1], out int my))
                        {
                            pet.SetVirtualCursor(mx, my);
                            return $"OK mouse=({mx},{my})";
                        }
                        return "ERR invalid mouse coords";
                    });
                case "pet-name":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet == null) return "ERR no pet window";
                        if (arg == "reset" || string.IsNullOrWhiteSpace(arg))
                        {
                            _vm.PetCustomName = null;
                            pet.SetPlayerName(_vm.EffectivePetName);
                        }
                        else
                        {
                            _vm.PetCustomName = arg;
                            pet.SetPlayerName(arg);
                        }
                        return "OK " + _vm.EffectivePetName;
                    });
                case "threads":
                    await Ui(() => _vm.DownloadThreads = int.Parse(arg));
                    return "OK";
                case "demo":
                    // Sample rows so the task-row template (buttons, bar, states) can be checked
                    // without waiting on a real multi-hundred-MB download.
                    await Ui(() =>
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
                    return await Ui(() =>
                    {
                        if (_window.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault()
                            is not { } preview) return "ERR no SkinPreview";
                        preview.ApplySkin(File.ReadAllBytes(arg));
                        return "OK " + arg;
                    });
                case "state":
                    return await Ui(State);
                case "skinsnap":
                    return await Ui(() =>
                    {
                        var preview = _window.GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault();
                        if (preview == null) return "ERR no preview";
                        preview.SaveSnapshot(arg);
                        return "OK " + arg;
                    });
                case "pet-skinsnap":
                    return await PetSnapshot(arg);
                case "shot":
                    return await Ui(() => Shot(arg));
                case "shot-pet":
                    return await Ui(() =>
                    {
                        var pet = (_window as MainWindow)?.PetWindowInstance;
                        if (pet?.Content is Visual content)
                        {
                            var size = content.Bounds.Size;
                            if (size.Width <= 0 || size.Height <= 0) size = new Size(pet.Width, pet.Height);
                            using var rtb = new RenderTargetBitmap(
                                new PixelSize((int)Math.Ceiling(size.Width), (int)Math.Ceiling(size.Height)),
                                new Vector(96, 96));
                            rtb.Render(content);
                            rtb.Save(arg, new PngBitmapEncoderOptions());
                            return $"OK {arg}";
                        }
                        return "ERR no pet window";
                    });
                case "tray-right":
                    return await Ui(() =>
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
                    return await Ui(() =>
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
                    await Ui(() =>
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

    private static Task Ui(Action action) =>
        Dispatcher.UIThread.InvokeAsync(() => action()).GetTask();

    private static Task<T> Ui<T>(Func<T> func) =>
        Dispatcher.UIThread.InvokeAsync(func).GetTask();

    // shot-pet cannot see the pet: its offscreen render only ever draws the startup snapshot, so
    // every frame comes out identical. This reads the live GL frame instead. SaveSnapshot hands
    // back the frame captured by the *previous* request, hence arm once, wait, then save.
    private async Task<string> PetSnapshot(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "ERR no path";

        var preview = await Ui(() =>
            (_window as MainWindow)?.PetWindowInstance?
                .GetVisualDescendants().OfType<MinecraftSkinPreview>().FirstOrDefault());
        if (preview == null) return "ERR no pet preview";

        var scratch = Path.Combine(Path.GetTempPath(), "ncl-pet-snap-arm.png");
        await Ui(() => preview.SaveSnapshot(scratch));
        await Task.Delay(90);
        await Ui(() => preview.SaveSnapshot(path));
        try { File.Delete(scratch); } catch (IOException) { }

        return "OK " + path;
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
