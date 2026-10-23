namespace NegiCraftLauncher.ViewModels;

/// <summary>
/// 页面切换的方向。两端（WPF / Avalonia）共用这一份判据，视图层只负责照着画。
///
/// <para><b>为什么方向按侧栏的竖直顺序排</b>：侧栏是竖排的（首页 / 实例 / 下载
/// 在中间，设置在底部），所以"往上走还是往下走"是用户对导航最直接的直觉。
/// 反过来，横向位移在这么宽的内容区里看着像"整块搬家"，方向感反而弱。</para>
///
/// <para><b>为什么只有进场、没有出场</b>：页面是<b>兄弟节点叠在同一个容器里</b>
/// （WPF 的 <c>MainContent</c>、Avalonia 的同一层），靠可见性切换，不是导航栈。
/// 旧页会立即消失，没有"旧页退场"可做；能做的只有"新页从哪个方向进来"。</para>
///
/// <para><b>为什么首页不位移</b>：首页那块 3D 皮肤模型是立体的，位移会让它跟着飘，
/// 看着像没对齐；而且首页是启动器的主脸、最常被看到，位移最容易变成"晃"。
/// 所以首页只淡入。</para>
/// </summary>
public enum PageDirection
{
    /// <summary>不动（淡入即可）。首页用它。</summary>
    None = 0,

    /// <summary>从上方滑入：正在往侧栏的上方走。</summary>
    FromAbove = 1,

    /// <summary>从下方滑入：正在往侧栏的下方走。</summary>
    FromBelow = -1,
}

/// <summary>
/// 页面在侧栏里的竖直次序，以及"从哪来、往哪去"的判定。
///
/// <para>放在共享层（ViewModels）是因为两端都要用同一份判据 —— 同一个页面切换，
/// WPF 与 Avalonia 必须给出<b>同一个方向</b>，否则两端的观感会分叉。</para>
/// </summary>
public static class PageOrder
{
    /// <summary>
    /// 侧栏从上到下的次序。数值大的在下面。
    ///
    /// <para><b>为什么设置排在最下面</b>：侧栏上设置那颗按钮在底部（见两端的
    /// <c>MainWindow</c>），下载在中间那组的最后一项。</para>
    ///
    /// <para><b>为什么实例配置页紧跟实例页</b>：它是从实例页点进去的下一层
    /// （见 <c>MainWindowViewModel.OpenInstanceConfig</c>），不属于侧栏导航，
    /// 但方向上"往下走一层"最自然。</para>
    /// </summary>
    private static int Rank(string page) => page switch
    {
        "home" => 0,
        "instances" => 1,
        "instance_manage" => 2,
        "download" => 3,
        "settings" => 4,
        _ => -1,
    };

    /// <summary>
    /// 从 <paramref name="from"/> 走到 <paramref name="to"/> 该用哪个方向。
    /// </summary>
    /// <param name="from">上一页。<c>null</c> 或未知页面当作"没有来路"。</param>
    /// <param name="to">要去的那一页。</param>
    /// <returns>
    /// <see cref="PageDirection.None"/> 表示只淡入不位移 —— 首页用它，
    /// 以及"来路不明"（冷启动直接落在某页、或从没有的页面跳过来）。
    /// </returns>
    public static PageDirection Between(string? from, string to)
    {
        // 首页只淡入：3D 模型会跟着飘，主脸不该晃。
        if (to == "home") return PageDirection.None;

        var toRank = Rank(to);
        if (toRank < 0) return PageDirection.None;

        var fromRank = from is null ? -1 : Rank(from);
        if (fromRank < 0) return PageDirection.None;

        // 序号没变 = 还在同一页（比如点当前页那颗导航），不动。
        if (toRank == fromRank) return PageDirection.None;

        // 序号变大 = 往侧栏下方走 ⇒ 新页从上方滑进来。
        return toRank > fromRank ? PageDirection.FromAbove : PageDirection.FromBelow;
    }
}
