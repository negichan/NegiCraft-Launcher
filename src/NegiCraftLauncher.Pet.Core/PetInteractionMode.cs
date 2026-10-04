namespace NegiCraftLauncher.Pet;

/// <summary>
/// 桌宠的四种交互模式。原来这个枚举在 Avalonia 与 WPF 两份 <c>PetWindow</c> 里各写了一遍，
/// 现在提到共享层 —— 两边是同一套状态机，不该有两份定义。
/// </summary>
public enum PetInteractionMode
{
    /// <summary>自由待机：不移动，只跟着光标转头。</summary>
    Free,

    /// <summary>操控：WASD / 空格 / Shift / Ctrl 直接驱动。</summary>
    Control,

    /// <summary>跟随鼠标：朝光标走，靠近到停靠半径就停。</summary>
    FollowMouse,

    /// <summary>前往坐标：按途经点队列逐个走过去，走完自动回 <see cref="Free"/>。</summary>
    NavigateToCoord,
}
