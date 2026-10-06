using System.Numerics;

namespace MinecraftSkinRender;

/// <summary>
/// 渲染器的<b>与图形 API 无关</b>的那一半：画布尺寸、皮肤类型、背景色、鼠标交互、位姿数学、
/// 内置走路动画的驱动。后端（OpenGL / 软件光栅化）继承它，自己负责"把三角形画到哪去"。
///
/// <para><b>为什么单独拆一层</b>：NegiCraft 有两个渲染后端 —— Windows 走 WPF，用自写软件光栅化
/// （不能有 <c>libSkiaSharp</c>）；macOS/Linux 走 Avalonia，用上游的 OpenGL 后端。
/// 而 <see cref="GetMatrix4"/> 里的位姿数学是 fork 自己改过的（手臂/腿各自独立旋转、蹲下折骨架、
/// 关节挪到 MC 的位置）。这份数学一旦有两份，两个后端就会慢慢视觉漂移。
/// 所以把它放在零依赖的 <c>MinecraftSkinRender.Core</c> 里，两边共用同一份。</para>
///
/// <para>上游的 <c>SkinRender</c> 把位姿逻辑和 <c>SKBitmap</c> 贴图字段混在一个类里，
/// 这里按"是否依赖 SkiaSharp"切开：本类不含任何 SkiaSharp 类型；
/// <c>SkinRender</c>（Skia 侧）只负责贴图字段与 <c>SetSkinTex</c> / <c>SetCapeTex</c>。</para>
/// </summary>
public abstract class SkinRenderBase
{
    protected bool _enableCape;
    protected bool _enableTop;
    protected bool _switchModel;
    protected bool _switchSkin;
    protected bool _switchType;
    protected bool _switchBack;
    protected bool _animation;

    protected SkinRenderType _renderType;
    protected Vector4 _backColor;
    protected SkinType _skinType = SkinType.Unkonw;

    protected double _time;

    protected float _dis = 1;

    protected int _fps;

    protected Vector2 _rotXY;
    protected Vector2 _diffXY;

    protected Vector2 _xy;
    protected Vector2 _saveXY;
    protected Vector2 _lastXY;

    protected Matrix4x4 _last;

    protected readonly SkinAnimation _skina;

    /// <summary>
    /// 渲染出错
    /// </summary>
    public event Action<object?, ErrorType>? Error;
    /// <summary>
    /// 渲染状态改变
    /// </summary>
    public event Action<object?, StateType>? State;

    /// <summary>
    /// 是否存在披风
    /// </summary>
    public bool HaveCape { get; protected set; }
    /// <summary>
    /// 是否存在皮肤
    /// </summary>
    public bool HaveSkin { get; protected set; }

    /// <summary>
    /// 画布宽度
    /// </summary>
    public int Width { get; set; }
    /// <summary>
    /// 画布高度
    /// </summary>
    public int Height { get; set; }

    /// <summary>
    /// 渲染器信息
    /// </summary>
    public string Info { get; protected set; }

    /// <summary>
    /// 模型动画
    /// </summary>
    public bool Animation
    {
        get { return _animation; }
        set
        {
            if (value)
            {
                _skina.Run = true;
            }
            else
            {
                _skina.Run = false;
            }

            _animation = value;
        }
    }

    /// <summary>
    /// 皮肤类型
    /// </summary>
    public SkinType SkinType
    {
        get { return _skinType; }
        set
        {
            _skinType = value;
            _skina.SkinType = value;
            _switchModel = true;
        }
    }
    /// <summary>
    /// 背景色
    /// </summary>
    public Vector4 BackColor
    {
        get { return _backColor; }
        set
        {
            _backColor = value;
            _switchBack = true;
        }
    }

    /// <summary>
    /// 渲染类型
    /// </summary>
    public SkinRenderType RenderType
    {
        get { return _renderType; }
        set
        {
            _renderType = value;
            _switchType = true;
        }
    }

    /// <summary>
    /// 是否启用披风渲染
    /// </summary>
    public bool EnableCape
    {
        get { return _enableCape; }
        set
        {
            _enableCape = value;
            _switchType = true;
        }
    }
    /// <summary>
    /// 是否启用第二层渲染
    /// </summary>
    public bool EnableTop
    {
        get { return _enableTop; }
        set
        {
            _enableTop = value;
            _switchType = true;
        }
    }

    // NegiCraft fork. Poses such as the Minecraft crouch move both arms (or both legs) the same
    // way, which upstream could not express: it drove both sides from one shared vector and
    // negated it for the right side, so only an antiphase walk swing was reachable. Each side
    // is now its own input, and every part also takes an offset.
    //
    // Rotation keeps upstream's convention: X rolls about Z, Y pitches about X, Z yaws about Y,
    // and the value is divided by 360 before being used as radians. Offsets are model units
    // (8 skin pixels = 1 unit) with +Y up and +Z toward the viewer.

    public Vector3 HeadRotate { get; set; }
    public Vector3 LeftArmRotate { get; set; }
    public Vector3 RightArmRotate { get; set; }
    public Vector3 LeftLegRotate { get; set; }
    public Vector3 RightLegRotate { get; set; }
    public Vector3 BodyRotate { get; set; }

    // Elbow and knee. Only read when LimbJoints is on — with the limbs in one piece there is
    // nothing to bend. Left as plain inputs rather than wired into _skina, so the built-in walk
    // animation keeps its original single-box behaviour.
    public Vector3 LeftForeArmRotate { get; set; }
    public Vector3 RightForeArmRotate { get; set; }
    public Vector3 LeftLowerLegRotate { get; set; }
    public Vector3 RightLowerLegRotate { get; set; }

    public Vector3 BodyPos { get; set; }
    public Vector3 HeadPos { get; set; }
    public Vector3 LeftArmPos { get; set; }
    public Vector3 RightArmPos { get; set; }
    public Vector3 LeftLegPos { get; set; }
    public Vector3 RightLegPos { get; set; }

    // 两节"总关节"：骨盆和胸椎。骨架本来是完全扁平的 —— 每个部件一个独立矩阵，躯干转了四肢不跟，
    // 所以旧姿势（蹲 / 挥拳 / 被拎）都是手动把躯干的倾角抄进每条四肢的角度里凑出来的。
    // 扭胯那种"胯转过去、胸口反着回正"的动作用凑是凑不出来的，必须有真正的父子链。
    //
    // 挂法：骨盆 → 两条腿 + 胸椎；胸椎 → 躯干盒 + 头 + 两条胳膊。
    // ⚠️ 全 0 时这两节都是单位阵，所以现有姿势一个像素都不会变 —— 只有新动画会去动它们。
    public Vector3 HipRotate { get; set; }
    public Vector3 HipPos { get; set; }
    public Vector3 SpineRotate { get; set; }

    /// <summary>
    /// 是否在扭胯摆动时将脚部/小腿锁死在地面（小腿与脚底保持静止水平，不随骨盆平移与侧倾）。
    /// </summary>
    public bool GroundLockFeet { get; set; }

    /// <summary>小腿接地锁定时的基准大腿旋转（静止姿态）。</summary>
    public Vector3 LeftLegRestRotate { get; set; }
    public Vector3 RightLegRestRotate { get; set; }

    /// <summary>
    /// 四肢切成上下两段（肘 / 膝）。
    ///
    /// <para>开着时几何换的是 <see cref="Steve3DModel.GetSteve(SkinType, bool)"/> 的分段版本，
    /// 位姿矩阵换成下面 <see cref="Joint"/> / <see cref="Chain"/> 那条链；关着时一切照旧。
    /// OpenGL 后端只认单段四肢，所以它必须保持 false。</para>
    ///
    /// <para>⚠️ setter 置的是 <c>_switchModel</c> 而不是 <c>_switchType</c>：软件光栅重建网格只看
    /// <c>_switchModel</c>，写错的表现是"设了没反应"。各自另有网格缓存的后端覆写
    /// <see cref="OnLimbJointsChanged"/> 把自己的缓存清掉。</para>
    /// </summary>
    public bool LimbJoints
    {
        get { return _limbJoints; }
        set
        {
            if (_limbJoints == value) return;

            _limbJoints = value;
            _switchModel = true;
            OnLimbJointsChanged();
        }
    }

    private bool _limbJoints;

    /// <summary>四肢分段开了 / 关了。几何换了，缓存过网格的后端在这里把自己的缓存作废。</summary>
    protected virtual void OnLimbJointsChanged()
    {
    }

    // ---- 脊椎自由变形（FFD）----------------------------------------------------
    //
    // 躯干不再是"一个刚性盒子转一下"，而是按顶点高度施加递增的转角：越往上转得越多。
    // 这样腰才真的拧得起来，而不是整块胸甲原地旋转。侧弯（绕 Z）和前弯（绕 X）走同一条通道，
    // 所以扭和弯是同一个机制的两个分量。
    //
    // ⚠️ 只有开了 SpineFlexible 的后端会去逐顶点变形；GL 后端拿的还是没细分的整盒，
    // 对它来说这些角度恒为 0，画面上什么都没有。

    /// <summary>
    /// 躯干侧壁竖切几段。6 段在桌宠那个尺寸（模型约 110×171）下已经看不出阶梯；
    /// 每多一段就是躯干每层多 4 个面，两层合计多 8 个面。
    /// </summary>
    public const int SpineSegments = 6;

    /// <summary>
    /// 躯干改成细分网格 + 逐顶点变形。几何变了，和 <see cref="LimbJoints"/> 一样要走
    /// <c>_switchModel</c> 让后端重建网格。
    /// </summary>
    public bool SpineFlexible
    {
        get { return _spineFlexible; }
        set
        {
            if (_spineFlexible == value) return;

            _spineFlexible = value;
            _switchModel = true;
            OnSpineChanged();
        }
    }

    private bool _spineFlexible;

    protected virtual void OnSpineChanged()
    {
    }

    /// <summary>
    /// 从腰到肩累计的形变角，顺序和别的旋转入参一致：<b>X = 侧弯（绕 Z）/ Y = 前弯（绕 X）/
    /// Z = 扭转（绕 Y）</b>，单位同样是"弧度 × 360"。全 0 时逐顶点等于没动。
    /// </summary>
    public Vector3 SpineDeform { get; set; }

    /// <summary>脊椎转轴基点（躯干盒底面，也就是髋线）。</summary>
    protected static readonly Vector3 SpinePivot = new(0, -CubeModel.Value * 1.5f, 0);

    /// <summary>从髋线到肩线的高度（躯干 12 像素 + 往上到肩顶 2 像素）。</summary>
    protected const float SpineHeight = CubeModel.Value * 2.5f;

    /// <summary>
    /// 某个高度上的形变矩阵。<b>网格顶点和挂载点（肩、头）必须都走这一个函数</b> ——
    /// 分开算就会出现"胳膊跟着转了、肩头的皮还留在原地"。
    /// </summary>
    protected Matrix4x4 SpineMatrixAt(float y)
    {
        if (!_spineFlexible || SpineDeform == Vector3.Zero) return Matrix4x4.Identity;

        // 高度线性分配转角：髋线 t=0 完全不动，往上越来越拧。t 可以超过 1（肩顶以上转得最多）。
        var t = (y - SpinePivot.Y) / SpineHeight;
        if (t <= 0f) return Matrix4x4.Identity;

        return Matrix4x4.CreateTranslation(-SpinePivot)
             * Rotation(SpineDeform * t)
             * Matrix4x4.CreateTranslation(SpinePivot);
    }

    /// <summary>把一个躯干局部顶点按它自己的高度变形。</summary>
    protected Vector3 SpinePoint(Vector3 p) => Vector3.Transform(p, SpineMatrixAt(p.Y));

    // ---- 四肢自由变形（FFD）----------------------------------------------------
    //
    // 四肢保持整根盒子（不再拆分成两截割裂的独立刚体盒），沿 Y 轴竖切细分，并在关节处施加平滑过渡弯曲。
    // 足底保证 Roll=0 / Pitch=0 贴地，膝盖与肘部以连续平滑曲面自然过渡。

    public const int LimbSegments = 8;

    public bool LimbFlexible
    {
        get { return _limbFlexible; }
        set
        {
            if (_limbFlexible == value) return;

            _limbFlexible = value;
            _switchModel = true;
            OnLimbFlexibleChanged();
        }
    }

    private bool _limbFlexible;

    protected virtual void OnLimbFlexibleChanged()
    {
    }

    public LimbDeform LeftArmDeform = LimbDeform.Identity;
    public LimbDeform RightArmDeform = LimbDeform.Identity;
    public LimbDeform LeftLegDeform = LimbDeform.Identity;
    public LimbDeform RightLegDeform = LimbDeform.Identity;

    public ref readonly LimbDeform GetLimbDeform(ModelPartType part)
    {
        switch (part)
        {
            case ModelPartType.LeftArm: return ref LeftArmDeform;
            case ModelPartType.RightArm: return ref RightArmDeform;
            case ModelPartType.LeftLeg: return ref LeftLegDeform;
            case ModelPartType.RightLeg: return ref RightLegDeform;
            default: return ref LimbDeform.Identity;
        }
    }

    public Vector3 LimbPoint(ModelPartType part, Vector3 p)
    {
        if (!_limbFlexible) return p;

        ref readonly var deform = ref GetLimbDeform(part);
        if (deform.BendRotation == Quaternion.Identity && deform.Offset == Vector3.Zero)
            return p;

        var yTop = deform.TransitionTop;
        var yBot = deform.TransitionBottom;
        if (yTop <= yBot)
        {
            yTop = 0.25f;
            yBot = -0.25f;
        }

        float w;
        if (p.Y >= yTop)
        {
            w = 0f;
        }
        else if (p.Y <= yBot)
        {
            w = 1f;
        }
        else
        {
            var t = (yTop - p.Y) / (yTop - yBot);
            w = t * t * (3f - 2f * t);
        }

        if (w <= 0f) return p;

        var q = Quaternion.Slerp(Quaternion.Identity, deform.BendRotation, w);
        var offset = deform.Offset * w;

        return Vector3.Transform(p, q) + offset;
    }

    /// <summary>
    /// FPS刷新
    /// </summary>
    public event Action<object?, int>? FpsUpdate;

    protected SkinRenderBase()
    {
        _skina = new();
        _last = Matrix4x4.Identity;
    }

    /// <summary>
    /// 鼠标按下
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerPressed(KeyType type, Vector2 point)
    {
        if (type == KeyType.Left)
        {
            _diffXY.X = point.X;
            _diffXY.Y = -point.Y;
        }
        else if (type == KeyType.Right)
        {
            _lastXY.X = point.X;
            _lastXY.Y = point.Y;
        }
    }

    /// <summary>
    /// 鼠标松开
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerReleased(KeyType type, Vector2 point)
    {
        if (type == KeyType.Right)
        {
            _saveXY.X = _xy.X;
            _saveXY.Y = _xy.Y;
        }
    }

    /// <summary>
    /// 鼠标移动
    /// </summary>
    /// <param name="type"></param>
    /// <param name="point"></param>
    public void PointerMoved(KeyType type, Vector2 point)
    {
        if (type == KeyType.Left)
        {
            _rotXY.Y = point.X - _diffXY.X;
            _rotXY.X = point.Y + _diffXY.Y;
            _rotXY.Y *= 2;
            _rotXY.X *= 2;
            _diffXY.X = point.X;
            _diffXY.Y = -point.Y;
        }
        else if (type == KeyType.Right)
        {
            _xy.X = -(_lastXY.X - point.X) / 100 + _saveXY.X;
            _xy.Y = (_lastXY.Y - point.Y) / 100 + _saveXY.Y;
        }
    }

    /// <summary>
    /// 滚轮
    /// </summary>
    /// <param name="ispost"></param>
    public void PointerWheelChanged(bool ispost)
    {
        if (ispost)
        {
            _dis += 0.1f;
        }
        else
        {
            _dis -= 0.1f;
        }
    }

    /// <summary>
    /// 旋转
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void Rot(float x, float y)
    {
        _rotXY.X += x;
        _rotXY.Y += y;
    }

    /// <summary>
    /// 移动
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void Pos(float x, float y)
    {
        _xy.X += x;
        _xy.Y += y;
    }

    /// <summary>
    /// 设置绝对坐标
    /// </summary>
    /// <param name="x"></param>
    /// <param name="y"></param>
    public void SetPos(float x, float y)
    {
        _xy.X = x;
        _xy.Y = y;
    }

    /// <summary>
    /// 缩放
    /// </summary>
    /// <param name="x"></param>
    public void AddDis(float x)
    {
        _dis += x;
    }

    /// <summary>
    /// 重置模型
    /// </summary>
    public void ResetPos()
    {
        _dis = 1;
        _diffXY.X = 0;
        _diffXY.Y = 0;
        _xy.X = 0;
        _xy.Y = 0;
        _saveXY.X = 0;
        _saveXY.Y = 0;
        _lastXY.X = 0;
        _lastXY.Y = 0;
        _last = Matrix4x4.Identity;
        GroundLockFeet = false;
        LeftLegRestRotate = Vector3.Zero;
        RightLegRestRotate = Vector3.Zero;
    }

    /// <summary>
    /// 模型逻辑
    /// </summary>
    /// <param name="time"></param>
    public void Tick(double time)
    {
        if (_animation)
        {
            _skina.Tick(time);
        }

        if (_rotXY.X != 0 || _rotXY.Y != 0)
        {
            _last *= Matrix4x4.CreateRotationX(_rotXY.X / 360)
                    * Matrix4x4.CreateRotationY(_rotXY.Y / 360);
            _rotXY.X = 0;
            _rotXY.Y = 0;
        }

        _fps++;
        _time += time;
        if (_time > 1)
        {
            _time -= 1;
            FpsUpdate?.Invoke(this, _fps);
            _fps = 0;
        }
    }

    protected void OnErrorChange(ErrorType data)
    {
        Error?.Invoke(this, data);
    }

    protected void OnStateChange(StateType data)
    {
        State?.Invoke(this, data);
    }

    protected Matrix4x4 GetMatrix4(ModelPartType type)
    {
        var value = _skinType == SkinType.NewSlim ? 1.375f : 1.5f;
        bool enable = _animation;

        // The built-in walk animation drives both sides from one mirrored vector; manual posing
        // drives each side on its own.
        var head = enable ? _skina.Head : HeadRotate;
        var leftArm = enable ? _skina.Arm : LeftArmRotate;
        var rightArm = enable ? -_skina.Arm : RightArmRotate;
        var leftLeg = enable ? _skina.Leg : LeftLegRotate;
        var rightLeg = enable ? -_skina.Leg : RightLegRotate;

        // Part geometry from Steve3DModel is centred on its own origin. Each matrix places that
        // centre at `rest`, rotates about Minecraft's joint (`pivot`, measured from the centre),
        // and then shifts the whole part by its pose offset. Upstream pivoted the limbs at their
        // free end and put the pose offset before the rotation, which made a fold read as a fall.
        var armPivotX = value * CubeModel.Value;

        // With the limbs split in two, each segment's geometry is authored with its own joint at
        // the local origin instead, so the parent needs no `pivot` term at all — `rest` *is* the
        // joint's position. These are the same joints the single-piece cases above use, just
        // added together, so both modes describe one skeleton.
        var shoulderL = new Vector3(armPivotX, Steve3DModel.ShoulderY, 0);
        var shoulderR = new Vector3(-armPivotX, Steve3DModel.ShoulderY, 0);
        var hipL = new Vector3(CubeModel.Value * 0.5f, -CubeModel.Value * 3f + Steve3DModel.HipY, 0);
        var hipR = new Vector3(-CubeModel.Value * 0.5f, -CubeModel.Value * 3f + Steve3DModel.HipY, 0);

        // Elbow / knee, measured from the parent joint down the parent's own axis — so they ride
        // the parent's rotation. Joint-to-joint distances, hence independent of the overlay's
        // 1.125x inflation.
        var elbow = new Vector3(0, Steve3DModel.SplitY - Steve3DModel.ShoulderY, 0);
        var knee = new Vector3(0, Steve3DModel.SplitY - Steve3DModel.HipY, 0);

        var joints = _limbJoints;

        // 骨盆绕髋线转（就是两条腿挂上去那条高度），所以胯怎么扭，腿根的挂载点都不动。
        // 胸椎绕躯干自己的中心转 —— 它是"胸口反着回正"那一节，挂躯干盒、头、两条胳膊。
        // 两节在全 0 时都是单位阵（Pose 里 T(-pivot) 和 T(pivot) 抵消），所以不驱动就等于不存在。
        var pelvis = Pose(HipPos, new Vector3(0, -CubeModel.Value * 1.5f, 0), HipRotate, Vector3.Zero);
        var torso = Pose(Vector3.Zero, Vector3.Zero, BodyRotate, BodyPos);

        // 肩线（躯干局部 y = CubeModel.Value）正好是脊椎参考高度的 t=1，所以胳膊和头挂的
        // 就是"扭到头"的那一节；躯干网格自己的顶点按各自高度取同一族矩阵（SpineMatrixAt），
        // 两边共用一个函数，不会出现胳膊转了、肩头的皮没转。
        var chest = Pose(Vector3.Zero, Vector3.Zero, SpineRotate, Vector3.Zero)
                  * SpineMatrixAt(CubeModel.Value) * torso * pelvis;

        return type switch
        {
            ModelPartType.Body => torso * pelvis,
            ModelPartType.Head => Pose(
              new Vector3(0, CubeModel.Value * 2.5f, 0),
              new Vector3(0, -CubeModel.Value, 0), head, HeadPos) * chest,
            ModelPartType.LeftArm => (joints
              ? Joint(shoulderL, leftArm, LeftArmPos)
              : Pose(new Vector3(armPivotX, 0, 0),
                new Vector3(0, CubeModel.Value, 0), leftArm, LeftArmPos)) * chest,
            ModelPartType.RightArm => (joints
              ? Joint(shoulderR, rightArm, RightArmPos)
              : Pose(new Vector3(-armPivotX, 0, 0),
                new Vector3(0, CubeModel.Value, 0), rightArm, RightArmPos)) * chest,
            ModelPartType.LeftForeArm => joints
              ? Chain(elbow, LeftForeArmRotate, Joint(shoulderL, leftArm, LeftArmPos) * chest)
              : Matrix4x4.Identity,
            ModelPartType.RightForeArm => joints
              ? Chain(elbow, RightForeArmRotate, Joint(shoulderR, rightArm, RightArmPos) * chest)
              : Matrix4x4.Identity,
            ModelPartType.LeftLeg => (joints
              ? Joint(hipL, leftLeg, LeftLegPos)
              : Pose(new Vector3(CubeModel.Value * 0.5f, -CubeModel.Value * 3f, 0),
                new Vector3(0, CubeModel.Value * 1.5f, 0), leftLeg, LeftLegPos)) * pelvis,
            ModelPartType.RightLeg => (joints
              ? Joint(hipR, rightLeg, RightLegPos)
              : Pose(new Vector3(-CubeModel.Value * 0.5f, -CubeModel.Value * 3f, 0),
                new Vector3(0, CubeModel.Value * 1.5f, 0), rightLeg, RightLegPos)) * pelvis,
            ModelPartType.LeftLowerLeg => joints
              ? (GroundLockFeet
                  ? Rotation(LeftLowerLegRotate) * Matrix4x4.CreateTranslation(Vector3.Transform(knee, Joint(hipL, LeftLegRestRotate, LeftLegPos)))
                  : Chain(knee, LeftLowerLegRotate, Joint(hipL, leftLeg, LeftLegPos) * pelvis))
              : Matrix4x4.Identity,
            ModelPartType.RightLowerLeg => joints
              ? (GroundLockFeet
                  ? Rotation(RightLowerLegRotate) * Matrix4x4.CreateTranslation(Vector3.Transform(knee, Joint(hipR, RightLegRestRotate, RightLegPos)))
                  : Chain(knee, RightLowerLegRotate, Joint(hipR, rightLeg, RightLegPos) * pelvis))
              : Matrix4x4.Identity,
            ModelPartType.Proj => Matrix4x4.CreatePerspectiveFieldOfView(
              (float)(Math.PI / 4), (float)Width / Height, 0.1f, 10.0f),
            ModelPartType.View => Matrix4x4.CreateLookAt(new(0, 0, 7), new(), new(0, 1, 0)),
            ModelPartType.Model => _last
            * Matrix4x4.CreateTranslation(new(_xy.X, _xy.Y, 0))
            * Matrix4x4.CreateScale(_dis),
            ModelPartType.Cape => Matrix4x4.CreateTranslation(0, -2f * CubeModel.Value, -CubeModel.Value * 0.1f) *
               Matrix4x4.CreateRotationX((float)((enable ? 11.8 + _skina.Cape : 6.3) * Math.PI / 180)) *
               Matrix4x4.CreateTranslation(0, 1.6f * CubeModel.Value, -CubeModel.Value * 0.5f),
            _ => Matrix4x4.Identity
        };
    }

    // Matrices are applied left to right (System.Numerics row-vector order, uploaded to GLSL as
    // `M * v`), so this reads: move the geometry so its joint sits at the origin, rotate, then
    // put the joint where the part belongs.
    private static Matrix4x4 Pose(Vector3 rest, Vector3 pivot, Vector3 rotate, Vector3 offset) =>
        Matrix4x4.CreateTranslation(-pivot) *
        Rotation(rotate) *
        Matrix4x4.CreateTranslation(pivot + rest + offset);

    /// <summary>
    /// 一段四肢的父变换：几何已经把自己的关节放在局部原点了，所以只剩"绕原点转、再搬到关节该在的位置"。
    /// </summary>
    private static Matrix4x4 Joint(Vector3 rest, Vector3 rotate, Vector3 offset) =>
        Rotation(rotate) *
        Matrix4x4.CreateTranslation(rest + offset);

    /// <summary>
    /// 子段挂在父段下面：<b>先</b>绕自己的关节转，<b>再</b>落到父段局部帧里的关节位置，最后整个交给父段。
    ///
    /// <para>⚠️ 行向量约定下这条链读起来和"骨骼直觉"是反的。写成看着更自然的
    /// <c>parent * Rotation * Translation</c> 编译得过、也画得出来，但那个关节偏移就<b>不会</b>跟着父段
    /// 转 —— 表现是小臂像被钉在地上，胳膊一抬它就脱开。</para>
    /// </summary>
    private static Matrix4x4 Chain(Vector3 joint, Vector3 rotate, in Matrix4x4 parent) =>
        Rotation(rotate) *
        Matrix4x4.CreateTranslation(joint) *
        parent;

    public static Matrix4x4 Rotation(Vector3 rotate) =>
        Matrix4x4.CreateRotationZ(rotate.X / 360) *
        Matrix4x4.CreateRotationX(rotate.Y / 360) *
        Matrix4x4.CreateRotationY(rotate.Z / 360);
}

/// <summary>
/// 四肢自由形变参数（弯曲旋转 + 平移补偿 + 过渡区上下边界）。
/// </summary>
public struct LimbDeform : IEquatable<LimbDeform>
{
    public Quaternion BendRotation;
    public Vector3 Offset;
    public float TransitionTop;
    public float TransitionBottom;

    public LimbDeform()
    {
        BendRotation = Quaternion.Identity;
        Offset = Vector3.Zero;
        TransitionTop = 0.25f;
        TransitionBottom = -0.25f;
    }

    public static readonly LimbDeform Identity = new();

    public static LimbDeform Lerp(in LimbDeform a, in LimbDeform b, float t)
    {
        if (t <= 0f) return a;
        if (t >= 1f) return b;
        return new LimbDeform
        {
            BendRotation = Quaternion.Slerp(a.BendRotation, b.BendRotation, t),
            Offset = Vector3.Lerp(a.Offset, b.Offset, t),
            TransitionTop = a.TransitionTop + (b.TransitionTop - a.TransitionTop) * t,
            TransitionBottom = a.TransitionBottom + (b.TransitionBottom - a.TransitionBottom) * t
        };
    }

    public bool Equals(LimbDeform other) =>
        BendRotation == other.BendRotation &&
        Offset == other.Offset &&
        TransitionTop == other.TransitionTop &&
        TransitionBottom == other.TransitionBottom;

    public override bool Equals(object? obj) => obj is LimbDeform other && Equals(other);
    public override int GetHashCode() => HashCode.Combine(BendRotation, Offset, TransitionTop, TransitionBottom);
    public static bool operator ==(LimbDeform left, LimbDeform right) => left.Equals(right);
    public static bool operator !=(LimbDeform left, LimbDeform right) => !left.Equals(right);
}

