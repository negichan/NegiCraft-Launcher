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
    /// <summary>
    /// 出厂默认。<b>水平速度已按用户要求调快</b>（行走 = 原疾跑、疾跑 = 行走 × 2），
    /// 其余仍是 Minecraft 原版数值（重力沿用迁移前的 2400 DIP/s²）。
    /// </summary>
    public static PetPhysicsProfile Default { get; } = new();

    // ---------------------------------------------------------------- 水平速度

    /// <summary>
    /// 潜行速度（格/s）。<b>用户只要求加快行走 / 疾跑，潜行没动</b> —— 所以它不再是
    /// "行走 × 0.3"，而是一个独立的绝对值（原来 = 4.317 × 0.3）。要一起提就把这里改掉。
    /// </summary>
    public double SneakBlocksPerSecond { get; init; } = 1.295;

    /// <summary>
    /// 行走速度（格/s）。<b>用户要求：提到原来疾跑的速度</b>（原行走 4.317 → 现在 5.612）。
    /// </summary>
    public double WalkBlocksPerSecond { get; init; } = 5.612;

    /// <summary>
    /// 疾跑速度（格/s）。<b>用户要求：= 行走 × 2</b>。刻意做成派生属性，
    /// 这样改 <see cref="WalkBlocksPerSecond"/> 时两者不会走散。
    /// </summary>
    public double SprintBlocksPerSecond => WalkBlocksPerSecond * 2.0;

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

    // ---------------------------------------------------------------- 拖拽抛出
    // **这套参数当前没生效** —— 「甩出去」在 PetMotion.EndDrag 里被注释掉了（P4 预留，见那里的注释）。
    // 数值留着，打开时直接可用。

    /// <summary>
    /// 松手时的速度超过它才算"甩出去"（格/s）。低于它就只是轻轻放下、停在原地。
    /// 2 格/s ≈ 128 DIP/s —— 刻意慢放约 6 DIP/帧，普通"摆放"够不到。
    /// </summary>
    public double ThrowMinBlocksPerSecond { get; init; } = 2.0;

    /// <summary>
    /// 甩出去的速度上限（格/s）。手抖一下不该把桌宠甩到屏幕外；
    /// 18 格/s 在 45° 下射程约 8.6 格（≈550 DIP），够"飞一段"又不至于失控。
    /// </summary>
    public double ThrowMaxBlocksPerSecond { get; init; } = 18.0;

    /// <summary>落地弹跳的恢复系数（0 = 不弹、1 = 原速弹回）。</summary>
    public double BounceRestitution { get; init; } = 0.45;

    /// <summary>撞左右墙 / 天花板的恢复系数。</summary>
    public double WallRestitution { get; init; } = 0.55;

    /// <summary>每次触地后水平速度保留多少（模拟地面摩擦）。</summary>
    public double BounceHorizontalRetention { get; init; } = 0.7;

    /// <summary>
    /// 反弹后的竖直速度低于它就停下（格/s）。1.2 格/s 只能弹起约 2 DIP —— 肉眼不可见，
    /// 再弹下去只会看到桌宠在台面上抖。
    /// </summary>
    public double BounceMinBlocksPerSecond { get; init; } = 1.2;

    /// <summary>
    /// 估算松手速度的取样时间窗（秒）。只对最近这段时间内的采样做最小二乘回归 ——
    /// 手停住 200ms 再松手，窗内位置全一样，速度自然算成 0（"放下"而不是"甩"）。
    /// </summary>
    public double ThrowSampleWindowSeconds { get; init; } = 0.12;

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

    /// <summary>双击方向键（W/A/S/D 任一）触发疾跑锁定的时间窗（毫秒）。</summary>
    public double DoubleTapWindowMs { get; init; } = 350.0;
}
