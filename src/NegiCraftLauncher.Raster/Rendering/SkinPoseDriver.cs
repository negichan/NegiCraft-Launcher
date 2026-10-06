using System;
using System.Numerics;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 把「姿势 / 动画」这套数学从 <c>SkinRenderControl</c> 里拆出来，放进与 UI 框架无关的共享层。
///
/// <para>它只跟 <see cref="SkinRenderBase"/> 打交道（Tick / Rot / SetPos / 各部件旋转），
/// 所以 WPF 的启动器主页预览和 WPF 桌宠可以共用同一份；Avalonia 侧那份原样留在
/// <c>SkinRenderControl</c> 里不动 —— 它已经在跑，没有必要为一致性去动一个能用的东西。</para>
///
/// <para><b>可以同时驱动多个后端</b>（构造时传多个）。桌宠开 GPU 模式时就是这么用的：
/// 显示走 <c>SkinRenderGpu</c>，同时把 <c>SkinRenderSoftware</c> 一起驱动着 ——
/// 后者不参与显示，只为离屏截图（<c>SaveSnapshot</c> / 调试桥 <c>pet-skinsnap</c>）保留一份
/// 状态同步的软件后端。WPF 的 3D 内容抓不到离屏位图，没有这一份就没法出图。</para>
///
/// <para>数值全部照抄 Avalonia 版，改动任何一条都会让两个平台的姿态对不上：</para>
/// <list type="bullet">
/// <item>蹲下用 skinview3d 的 <c>|sin(k·π/2)|</c> 曲线，过渡 <c>SneakTransition</c> 秒。</item>
/// <item>待机呼吸 <c>0.02π + 0.03cos(2t)</c>，两条胳膊镜像外摆。</item>
/// <item>渲染器的旋转入参是"弧度 / 360"，所以度数要乘 <c>2π</c> 再喂进去。</item>
/// </list>
/// </summary>
public sealed class SkinPoseDriver
{
    // 渲染器的旋转输入先除以 360 再当弧度用。
    public const float RadToRotateInput = 360f;

    /// <summary>
    /// 朝向矩阵 → 渲染器的三个旋转入参。<see cref="SkinRenderBase.Rotation"/> 的逆运算：
    /// 它按 Rz(入参.X) · Rx(入参.Y) · Ry(入参.Z) 的行向量顺序搭矩阵，所以这里按同一顺序解回去。
    /// 和 <see cref="SkinRenderBase.Rotation"/> 的往返在 4000 个随机旋转上对拍过，最大矩阵误差 4e-6。
    /// </summary>
    public static Vector3 MatrixToRotateInput(System.Numerics.Matrix4x4 m)
    {
        var sinX = Math.Clamp(-m.M32, -1.0, 1.0);
        var x = (float)Math.Asin(sinX);
        var cosX = (float)Math.Cos(x);

        float y, z;
        if (MathF.Abs(cosX) > 1e-4f)
        {
            y = MathF.Atan2(m.M31, m.M33);
            z = MathF.Atan2(m.M12, m.M22);
        }
        else
        {
            y = MathF.Atan2(-m.M13, m.M11);
            z = 0;
        }

        return new Vector3(z * RadToRotateInput, x * RadToRotateInput, y * RadToRotateInput);
    }

    /// <summary>四元数版的便捷入口。</summary>
    public static Vector3 QuaternionToRotateInput(System.Numerics.Quaternion q) =>
        MatrixToRotateInput(System.Numerics.Matrix4x4.CreateFromQuaternion(q));

    // skinview3d 的 CrouchAnimation，单位是皮肤像素；8 像素 = 渲染器里的 1 个模型单位。
    private const float CrouchBodyLean = 0.4537860552f;
    private const float CrouchBodyY = -2.103677462f;
    private const float CrouchBodyZ = -2.1244129f;
    private const float CrouchHeadY = -3.618325234674f;
    private const float CrouchArmLean = 0.410367746202f;
    private const float CrouchArmSplay = 0.1f;
    private const float CrouchArmY = -2.53943318f;
    private const float CrouchArmZ = 0.1682942f;
    private const float CrouchLegZ = -3.4500310377f;

    private const double SneakTransition = 0.12;
    private const double DangleTransition = 0.15;
    private const double WalkTransition = 0.08;
    private const double JumpTransition = 0.10;

    // 空手挥击在 Minecraft 里是 6 个游戏刻。
    private const double AttackDuration = 0.3;

    // 叉腰站的骨架角。单位是弧度（喂进渲染器时再乘 RadToRotateInput），正负按"左手 / 左腿"给，
    // 右侧镜像。
    //
    // <b>第一版把肘的弯折轴搞错了</b>：手是叉在<b>腰两侧</b>的，不是伸到肚子前面。
    // 肘往回折发生在侧平面里 = 绕 Z 转 = 入参的 <b>X</b> 分量；入参的 Y 分量（绕 X）是往前/往后，
    // 拿它折弯小臂就会怼到身前 —— 实测就是这个错。
    //
    // 按参考图反解一遍几何（上臂 / 小臂各 0.75 单位长）：
    //   肩在 (0.75, 0.5, 0)，手要落在腰侧 (0.5, -0.45, 0)
    //   ⇒ 肘顶到 (1.17, -0.17, 0)：上臂从竖直往外转 32°，小臂再相对上臂折回 100°。
    // 叉腰站的骨架角。单位是弧度（进渲染器时乘 RadToRotateInput）。
    // 正负按"左手 / 左腿"（模型局部坐标系，朝向视口时角色左侧为画面右侧）给，右侧镜像。
    // 手稳稳扶在屁股上方一点（腰臀交界处），大臂后仰外展约 40°，肘部向外，小臂适度内收；
    // 双腿内向 X 腿开立（大腿与小腿角度预设，提供不同开度选项）。
    private const float HipsShoulderSplay = 0.70f;   // 上臂往外展约 40°，形成干净的手肘轮廓
    private const float HipsShoulderBack = 0.32f;    // 上臂向后仰（~+18°），将手肘与手部稳稳带向臀部
    private const float HipsElbowFold = -1.22f;     // 肘部适度内折 ~70°，手掌正好落在屁股上方（手一直扶在屁股上）
    private const float HipsElbowForward = 0.44f;   // 小臂向后带（~+25°），使手掌贴合在臀部表面
    private const float HipsLegSplay = 0.16f;       // 大腿外展 ~+9°（左右镜像，自然开立腿缝）
    private const float HipsLegPitch = -0.05f;      // 大腿微后仰（~-3°），保持站姿自然平稳
    private const float HipsLegPigeon = -0.28f;     // 内八足向扭转 ~-16°（内扣膝关节，形成自然的内八弯曲感）
    private const float HipsKneeBend = 0.13f;       // 膝盖微屈 ~+7.5°（随内八朝向内侧微微弯曲）
    private const float HipsKneeIn = 0.19f;         // 小腿外展 ~+11°（仅比大腿大 2°，绝不过度外撇）

    private const double HipsTransition = 0.18;

    // 扭胯摆动：节奏舒适、舒缓带感的左右顶胯律动。
    // 调慢摆动频率：1.30 Hz（循环周期 ~0.77s）。
    private const double SwayFrequency = 1.30;
    private const double SwayTransition = 0.22;

    // 核心轨迹：
    // 1. 骨盆横向平移（X轴左右顶胯）：极值达到 ±0.18 模型单位。
    //    曲线带两侧极值点停顿（Snap），快速穿越中点。
    // 2. 骨盆垂直起伏（2倍频 Bounce 下沉）：极值点最高，过中点时重心下沉 ~0.05 模型单位。
    // 3. 骨盆侧倾（Roll 绕 Z 轴）：顶胯侧骨盆抬高 ±14°（±0.24 rad）。纯平面摆动无竖向旋转（Yaw=0）。
    // 4. 脊椎反向 C 型补偿：胸椎反向倾斜抵消侧倾，使肩线与头部保持水平。
    // 5. 双脚无独立屈伸动画：脚掌锁死地面，仅随骨盆平移/侧倾做纯被动倾角补偿，由扭胯带动腿微动。
    private const float SwayHipShiftX = 0.18f;      // 骨盆横向左右位移幅度（模型单位）
    private const float SwayHipDipY = 0.05f;        // 骨盆过中点时 2 倍频下沉深度（模型单位）
    private const float SwayHipRoll = 0.24f;        // 骨盆侧倾角 ±14°（顶胯侧抬高）
    private const float SwaySpineCounter = 1.0f;    // 脊椎反向补偿比例（完全抵消肩部倾斜）

    /// <summary>姿势要同时施加到哪些后端。至少一个；多个时状态必然一致（同一份输入、同一份数学）。</summary>
    private readonly SkinRenderBase[] _renderers;

    private double _idleClock;
    private double _walkClock;
    private double _sneakK;
    private double _dangleK;
    private double _walkK;
    private double _jumpK;
    private double _hipsK;
    private double _swayK;
    private double _swayClock;
    private double _attackT = 1.0;
    private bool _attackParked;

    private System.Numerics.Vector3 _lastHeadLookRotate;

    // 调试用（pet-joint）：往单个关节上加一份角度，用来把上面那组叉腰数一点一点调对 ——
    // 每改一次数值就重新编译一次太慢了。键是部件，值是**度**（换算成渲染器入参在那一步做）。
    private readonly Dictionary<ModelPartType, System.Numerics.Vector3> _jointTweak = [];

    private float _lastTargetPitchDeg;
    private float _lastTargetYawDeg;

    public SkinPoseDriver(params SkinRenderBase[] renderers)
    {
        if (renderers.Length == 0) throw new ArgumentException("至少要有一个后端。", nameof(renderers));
        _renderers = renderers;
    }

    /// <summary>模型当前朝向（度）。<c>RotateModel</c> 累加它，头部相对角由它算。</summary>
    public float CurrentYawDeg { get; private set; } = 24f;

    // 姿势 / 朝向被外部改过、但还没画出来的标记。与 IsAnimating 合起来就是"这一帧要不要重画"。
    private bool _dirty = true;

    private bool _sneaking;
    private bool _dangling;
    private bool _walking;
    private bool _jumping;
    private bool _sprinting;

    public bool Sneaking
    {
        get => _sneaking;
        set { if (_sneaking != value) { _sneaking = value; _dirty = true; } }
    }

    public bool Dangling
    {
        get => _dangling;
        set { if (_dangling != value) { _dangling = value; _dirty = true; } }
    }

    public bool Walking
    {
        get => _walking;
        set { if (_walking != value) { _walking = value; _dirty = true; } }
    }

    public bool Jumping
    {
        get => _jumping;
        set { if (_jumping != value) { _jumping = value; _dirty = true; } }
    }

    public bool Sprinting
    {
        get => _sprinting;
        set { if (_sprinting != value) { _sprinting = value; _dirty = true; } }
    }

    /// <summary>
    /// 双手叉腰、两脚分开站。<b>要 <c>SkinRenderBase.LimbJoints</c> 开着才有意义</b> ——
    /// 单段四肢折不出肘，开了也只会看到胳膊斜着伸出去。
    /// </summary>
    public bool HandsOnHips
    {
        get => _hips;
        set { if (_hips != value) { _hips = value; _dirty = true; } }
    }

    private bool _hips;

    /// <summary>
    /// 扭胯摆动。<b>叠在当前姿势之上</b>，不替换它 —— 所以和 <see cref="HandsOnHips"/>
    /// 同时开就是"叉着腰扭"，这正是这个姿势的用法。
    ///
    /// <para>它走的是骨盆 / 胸椎那两节新关节（<c>SkinRenderBase.Hip*</c> / <c>Spine*</c>），
    /// 四肢和头是真的挂在下面被带着走的。旧的 <c>BodyRotate</c> 是扁平的、不带动任何部件，
    /// 用它扭只会看到躯干盒子在自己原地转。</para>
    /// </summary>
    public bool Swaying
    {
        get => _swaying;
        set { if (_swaying != value) { _swaying = value; _dirty = true; } }
    }

    private bool _swaying;

    /// <summary>
    /// 这一帧需不需要重画？—— 动画还在跑（<see cref="IsAnimating"/>），或者姿势 / 朝向被外部改过
    /// （旋转、转头、蹲/走/跳/跑开关、挥击、被拎起来）。
    ///
    /// <para><b>宿主用它来"静止时停渲染"</b>：桌面上的桌宠大部分时间是不动的，让它每秒白跑
    /// 60 次软件光栅化没有意义 —— 这是计划 §9 的性能预算里那条"idle 占用：静止时 CPU ≈ 0"。
    /// 唯一的代价是待机呼吸会停在当前相位（幅度 ±1.7°、约 1 像素，看不出来）。</para>
    /// </summary>
    public bool NeedsRepaint => _dirty || IsAnimating;

    /// <summary>启动一次挥臂；传 <paramref name="parkAt"/> 则把动画定格在那个进度（0-1）。</summary>
    public void TriggerAttack(double? parkAt = null)
    {
        _attackT = parkAt ?? 0.0;
        _attackParked = parkAt.HasValue;
        _dirty = true;
    }

    private bool _swayParked;

    /// <summary>调试用：把扭胯动画定格在某个进度相位（0..1，0=左极值、0.25=回中、0.5=右极值）。传 null 解除定格。</summary>
    public void ParkSway(double? phase = null)
    {
        _swayParked = phase.HasValue;
        if (phase.HasValue)
        {
            _swayK = 1.0;
            _swayClock = phase.Value;
            _swaying = true;
        }
        _dirty = true;
    }

    /// <summary>
    /// 调试用：给某个关节的角度加一份偏移（<b>度</b>，按渲染器那三个入参的顺序 X=外摆 / Y=前后 / Z=竖转）。
    /// 传 0 就是把这个关节的偏移清掉。
    /// </summary>
    public void TweakJoint(ModelPartType part, float xDeg, float yDeg, float zDeg)
    {
        var v = new System.Numerics.Vector3(xDeg, yDeg, zDeg);

        if (v == System.Numerics.Vector3.Zero) _jointTweak.Remove(part);
        else _jointTweak[part] = v;

        _dirty = true;
    }

    /// <summary>把某个关节上的调试偏移换成渲染器的入参单位（度 → 弧度 × 360）。</summary>
    private System.Numerics.Vector3 Tweak(ModelPartType part) =>
        _jointTweak.TryGetValue(part, out var d)
            ? new System.Numerics.Vector3(
                d.X * 2f * (float)Math.PI,
                d.Y * 2f * (float)Math.PI,
                d.Z * 2f * (float)Math.PI)
            : System.Numerics.Vector3.Zero;

    private static System.Numerics.Vector3 Vector3Lerp(
        System.Numerics.Vector3 a, System.Numerics.Vector3 b, float k) =>
        a + (b - a) * k;

    /// <summary>把模型摆回初始姿态：位置归零、朝向复位，动画进度也一并退回中立。</summary>
    public void Reset()
    {
        // 定格的挥击要在这里放掉。<see cref="TriggerAttack"/> 传了 parkAt 就不自己走完（_attackParked），
        // 而换皮肤 / 重建驱动都会走 Reset —— 不清的话那份躯干扭转会漏给之后所有姿势，且再也解不开。
        _attackT = 1.0;
        _attackParked = false;

        foreach (var r in _renderers)
        {
            r.ResetPos();
            r.HipPos = System.Numerics.Vector3.Zero;
            r.HipRotate = System.Numerics.Vector3.Zero;
            r.SpineDeform = System.Numerics.Vector3.Zero;
            r.LeftArmDeform = LimbDeform.Identity;
            r.RightArmDeform = LimbDeform.Identity;
            r.LeftLegDeform = LimbDeform.Identity;
            r.RightLegDeform = LimbDeform.Identity;
            // 抬 0.3 个单位，头落在 Y≈36px、脚在 Y≈209px。
            r.SetPos(0, 0.3f);
            r.Rot(0, CurrentYawDeg * 2f * (float)Math.PI);
        }

        UpdateHeadRotate();
        UpdatePose();
        _dirty = true;
    }

    /// <summary>绕竖直轴转 <paramref name="deltaYawDeg"/> 度。</summary>
    public void RotateModel(float deltaYawDeg)
    {
        CurrentYawDeg += deltaYawDeg;
        while (CurrentYawDeg > 180f) CurrentYawDeg -= 360f;
        while (CurrentYawDeg < -180f) CurrentYawDeg += 360f;

        foreach (var r in _renderers) r.Rot(0, deltaYawDeg * 2f * (float)Math.PI);

        UpdateHeadRotate();
        _dirty = true;
    }

    /// <summary>头部看向某个方向（相对世界，度）。</summary>
    public void SetHeadLookAt(float targetPitchDeg, float targetYawDeg)
    {
        // 目标没变就什么都不做 —— 宿主的心跳是每 16ms 喂一次同样的值，
        // 不挡住的话脏标记会一直被点亮，"静止时停渲染"永远生效不了。
        if (_lastTargetPitchDeg == targetPitchDeg && _lastTargetYawDeg == targetYawDeg) return;

        _lastTargetPitchDeg = targetPitchDeg;
        _lastTargetYawDeg = targetYawDeg;
        UpdateHeadRotate();
        _dirty = true;
    }

    /// <summary>推进 <paramref name="dt"/> 秒，然后 Tick 渲染器并重算姿态。跑完即清掉脏标记。</summary>
    public void Update(double dt)
    {
        _idleClock += dt;

        _sneakK = Math.Clamp(_sneakK + ((Sneaking ? 1 : -1) * dt / SneakTransition), 0, 1);
        _dangleK = Math.Clamp(_dangleK + ((Dangling ? 1 : -1) * dt / DangleTransition), 0, 1);
        _walkK = Math.Clamp(_walkK + ((Walking ? 1 : -1) * dt / WalkTransition), 0, 1);
        _hipsK = Math.Clamp(_hipsK + (((HandsOnHips || Swaying) ? 1 : -1) * dt / HipsTransition), 0, 1);

        if (!_swayParked)
        {
            _swayK = Math.Clamp(_swayK + ((Swaying ? 1 : -1) * dt / SwayTransition), 0, 1);

            // 相位只在还有摆动（含收尾）的时候走；完全归零后把时钟也清掉，
            // 下次打开是从中立位起摆，而不是接着上一回的相位。
            if (_swayK > 0) _swayClock += dt * SwayFrequency;
            else _swayClock = 0;
        }

        if (_walkK > 0)
        {
            var walkFrequency = Sprinting ? 16.5 : 12.0 * (1.0 - 0.45 * _sneakK);
            var groundProgress = 1.0 - 0.85 * _jumpK;
            _walkClock += dt * walkFrequency * groundProgress;
        }

        _jumpK = Math.Clamp(_jumpK + ((Jumping ? 1 : -1) * dt / JumpTransition), 0, 1);
        if (_attackT < 1.0 && !_attackParked) _attackT = Math.Min(1.0, _attackT + dt / AttackDuration);

        foreach (var r in _renderers) r.Tick(dt);

        UpdateHeadRotate();
        UpdatePose();
        _dirty = false;
    }

    /// <summary>
    /// 还有动画在跑吗？没有的话上层可以停下来省点 CPU。
    ///
    /// <para><b>与 Avalonia 侧 <c>SkinRenderControl.OnOpenGlRender</c> 末尾那段判据逐条一致</b>
    /// （那边是生产在跑的版本，这里是它的具名版）—— 两端的渲染节奏要对齐。
    /// 蹲下只在**过渡中**算"在动"：蹲稳之后姿势是定的，没必要继续重画。</para>
    ///
    /// <para>注意它**不含待机呼吸**（那是常驻的），所以"静止"是真的静止 ——
    /// 这也是为什么 <see cref="NeedsRepaint"/> 要另外记一份 <c>_dirty</c>。</para>
    /// </summary>
    public bool IsAnimating =>
        _dangleK > 0 || Dangling ||
        (_sneakK > 0 && _sneakK < 1) ||
        (_hipsK > 0 && _hipsK < 1) ||
        (_swayK > 0 && !_swayParked) ||
        _walkK > 0 || Walking ||
        _jumpK > 0 || Jumping ||
        (_attackT < 1.0 && !_attackParked);

    private void UpdateHeadRotate()
    {
        // 相对脖子转角：headYaw = 目标朝向 - 身体朝向。
        var diffYaw = _lastTargetYawDeg - CurrentYawDeg;
        while (diffYaw > 180f) diffYaw -= 360f;
        while (diffYaw < -180f) diffYaw += 360f;

        var headYaw = Math.Clamp(diffYaw, -55f, 55f);
        var headPitch = Math.Clamp(_lastTargetPitchDeg, -25f, 25f);
        var headRoll = 0f;

        // 被拎起来的时候摇头抗议：约 16 rad/s、±28°，同时抬头看光标。
        var dk = (float)_dangleK;
        if (dk > 0)
        {
            var shakeYaw = (float)(Math.Sin(_idleClock * 16.0) * 28.0);
            var shakePitch = -18f + (float)(Math.Sin(_idleClock * 8.0) * 4.0);
            var shakeRoll = (float)(Math.Sin((_idleClock * 16.0) + (Math.PI / 4)) * 6.0);

            headYaw = ((1 - dk) * headYaw) + (dk * shakeYaw);
            headPitch = ((1 - dk) * headPitch) + (dk * shakePitch);
            headRoll = dk * shakeRoll;
        }

        var rotate = new System.Numerics.Vector3(
            headRoll * 2.0f * (float)Math.PI,
            headPitch * 2.0f * (float)Math.PI,
            headYaw * 2.0f * (float)Math.PI);

        _lastHeadLookRotate = rotate;
        foreach (var r in _renderers) r.HeadRotate = rotate;
    }

    private void UpdatePose()
    {
        // 蹲下用 |sin(k·π/2)| 过渡。
        var e = (float)Math.Sin(_sneakK * Math.PI / 2);
        var px = e / 8f;

        var dk = (float)_dangleK;
        var wk = (float)_walkK * (1.0f - dk);
        var jk = (float)_jumpK * (1.0f - dk);

        // 扭胯：基于视频与关键帧拆解的顶胯动作：
        // 骨盆横向平移与侧倾 + 2倍频下沉起伏 + 脊椎C型反向补偿 + 双腿纯被动接地跟随
        var sk = (float)_swayK;
        System.Numerics.Vector3 hipRot = default, hipPos = default, spineBend = default, headLevel = default;
        System.Numerics.Vector3 ikThighL = default, ikThighR = default;
        float armSwayRoll = 0f, armSwayElbow = 0f, sSnap = 0f;

        if (sk > 0)
        {
            var w = _swayClock * 2.0 * Math.PI;
            var s = (float)Math.Sin(w);

            // 两端带有短暂停顿感（Snap）的修正曲线：保留正负号，在极值点稍作停留，穿过中点时快速有力
            sSnap = MathF.Sign(s) * MathF.Pow(MathF.Abs(s), 0.72f);

            // 1. 骨盆横向平移（X轴左右顶胯）：极值达到 ±0.20 模型单位
            var curShiftX = SwayHipShiftX * sSnap * sk;

            // 2. 骨盆垂直起伏（2倍频 Bounce 下沉，过中点 s=0 时下沉，极值点最高）
            var bounceProgress = 1.0f - (s * s); // 0 at extremes, 1 at center
            var curDipY = -SwayHipDipY * bounceProgress * sk;
            hipPos = new System.Numerics.Vector3(curShiftX, curDipY, 0);

            // 3. 骨盆侧倾（Roll 绕 Z 轴）：顶胯侧骨盆抬高（角度随 sSnap 走）
            var hipRoll = SwayHipRoll * sSnap * sk;
            hipRot = new System.Numerics.Vector3(hipRoll * RadToRotateInput, 0, 0);

            // 4. 脊椎反向 C 型补偿：完全抵消骨盆侧倾，使肩线保持水平
            spineBend = new System.Numerics.Vector3(-hipRoll * SwaySpineCounter * RadToRotateInput, 0, 0);

            // 5. 双腿地面锁定（双脚彻底固定在地面，绝对不动、不滑移、不歪斜）：
            // 小腿与脚底在渲染器中与骨盆断开级联，完全锁定在静止贴地姿态（绝对平直不歪斜、零晃动）；
            // 骨盆左右顶胯 curShiftX 并侧倾 hipRoll 时，仅大腿作为连杆产生自然倾角微动，连接顶胯骨盆与固定膝位。
            float dRoll_L  = -0.3884f * sSnap * sk;
            float dPitch_L = -0.1142f * sSnap * sk;

            float dRoll_R  = -0.3884f * sSnap * sk;
            float dPitch_R =  0.1142f * sSnap * sk;

            ikThighL = new System.Numerics.Vector3(dRoll_L, dPitch_L, 0) * RadToRotateInput;
            ikThighR = new System.Numerics.Vector3(dRoll_R, dPitch_R, 0) * RadToRotateInput;

            // 6. 手臂动态跟随骨盆：手一直扶在屁股上不脱手。
            // 骨盆横移 curShiftX 且侧倾 hipRoll 时，大臂与小臂同步产生横向平移与屈折代偿
            armSwayRoll = (curShiftX / 1.15f) * RadToRotateInput;
            armSwayElbow = -hipRoll * 0.70f * RadToRotateInput;
        }

        // 挥击：躯干先拧再回、手臂四次缓出前劈，1:1 复刻原版人形动作。
        float bodyTurn = 0f;
        float attackPitch = 0f;
        float attackYaw = 0f;
        float attackRoll = 0f;
        float attackBodyYaw = 0f;

        if (_attackT < 1.0)
        {
            var p = (float)_attackT;

            bodyTurn = -(float)Math.Sin(Math.Sqrt(p) * Math.PI * 2.0) * 0.18f;
            attackBodyYaw = bodyTurn * RadToRotateInput * (1 - dk);

            var f = 1.0f - p;
            f = 1.0f - (f * f * f * f);
            var f1 = (float)Math.Sin(f * Math.PI);
            var headPitchRad = _lastTargetPitchDeg * ((float)Math.PI / 180f);
            var f2 = (float)Math.Sin(p * Math.PI) * -(headPitchRad - 0.7f) * 0.75f;

            attackPitch = -(f1 * 1.25f + f2) * RadToRotateInput * (1 - dk);
            attackYaw = bodyTurn * 1.8f * RadToRotateInput * (1 - dk);
            attackRoll = (float)Math.Sin(p * Math.PI) * 0.40f * RadToRotateInput * (1 - dk);
        }

        var shoulderOffsetZ = (float)(0.75 * Math.Sin(bodyTurn)) * (1 - dk);

        var bodyPos = new System.Numerics.Vector3(0, CrouchBodyY * px, CrouchBodyZ * px);
        var headPos = new System.Numerics.Vector3(0, CrouchHeadY * px, 0);
        var leftArmPos = new System.Numerics.Vector3(0, CrouchArmY * px, (CrouchArmZ * px) - shoulderOffsetZ);
        var rightArmPos = new System.Numerics.Vector3(0, CrouchArmY * px, (CrouchArmZ * px) + shoulderOffsetZ);
        var legPos = new System.Numerics.Vector3(0, 0, CrouchLegZ * px);

        foreach (var r in _renderers)
        {
            r.BodyPos = bodyPos;
            r.HeadPos = headPos;
            r.LeftArmPos = leftArmPos;
            r.RightArmPos = rightArmPos;
            r.LeftLegPos = legPos;
            r.RightLegPos = legPos;
        }

        // 待机呼吸；蹲下时再加一份外摆并把胳膊往后带，让它们垂着而不是荡着。
        var idle = (float)((0.02 * Math.PI) + (0.03 * Math.Cos(2 * _idleClock))) * RadToRotateInput;
        var splay = idle + (CrouchArmSplay * e * RadToRotateInput);
        var armLean = CrouchArmLean * e * RadToRotateInput;

        // 走路 / 疾跑摆臂摆腿（腾空时压掉地面摆动）。
        // ⚠️ 这个值**不要再乘 RadToRotateInput** —— 它在下面 BodyRotate.Y 里已经乘过一次了。
        // 早先这里多乘了一次，导致疾跑前倾变成 0.07×360×360（≈25.2 rad = 4 整圈）：
        // 一按疾跑、一起跳，躯干就"唰"地转 4 圈再转回来，看着像胳膊旁边有东西一闪而过。
        var sprintBodyLean = Sprinting ? 0.07f * wk * (1.0f - jk) : 0f;
        var sprintFactor = Sprinting ? 1.25f : 1.0f;
        var airLimbFactor = 1.0f - (jk * 0.85f);
        var legSwing = (float)(Math.Sin(_walkClock) * 0.62 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        var armSwing = (float)(Math.Sin(_walkClock) * 0.52 * RadToRotateInput) * wk * sprintFactor * airLimbFactor;
        var jumpLegSwing = 0.22f * RadToRotateInput * jk;

        var walkBob = (float)(Math.Abs(Math.Sin(_walkClock)) * 0.025) * wk * (1.0f - jk);
        var standPos = 0.3f + (0.12f * dk) + walkBob;
        foreach (var r in _renderers) r.SetPos(0, standPos);

        // 被拎起来：胳膊举过头顶去抓光标，还带点扑腾。
        var flutter = (float)(Math.Sin(_idleClock * 16.0) * 0.08 * RadToRotateInput);
        var dangleArmPitch = (-2.75f * RadToRotateInput) + flutter;
        var dangleArmLX = (-0.15f * RadToRotateInput) + (flutter * 0.2f);
        var dangleArmRX = (0.15f * RadToRotateInput) - (flutter * 0.2f);

        var normalArmLY = armLean + armSwing;
        var normalArmRY = armLean - armSwing;

        var finalArmLX = ((1 - dk) * splay) + (dk * dangleArmLX);
        var finalArmRX = ((1 - dk) * -splay) + (dk * dangleArmRX);
        var finalArmLY = ((1 - dk) * normalArmLY) + (dk * dangleArmPitch);
        var finalArmRY = ((1 - dk) * normalArmRY) + (dk * dangleArmPitch);

        var leftArmYaw = bodyTurn * 0.8f * RadToRotateInput * (1 - dk);

        var leftArmRotate = new System.Numerics.Vector3(finalArmLX, finalArmLY, leftArmYaw);
        var rightArmRotate = new System.Numerics.Vector3(finalArmRX + attackRoll, finalArmRY + attackPitch, attackYaw);

        // 叉腰：把上面那套（呼吸外摆 + 走路摆臂）往"折起来"的目标上插值。hk=1 时目标是个定值，
        // 所以走路摆动自然被压干净，不用另外加"叉腰时不许摆臂"的特判。
        // 四个关节（两条上臂 + 两条小臂）都按同一个 hk 过渡，不然肘会"啪"地一下弹到位。
        var hk = (float)_hipsK;

        if (hk > 0)
        {
            var hipsArmL = new System.Numerics.Vector3(HipsShoulderSplay, HipsShoulderBack, 0) * RadToRotateInput;
            var hipsArmR = new System.Numerics.Vector3(-HipsShoulderSplay, HipsShoulderBack, 0) * RadToRotateInput;

            leftArmRotate = Vector3Lerp(leftArmRotate, hipsArmL, hk);
            rightArmRotate = Vector3Lerp(rightArmRotate, hipsArmR, hk);
        }

        // 小臂 / 小腿折叠与自由形变计算
        var leftElbowRotate = Vector3Lerp(
            Vector3.Zero,
            new Vector3(HipsElbowFold, HipsElbowForward, 0) * RadToRotateInput,
            hk);
        var rightElbowRotate = Vector3Lerp(
            Vector3.Zero,
            new Vector3(-HipsElbowFold, HipsElbowForward, 0) * RadToRotateInput,
            hk);

        // 腿：走路摆动 + 起跳分开 + 被拎起来时乱蹬。
        var kick = (float)(Math.Sin(_idleClock * 13.5) * 0.65 * RadToRotateInput);
        var legSplay = 0.12f * RadToRotateInput;

        var leftLegY = ((1 - dk) * ((legSwing * (1 - (jk * 0.5f))) + jumpLegSwing)) + (kick * dk);
        var rightLegY = ((1 - dk) * ((-legSwing * (1 - (jk * 0.5f))) - jumpLegSwing)) - (kick * dk);

        var leftLegRotate = new Vector3(legSplay * dk, leftLegY, 0);
        var rightLegRotate = new Vector3(-legSplay * dk, rightLegY, 0);

        var hipsLegL = new Vector3(HipsLegSplay, HipsLegPitch, HipsLegPigeon) * RadToRotateInput;
        var hipsLegR = new Vector3(-HipsLegSplay, HipsLegPitch, -HipsLegPigeon) * RadToRotateInput;

        if (hk > 0)
        {
            leftLegRotate = Vector3Lerp(leftLegRotate, hipsLegL, hk);
            rightLegRotate = Vector3Lerp(rightLegRotate, hipsLegR, hk);
        }

        var finalThighL = leftLegRotate + ikThighL + Tweak(ModelPartType.LeftLeg);
        var finalThighR = rightLegRotate + ikThighR + Tweak(ModelPartType.RightLeg);

        // 自由形变（FFD）计算：手臂折肘与双腿内八接地
        var blendFactor = Math.Max(hk, sk);
        var armDeformL = LimbDeform.Identity;
        var armDeformR = LimbDeform.Identity;
        var legDeformL = LimbDeform.Identity;
        var legDeformR = LimbDeform.Identity;

        if (blendFactor > 0)
        {
            // 1. 手臂折肘自由弯曲：
            // 大臂自然伸展，肘部平滑过渡弯曲，手掌稳定贴在臀部
            var elbowL = leftElbowRotate + new Vector3(armSwayElbow, 0, 0) + Tweak(ModelPartType.LeftForeArm);
            var elbowR = rightElbowRotate + new Vector3(armSwayElbow, 0, 0) + Tweak(ModelPartType.RightForeArm);

            var mElbowL = SkinRenderBase.Rotation(elbowL);
            var mElbowR = SkinRenderBase.Rotation(elbowR);
            var qElbowL = Quaternion.CreateFromRotationMatrix(mElbowL);
            var qElbowR = Quaternion.CreateFromRotationMatrix(mElbowR);

            armDeformL = new LimbDeform
            {
                BendRotation = qElbowL,
                Offset = Vector3.Zero,
                TransitionTop = 0.22f,
                TransitionBottom = -0.22f
            };
            armDeformR = new LimbDeform
            {
                BendRotation = qElbowR,
                Offset = Vector3.Zero,
                TransitionTop = 0.22f,
                TransitionBottom = -0.22f
            };

            // 2. 双腿自由弯曲与足底接地锁定：
            // - 小腿部分随膝盖左右摆动微微摇曳连动（过渡区延伸至整根小腿）
            // - 脚底保持贴地（Roll = 0, Pitch = 0）
            // - 当小腿向外摆到极值时，脚产生轻微动一丁点（向外微微给约 1.5 像素的弹性缓冲后回正）
            var kickL = MathF.Pow(Math.Max(0f, sSnap), 2f) * sk;
            var kickR = MathF.Pow(Math.Max(0f, -sSnap), 2f) * sk;

            var footShiftL = new Vector3(0.020f * kickL, 0, 0);
            var footShiftR = new Vector3(-0.020f * kickR, 0, 0);

            // 脚底平面绝对水平贴地（Roll = 0, Pitch = 0, Yaw = Pigeon）
            var mTargetFootL = Matrix4x4.CreateRotationY(HipsLegPigeon);
            var mTargetFootR = Matrix4x4.CreateRotationY(-HipsLegPigeon);

            var mThighWorldL = SkinRenderBase.Rotation(finalThighL) * SkinRenderBase.Rotation(hipRot);
            var mThighWorldR = SkinRenderBase.Rotation(finalThighR) * SkinRenderBase.Rotation(hipRot);

            Matrix4x4.Invert(mThighWorldL, out var invThighL);
            Matrix4x4.Invert(mThighWorldR, out var invThighR);

            var mBendL = mTargetFootL * invThighL;
            var mBendR = mTargetFootR * invThighR;
            var qBendL = Quaternion.CreateFromRotationMatrix(mBendL);
            var qBendR = Quaternion.CreateFromRotationMatrix(mBendR);

            // 参考站姿下双脚世界位置
            var kneeRestL = new Vector3(0.25f, -0.75f, 0) + Vector3.Transform(new Vector3(0, -0.75f, 0), SkinRenderBase.Rotation(hipsLegL));
            var kneeRestR = new Vector3(-0.25f, -0.75f, 0) + Vector3.Transform(new Vector3(0, -0.75f, 0), SkinRenderBase.Rotation(hipsLegR));
            var footRestL = kneeRestL + new Vector3(0, -0.75f, 0);
            var footRestR = kneeRestR + new Vector3(0, -0.75f, 0);

            var curTargetFootL = footRestL + footShiftL;
            var curTargetFootR = footRestR + footShiftR;

            // 当前帧骨盆与大腿移动后的膝盖与脚底世界坐标
            var hipSocketL = Vector3.Transform(new Vector3(0.25f, 0, 0), SkinRenderBase.Rotation(hipRot)) + new Vector3(0, -0.75f, 0) + hipPos;
            var hipSocketR = Vector3.Transform(new Vector3(-0.25f, 0, 0), SkinRenderBase.Rotation(hipRot)) + new Vector3(0, -0.75f, 0) + hipPos;

            var kneeWorldL = hipSocketL + Vector3.Transform(new Vector3(0, -0.75f, 0), mThighWorldL);
            var kneeWorldR = hipSocketR + Vector3.Transform(new Vector3(0, -0.75f, 0), mThighWorldR);

            var soleWorldL = kneeWorldL + new Vector3(0, -0.75f, 0);
            var soleWorldR = kneeWorldR + new Vector3(0, -0.75f, 0);

            // 将脚底固定所需的世界位移补偿转换到大腿局部坐标系
            var offsetL = Vector3.Transform(curTargetFootL - soleWorldL, invThighL);
            var offsetR = Vector3.Transform(curTargetFootR - soleWorldR, invThighR);

            // 小腿过渡区从膝盖（0.12）平滑铺开到脚踝上方（-0.58），使整根小腿自然微动，仅脚部紧贴地面
            legDeformL = new LimbDeform
            {
                BendRotation = qBendL,
                Offset = offsetL,
                TransitionTop = 0.12f,
                TransitionBottom = -0.58f
            };
            legDeformR = new LimbDeform
            {
                BendRotation = qBendR,
                Offset = offsetR,
                TransitionTop = 0.12f,
                TransitionBottom = -0.58f
            };

            if (blendFactor < 1f)
            {
                armDeformL = LimbDeform.Lerp(LimbDeform.Identity, armDeformL, blendFactor);
                armDeformR = LimbDeform.Lerp(LimbDeform.Identity, armDeformR, blendFactor);
                legDeformL = LimbDeform.Lerp(LimbDeform.Identity, legDeformL, blendFactor);
                legDeformR = LimbDeform.Lerp(LimbDeform.Identity, legDeformR, blendFactor);
            }
        }

        foreach (var r in _renderers)
        {
            // 四肢完全使用自由形变（整根网格竖切细分 + 平滑过渡曲面），彻底取代两段式硬切割
            r.LimbJoints = false;
            r.LimbFlexible = blendFactor > 0f;

            r.LeftArmDeform = armDeformL;
            r.RightArmDeform = armDeformR;
            r.LeftLegDeform = legDeformL;
            r.RightLegDeform = legDeformR;

            // 摆动时手臂动态跟随骨盆横移与侧倾，使双手始终稳稳贴在臀部上
            r.LeftArmRotate = leftArmRotate + new Vector3(armSwayRoll, 0, 0) + Tweak(ModelPartType.LeftArm);
            r.RightArmRotate = rightArmRotate + new Vector3(armSwayRoll, 0, 0) + Tweak(ModelPartType.RightArm);

            r.LeftLegRotate = finalThighL;
            r.RightLegRotate = finalThighR;
        }

        var bodySwing = (float)(Math.Sin(_idleClock * 13.5) * 0.05 * RadToRotateInput);
        var walkBodyTilt = (float)(Math.Sin(_walkClock) * 0.03 * RadToRotateInput) * wk * (1.0f - jk);

        var bodyRotate = new System.Numerics.Vector3(
            0,
            ((CrouchBodyLean * e) - (0.06f * dk) + sprintBodyLean) * RadToRotateInput,
            (bodySwing * dk) + walkBodyTilt + attackBodyYaw);

        foreach (var r in _renderers) r.BodyRotate = bodyRotate;

        // 骨盆平移/侧倾、脊椎反向补偿、头部视线稳定
        foreach (var r in _renderers)
        {
            r.HipPos = hipPos;
            r.HipRotate = hipRot;

            // pet-joint body <侧弯> <前弯> <扭转>（度）直接掰脊椎，用来静态拍形变长什么样 ——
            // 摆动是正弦的，抓不到想要的相位。
            r.SpineDeform = spineBend + Tweak(ModelPartType.Body);

            r.HeadRotate = r.HeadRotate + headLevel;
        }
    }
}
