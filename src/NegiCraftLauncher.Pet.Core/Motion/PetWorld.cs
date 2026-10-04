namespace NegiCraftLauncher.Pet;

/// <summary>
/// 一块可以站上去的**水平面**（DIP）。桌宠脚下最高的那块就是它的支撑面。
///
/// <para><b>只做单向平台</b>：从上方落上去会停住，从下方或侧面穿过去不受影响。
/// 这就是"站在别的窗口标题栏上"该有的手感，也省掉了窗口之间互相挤的复杂情况 ——
/// 桌宠本来就是浮在所有窗口之上的小玩意，不该被窗口的侧面挡住。</para>
/// </summary>
/// <param name="Left">左端（屏幕 DIP，含）。</param>
/// <param name="Right">右端（屏幕 DIP，含）。</param>
/// <param name="Top">台面高度（屏幕 DIP）。落上去时脚底贴在这一行。</param>
public readonly record struct PetSurface(double Left, double Right, double Top)
{
    /// <summary>这块面在水平方向上是否与 <paramref name="left"/>..<paramref name="right"/> 有交叠。</summary>
    public bool OverlapsHorizontally(double left, double right) => Left <= right && Right >= left;
}

/// <summary>
/// 一帧的"世界"。工作区提供**地板**（下边缘，也就是任务栏上沿）与**左右墙**；
/// <see cref="Surfaces"/> 是平台侧枚举出来的别的窗口的顶边。
///
/// <para>平台侧可以只给"可见且没最小化"的窗口 —— 枚举本身有成本，宿主按 ~150ms 节流重建一份就行，
/// 内核每帧只读。</para>
/// </summary>
/// <param name="WorkArea">屏幕工作区（DIP，不含任务栏）。</param>
/// <param name="Surfaces">可站立的窗口顶边。可以为空（那就只剩工作区地板）。</param>
public readonly record struct PetWorld(PetWorkArea WorkArea, IReadOnlyList<PetSurface>? Surfaces)
{
    /// <summary>地板：工作区的下边缘。永远存在，所以桌宠不会掉出屏幕。</summary>
    public PetSurface Floor => new(WorkArea.X, WorkArea.X + WorkArea.Width, WorkArea.Y + WorkArea.Height);

    /// <summary>没有窗口顶面时的世界（非 Windows 平台、或枚举失败时的兜底）。</summary>
    public static PetWorld JustWorkArea(PetWorkArea area) => new(area, null);
}
