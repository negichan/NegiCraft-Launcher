using System;
using System.Threading.Tasks;
using Avalonia.Threading;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// 把共享层的 <see cref="IUiDispatcher"/> 接到 Avalonia 的 UI 线程上。
/// WPF 侧有对应的实现（用 <c>Application.Current.Dispatcher</c>）。
/// </summary>
public sealed class AvaloniaUiDispatcher : IUiDispatcher
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);

    public Task InvokeAsync(Action action) => Dispatcher.UIThread.InvokeAsync(action).GetTask();
}
