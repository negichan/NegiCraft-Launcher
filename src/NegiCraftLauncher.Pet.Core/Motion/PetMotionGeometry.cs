namespace NegiCraftLauncher.Pet;

/// <summary>屏幕上的一个点，单位一律是 <b>DIP</b>。</summary>
public readonly record struct PetPoint(double X, double Y);

/// <summary>
/// 屏幕工作区（不含任务栏），单位 <b>DIP</b>。
/// Avalonia 的 <c>Screen.WorkingArea</c> 是物理像素，WPF 的 <c>SystemParameters.WorkArea</c> 是 DIP ——
/// 由宿主统一换算成 DIP 再传进来，物理内核就不用关心平台了。
/// </summary>
public readonly record struct PetWorkArea(double X, double Y, double Width, double Height);

/// <summary>
/// 桌宠的"舞台"几何：窗口尺寸 + 缩放 + 皮肤在舞台里的竖直偏移。
///
/// <para>皮肤模型是以 160x320 的舞台为基准画的，头心在 <c>52 + StageOffsetY</c>、脚底在
/// <c>160 + StageOffsetY</c>（舞台单位）。物理里的"头部中心 / 脚底位置"就靠这两个数换算。</para>
/// </summary>
public readonly record struct PetStage(double Width, double Height, double Scale, double StageOffsetY);

/// <summary>
/// 一帧物理需要的全部外部输入。宿主每帧构造一次，内核不持有任何窗口引用。
/// </summary>
/// <param name="Stage">舞台几何。</param>
/// <param name="WorkArea">屏幕工作区（DIP）—— 内核拿它的下边缘当地板、左右边当墙。</param>
/// <param name="Window">窗口左上角（DIP）。</param>
/// <param name="Cursor">光标（DIP），没有就 null。</param>
/// <param name="CurrentYawDeg">当前朝向，宿主持有。</param>
/// <param name="Surfaces">
/// 可站立的窗口顶边（DIP）。宿主可以按 ~150ms 节流重建，内核每帧只读。
/// <b>null = 只有工作区地板</b>（非 Windows 平台、或枚举失败时的兜底）。
/// </param>
public readonly record struct PetMotionContext(
    PetStage Stage,
    PetWorkArea WorkArea,
    PetPoint Window,
    PetPoint? Cursor,
    float CurrentYawDeg,
    IReadOnlyList<PetSurface>? Surfaces = null);
