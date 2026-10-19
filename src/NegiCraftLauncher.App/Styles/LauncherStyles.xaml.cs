using System.Windows;
using System.Windows.Controls;

namespace NegiCraftLauncher.App.Styles;

/// <summary>
/// <c>Styles/LauncherStyles.xaml</c> 的 code-behind —— 它存在只因为字典里有 4 个按钮挂了
/// <c>Click</c>：WPF 要求带事件处理器的 XAML 必须有 <c>x:Class</c>，没有就得把整段模板
/// 退回 <c>MainWindow.xaml</c>（那些模板加起来 200 多行）。
///
/// <para><b>取窗口走 <c>Window.GetWindow(sender)</c></b>，不缓存、不传引用：这几个按钮只出现在
/// 主窗口里，拿到的就是它；文件选择框的 owner 因此仍然是主窗口，和在
/// <c>MainWindow.xaml.cs</c> 里直接写 <c>this</c> 等价。</para>
///
/// <para>动作本身留在 MainWindow（<c>PickBackground</c> / <c>PickVideo</c> / <c>PickJava</c> /
/// <c>SyncWallpaperEngineFromTuning</c>），这里只做转发 —— 字典不该知道 VM 怎么调。</para>
/// </summary>
public partial class LauncherStyles : ResourceDictionary
{
    public LauncherStyles() => InitializeComponent();

    private void OnPickBackgroundClick(object sender, RoutedEventArgs e) => ShellOf(sender)?.PickBackground();

    private void OnPickVideoBackgroundClick(object sender, RoutedEventArgs e) => ShellOf(sender)?.PickVideo();

    private void OnSyncWallpaperEngineClick(object sender, RoutedEventArgs e) => ShellOf(sender)?.SyncWallpaperEngineFromTuning();

    private void OnPickJavaClick(object sender, RoutedEventArgs e) => ShellOf(sender)?.PickJava();

    /// <summary>按钮所在的窗口。模板被别处复用时可能拿不到主窗口，那就什么也不做。</summary>
    private static MainWindow? ShellOf(object sender) =>
        Window.GetWindow(sender as DependencyObject) as MainWindow;
}
