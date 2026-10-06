using System.Numerics;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 两段连杆腿的逆向解算：给"髋→踝该落在哪"，算出让脚掌正好踩在那上的大腿角度和膝盖折弯。
///
/// <para>这是**与动作格式无关**的一块。MMD 的脚部 IK、我们自己的扭胯、以后可能的手调关键帧，
/// 要的都是同一件事：骨盆怎么动、脚不许动 —— 于是腿必须自己找角度。以前这件事在
/// 各自算过一遍、近似程度还不一样，所以收到这里只留一份。</para>
///
/// <para>MMD 动作那条线现在放在 <c>spike/mmd-vmd-motion</c> 分支上备用；这个求解器和
/// <see cref="MotionPose"/> 留在主干，是为了那条线合回来时只是一份增量。</para>
///
/// <para><b>坐标口径</b>：模型空间，即骨盆施加之后的那一层 —— +X 是角色自身左侧、+Y 朝上、
/// +Z 朝镜头。髋当原点，踝目标是从髋量过去的向量。大腿朝向减掉骨盆这一步在这里做，
/// 调用方把"自己搭骨盆用的那份朝向矩阵"传进来（必须和渲染器实际施加的是同一份，否则腿会脱开骨盆）。</para>
/// </summary>
public static class LegIk
{
    // 本项目腿骨局部系：髋 +0.75 / 膝 0 / 脚底 -0.75（Steve3DModel.HipY、SplitY，CubeModel.Value = 0.5）。
    public const float ThighLen = 0.75f;
    public const float ShinLen = 0.75f;
    public const float SoleLocalY = -0.75f;
    public static readonly Vector3 Down = new(0f, -1f, 0f);

    /// <summary>膝盖偏向：膝盖朝角色正面顶。腿将直未直时决定不了什么，一蹲全看它。</summary>
    public static readonly Vector3 KneeBias = new(0f, 0f, 1f);

    private static readonly float FullLen = ThighLen + ShinLen;

    /// <summary>一条腿的解算结果，已经是渲染器入参那一层的量。</summary>
    public readonly struct Result
    {
        /// <summary>大腿角度（渲染器入参：X=侧倾 / Y=前后 / Z=竖转，单位弧度 × 360），已减掉骨盆。</summary>
        public readonly Vector3 ThighRotate;

        /// <summary>膝（和可选的脚）的折弯四元数，表达在腿部件的静止局部系里 —— 渲染器是先形变再件矩阵。</summary>
        public readonly Quaternion Bend;

        /// <summary>折弯带来的脚底漂移补偿：形变绕膝线转，加了踮脚就得用平移把落点按回去。</summary>
        public readonly Vector3 BendOffset;

        /// <summary>|髋→踝| ÷ 腿全长。1 = 完全伸直。</summary>
        public readonly float Extension;

        /// <summary>膝盖折角（度）。</summary>
        public readonly float KneeDeg;

        /// <summary>大腿指向（模型空间单位向量），留给探针核对。</summary>
        public readonly Vector3 ThighDir;

        /// <summary>传进来的踝目标原样带出，方便调用方打日志。</summary>
        public readonly Vector3 AnkleTarget;

        public Result(Vector3 thighRotate, Quaternion bend, Vector3 bendOffset,
                      float extension, float kneeDeg, Vector3 thighDir, Vector3 ankleTarget)
        {
            ThighRotate = thighRotate;
            Bend = bend;
            BendOffset = bendOffset;
            Extension = extension;
            KneeDeg = kneeDeg;
            ThighDir = thighDir;
            AnkleTarget = ankleTarget;
        }

        /// <summary>退化输入（目标长度≈0）：整条腿笔直向下。</summary>
        public static readonly Result Straight = new(
            default, Quaternion.Identity, Vector3.Zero, 1f, 0f, Down, Vector3.Zero);
    }

    /// <summary>
    /// 解一条腿。<paramref name="footRotation"/> 是叠在小腿之后的额外旋转（踮脚一类），
    /// 不需要就传单位矩阵；它是模型空间的量，所以得先乘出脚的绝对朝向再减大腿朝向 ——
    /// <c>(S·T⁻¹)·A</c> 和 <c>S·A·T⁻¹</c> 不等价。
    /// </summary>
    public static Result Solve(Vector3 hipToAnkle, Matrix4x4 pelvis, Matrix4x4 footRotation)
    {
        var len = hipToAnkle.Length();
        if (len < 1e-4f) return Result.Straight;

        // 夹到 0.999：完全伸直时余弦定理要除 0；而且"差一点点直"比"正好直"更像真人站姿。
        var extension = Math.Clamp(len / FullLen, 0.05f, 0.999f);
        var target = hipToAnkle * (extension * FullLen / len);
        var u = Vector3.Normalize(target);
        var dist = target.Length();

        // 膝盖偏移方向：把正面偏好投到垂直于 髋→踝 连线的平面上。
        var bias = KneeBias - u * Vector3.Dot(u, KneeBias);
        if (bias.LengthSquared() < 1e-6f) bias = Vector3.UnitX - u * Vector3.Dot(u, Vector3.UnitX);
        bias = Vector3.Normalize(bias);

        // 余弦定理解出大腿与 髋→踝 连线的夹角，绕 cross(u, bias) 正向转就把膝盖顶到了 bias 那一侧。
        var cosPhi = Math.Clamp(
            (dist * dist + ThighLen * ThighLen - ShinLen * ShinLen) / (2f * dist * ThighLen), -1f, 1f);
        var thighDir = Vector3.Normalize(
            Vector3.Transform(u, Matrix4x4.CreateFromAxisAngle(Vector3.Cross(u, bias), MathF.Acos(cosPhi))));
        var knee = thighDir * ThighLen;
        var shinDir = Vector3.Normalize(target - knee);

        // 绝对朝向（模型空间）：大腿 = 静止朝下转到 thighDir；小腿 = 大腿朝向之后叠一个折角。
        var thighW = ToMatrix(FromTo(Down, thighDir));
        var shinW = thighW * ToMatrix(FromTo(thighDir, shinDir));

        var res = FromOrientations(thighW, shinW, pelvis, footRotation);
        return new Result(res.ThighRotate, res.Bend, res.BendOffset,
            len / FullLen, AngleDeg(thighDir, shinDir), thighDir, hipToAnkle);
    }

    /// <summary>
    /// 已经知道两段绝对朝向时（FK 驱动的动作，或外部解算器给的角），只走后半段：
    /// 减骨盆、把折角换到腿部静止局部系、把脚底钉回目标。
    /// </summary>
    public static Result FromOrientations(Matrix4x4 thighW, Matrix4x4 shinW, Matrix4x4 pelvis, Matrix4x4 footRotation)
    {
        var thighDir = Vector3.Transform(Down, thighW);
        var shinDir = Vector3.Transform(Down, shinW);

        Matrix4x4.Invert(thighW, out var invThighW);
        Matrix4x4.Invert(pelvis, out var invPelvis);

        // 件矩阵那一路是"先形变、再大腿转、再骨盆"，所以：
        //   大腿入参 = T_w · P⁻¹   （减的必须是渲染器真正拿去搭骨盆的那一份，否则腿会脱开）
        //   bend     = S_w · T_w⁻¹ （静止腿部局部系里的折角）
        // 脚那一步得先乘出脚的绝对朝向再减 T_w —— (S·T⁻¹)·A 和 S·A·T⁻¹ 不等价。
        var bendFold = shinW * invThighW;
        var bendFinal = (shinW * footRotation) * invThighW;

        // 形变绕部件局部原点（= 膝线）转，所以叠了踮脚之后脚底会画弧；Offset 与形变共用同一条
        // 权重曲线，在脚底那一段正好能把落点平移回去。
        var sole = new Vector3(0f, SoleLocalY, 0f);

        return new Result(
            SkinPoseDriver.MatrixToRotateInput(thighW * invPelvis),
            Quaternion.CreateFromRotationMatrix(bendFinal),
            Vector3.Transform(sole, bendFold) - Vector3.Transform(sole, bendFinal),
            1f,
            AngleDeg(thighDir, shinDir),
            thighDir,
            Vector3.Zero);
    }

    // ---- 这条链的地基小工具，两边共用 --------------------------------------------
    //
    // ⚠️ System.Numerics 里**四元数乘序和矩阵乘序是反的**：Vector3.Transform(v, qa * qb) 实测
    // 是"先 qb 再 qa"，而 v * (Ma * Mb) 是"先 Ma 再 Mb"。这条链上全部用矩阵（左到右 = 先左后右），
    // 只在喂 LimbDeform 的那一刻转回四元数。

    /// <summary>最小弧旋转：把单位向量 a 转到单位向量 b（两向量需已归一）。</summary>
    public static Quaternion FromTo(Vector3 a, Vector3 b)
    {
        var d = Vector3.Dot(a, b);
        if (d > 0.999999f) return Quaternion.Identity;
        if (d < -0.999999f)
        {
            var perp = MathF.Abs(a.X) > 0.9f ? Vector3.UnitY : Vector3.UnitX;
            var axis = Vector3.Normalize(Vector3.Cross(a, perp));
            return new Quaternion(axis.X, axis.Y, axis.Z, 0f);
        }

        var c = Vector3.Cross(a, b);
        return Quaternion.Normalize(new Quaternion(c.X, c.Y, c.Z, 1f + d));
    }

    public static Matrix4x4 ToMatrix(Quaternion q) => Matrix4x4.CreateFromQuaternion(q);

    /// <summary>两个<b>已归一</b>向量之间的夹角（度）。System.Numerics 没有 Vector3.Angle。</summary>
    public static float AngleDeg(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * (180f / MathF.PI);
}
