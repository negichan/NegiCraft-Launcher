using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
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
                    });
                    return "OK";
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
                case "state":
                    return await Ui(State);
                case "shot":
                    return await Ui(() => Shot(arg));
                case "quit":
                    await Ui(() => _window.Close());
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

    private string State()
    {
        var sb = new StringBuilder();
        sb.Append($"page={_vm.CurrentPage} tab={_vm.CurrentSettingsTab} ");
        sb.Append($"pops=[acc={_vm.IsAccPopOpen} inst={_vm.IsInstPopOpen} dl={_vm.IsDlPopOpen} bg={_vm.IsBgPopOpen}] ");
        sb.Append($"threads={_vm.DownloadThreads} ");
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
