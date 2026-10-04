using System.Windows.Threading;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App.Services;

/// <summary>
/// 把共享层的 <see cref="IUiDispatcher"/> 接到 WPF 的 UI 线程上。
/// 与 Avalonia 侧的 <c>AvaloniaUiDispatcher</c> 一一对应。
///
/// <para>必须在构造 <c>MainWindowViewModel</c> <b>之前</b>装好 —— VM 初始化时会去拉皮肤、
/// 版本列表，那些回调要靠它回到 UI 线程。</para>
/// </summary>
public sealed class WpfUiDispatcher : IUiDispatcher
{
    private readonly Dispatcher _dispatcher;

    public WpfUiDispatcher(Dispatcher dispatcher) => _dispatcher = dispatcher;

    public void Post(Action action) => _dispatcher.BeginInvoke(action);

    public Task InvokeAsync(Action action) => _dispatcher.InvokeAsync(action).Task;
}
