namespace NegiCraftLauncher.Pet;

/// <summary>
/// 桌宠物理参数集。<b>所有速度 / 重力 / 跳跃高度按 Minecraft 原版的「格」给</b>，
/// 用时乘 <see cref="PetMotion.DipPerBlock"/>（以及桌宠缩放）换成 DIP ——
/// 所以桌宠被放大到 150% 时，"一格"也跟着变大，相对自身身高的手感不变。
///
/// <para><b>为什么单独一个 record</b>：这些数值原来散在 <see cref="PetMotion"/> 的 const 区里，
/// 改一个手感就要重编译、也没法在自测里换一套跑对照。现在集中成不可变 record，
/// 想试"轻快"或"沉重"直接 <c>with { ... }</c>，不用碰逻辑。</para>
///
/// <para><b>暂不给 UI</b>：目前只有内核与调试动词读它。要加设置项时，
/// 在宿主里把序列化出来的 profile 塞给 <see cref="PetMotion.Profile"/> 即可，逻辑侧不用改。</para>
/// </summary>
public sealed record PetPhysicsProfile
{
    /// <summary>出厂默认 —— 全部是 Minecraft 原版数值（重力沿用迁移前的 2400 DIP/s²）。</summary>
    public static PetPhysicsProfile Default { get; } = new();

    // ---------------------------------------------------------------- 水平速度

    /// <summary>潜行速度（格/s）。Minecraft 原版 1.295，= 行走 × 0.3。</summary>
    public double SneakBlocksPerSecond { get; init; } = 1.295;

    /// <summary>行走速度（格/s）。Minecraft 原版 4.317。</summary>
    public double WalkBlocksPerSecond { get; init; } = 4.317;

    /// <summary>疾跑速度（格/s）。Minecraft 原版 5.612。</summary>
    public double SprintBlocksPerSecond { get; init; } = 5.612;

    /// <summary>跳跃中行走（格/s）—— 沿用原来的 200/180 比例，改行走会跟着改。</summary>
    public double JumpWalkBlocksPerSecond => WalkBlocksPerSecond * 10.0 / 9.0;

    /// <summary>跳跃中疾跑（格/s）—— 沿用原来的 290/270 比例。</summary>
    public double JumpSprintBlocksPerSecond => SprintBlocksPerSecond * 29.0 / 27.0;

    // ---------------------------------------------------------------- 竖直

    /// <summary>
    /// 重力（格/s²）。沿用迁移前的 2400 DIP/s² ÷ 64 = 37.5 —— 比 Minecraft 的 32 略重，
    /// 落得干脆些。跳跃初速是按它反解的，改它会连带改跳跃滞空时间。
    /// </summary>
    public double GravityBlocksPerSecondSquared { get; init; } = 2400.0 / PetMotion.DipPerBlock;

    /// <summary>目标跳跃高度（格）。Minecraft 原版起跳约 1.25 格 —— 刚好够跨上一格台阶。</summary>
    public double JumpHeightBlocks { get; init; } = 1.25;

    /// <summary>
    /// 起跳初速度（格/s，向上为负）。由 h = v²/(2g) 反解 ⇒ √(2 × 37.5 × 1.25) ≈ 9.68 格/s
    /// （滞空约 0.52s）。<b>用的时候要乘 <see cref="PetMotion.DipPerBlock"/> × 缩放。</b>
    /// </summary>
    public double JumpVelocityBlocksPerSecond =>
        -Math.Sqrt(2 * GravityBlocksPerSecondSquared * JumpHeightBlocks);

    // ---------------------------------------------------------------- 转身

    /// <summary>朝行进方向转身的最大角速度（deg/s）。</summary>
    public double MoveTurnSpeed { get; init; } = 720.0;

    // ---------------------------------------------------------------- 跟随 / 导航阈值（DIP）

    /// <summary>跟随鼠标的停靠半径：光标在身旁这么近就停下。</summary>
    public double FollowDockRadius { get; init; } = 85.0;

    /// <summary>跟随鼠标时超过这个距离就疾跑。</summary>
    public double FollowSprintDistance { get; init; } = 360.0;

    /// <summary>坐标导航时超过这个距离就疾跑。</summary>
    public double NavSprintDistance { get; init; } = 350.0;

    /// <summary>坐标导航的到达判定半径。</summary>
    public double NavArriveDistance { get; init; } = 8.0;

    // ---------------------------------------------------------------- 输入

    /// <summary>双击 W 触发疾跑锁定的时间窗（毫秒）。</summary>
    public double DoubleTapWindowMs { get; init; } = 350.0;
}
