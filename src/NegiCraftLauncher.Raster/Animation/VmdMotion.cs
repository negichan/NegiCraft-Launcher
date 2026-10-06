using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using MinecraftSkinRender;
using NegiCraftLauncher.Raster.Rendering;

namespace NegiCraftLauncher.Raster.Animation;

/// <summary>
/// MMD 的补间曲线：一条通道一组 (x1, y1, x2, y2)，字节 ÷127。
/// 曲线是三次贝塞尔 (0,0)→(x1,y1)→(x2,y2)→(1,1)，横轴是归一化时间、纵轴是插值比例。
/// </summary>
public readonly struct VmdCurveSet
{
    public readonly Vector4 PosX, PosY, PosZ, Rot;

    public VmdCurveSet(Vector4 posX, Vector4 posY, Vector4 posZ, Vector4 rot)
    {
        PosX = posX; PosY = posY; PosZ = posZ; Rot = rot;
    }

    /// <summary>全 127（= 1,1,1,1）就是直线，等价于不做缓动。</summary>
    public static readonly VmdCurveSet Linear = new(IdentityCurve(), IdentityCurve(), IdentityCurve(), IdentityCurve());

    private static Vector4 IdentityCurve() => new(1f, 1f, 1f, 1f);

    /// <summary>
    /// 给归一化时间 x，回带缓动的比例。先二分求出参数 t 使 x(t) = x，再取 y(t)。
    /// 15 次迭代、1e-5 收敛 —— 和 MMD 的口径一致（贝塞尔在这里不是单调函数族，牛顿法会飞出去）。
    /// </summary>
    public static float Apply(Vector4 c, float x)
    {
        if (x <= 0f) return 0f;
        if (x >= 1f) return 1f;

        var t = 0.5f;
        var step = 0.5f;
        for (var i = 0; i < 15; i++)
        {
            var s = 1f - t;
            var ft = 3f * s * s * t * c.X + 3f * s * t * t * c.Z + t * t * t - x;
            if (MathF.Abs(ft) < 1e-5f) break;
            step *= 0.5f;
            t += ft < 0f ? step : -step;
        }

        var q = 1f - t;
        return 3f * q * q * t * c.Y + 3f * q * t * t * c.W + t * t * t;
    }

    /// <summary>
    /// 读 VMD 的 64 字节补间表。用到的只有前 16 字节：位置 X/Y/Z 各一条、旋转一条，
    /// 每条的四个控制量分别在偏移 0 / 4 / 8 / 12（x1, y1, x2, y2）。
    /// </summary>
    public static VmdCurveSet Read(byte[] iv)
    {
        Vector4 Ch(int i) => new(iv[i] / 127f, iv[i + 4] / 127f, iv[i + 8] / 127f, iv[i + 12] / 127f);
        return new VmdCurveSet(Ch(0), Ch(1), Ch(2), Ch(3));
    }
}

/// <summary>
/// MMD 骨骼关键帧数据。
/// </summary>
public readonly struct VmdBoneKeyframe
{
    public readonly uint FrameIndex;
    public readonly Vector3 Position;
    public readonly Quaternion Rotation;

    /// <summary>这一帧**到达时**用的补间曲线（也就是"上一帧→这一帧"那一段）。</summary>
    public readonly VmdCurveSet Curves;

    public VmdBoneKeyframe(uint frameIndex, Vector3 position, Quaternion rotation, VmdCurveSet curves)
    {
        FrameIndex = frameIndex;
        Position = position;
        Rotation = rotation;
        Curves = curves;
    }
}

/// <summary>
/// 单根骨骼的关键帧时间轨。
/// </summary>
public sealed class VmdBoneTrack
{
    public string BoneName { get; }
    public List<VmdBoneKeyframe> Keyframes { get; } = new();

    public VmdBoneTrack(string boneName)
    {
        BoneName = boneName;
    }

    public void AddKeyframe(VmdBoneKeyframe kf) => Keyframes.Add(kf);

    public void SortKeyframes()
    {
        Keyframes.Sort((a, b) => a.FrameIndex.CompareTo(b.FrameIndex));
    }

    /// <summary>
    /// 在指定帧采样（按 MMD 的贝塞尔曲线做缓动：位置逐轴插值、旋转球面插值）。
    /// </summary>
    public (Vector3 Position, Quaternion Rotation) Sample(float frameIndex)
    {
        if (Keyframes.Count == 0) return (Vector3.Zero, Quaternion.Identity);
        if (Keyframes.Count == 1 || frameIndex <= Keyframes[0].FrameIndex)
            return (Keyframes[0].Position, Keyframes[0].Rotation);

        if (frameIndex >= Keyframes[^1].FrameIndex)
            return (Keyframes[^1].Position, Keyframes[^1].Rotation);

        // 二分查找目标区间
        int low = 0;
        int high = Keyframes.Count - 1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (Keyframes[mid].FrameIndex < frameIndex)
                low = mid + 1;
            else
                high = mid - 1;
        }

        int idxA = Math.Max(0, low - 1);
        int idxB = Math.Min(Keyframes.Count - 1, low);

        var a = Keyframes[idxA];
        var b = Keyframes[idxB];

        if (a.FrameIndex == b.FrameIndex) return (a.Position, a.Rotation);

        var span = b.FrameIndex - a.FrameIndex;
        // 相邻不到 1.5 帧（30fps）的两帧之间 MMD 不补间，直接停在起点 —— 表现成"顿一下"，
        // 舞蹈里的重拍就靠这个。少了这条，重拍会被摊平成匀速。
        var w = span <= 1 ? 0f : (frameIndex - a.FrameIndex) / span;
        // 曲线取区间**右端**那一帧的（three.js 的 MMDLoader 就是这么读的）。
        // 归属其实有争议（MMD 编辑器看着像挂在左端），但常见的舞蹈文件里每一帧的曲线是同一组字节
        // —— phuthon_1121 全文件都是 (20,20,107,107)，两种读法逐帧等价，所以这里不额外开开关。
        var c = b.Curves;

        var pos = new Vector3(
            Mix(a.Position.X, b.Position.X, VmdCurveSet.Apply(c.PosX, w)),
            Mix(a.Position.Y, b.Position.Y, VmdCurveSet.Apply(c.PosY, w)),
            Mix(a.Position.Z, b.Position.Z, VmdCurveSet.Apply(c.PosZ, w)));
        var rot = Quaternion.Slerp(a.Rotation, b.Rotation, VmdCurveSet.Apply(c.Rot, w));
        return (pos, rot);
    }

    private static float Mix(float a, float b, float t) => a + (b - a) * t;
}

/// <summary>
/// 计算完成并准备施加到渲染器的位姿数据。
/// </summary>
public struct VmdPose
{
    public Vector3 HipPos;
    public Vector3 HipRotate;
    public Vector3 SpineDeform;
    public Vector3 HeadRotate;
    public Vector3 LeftArmRotate;
    public Vector3 RightArmRotate;
    public LimbDeform LeftArmDeform;
    public LimbDeform RightArmDeform;
    public Vector3 LeftLegRotate;
    public Vector3 RightLegRotate;
    public LimbDeform LeftLegDeform;
    public LimbDeform RightLegDeform;
}

/// <summary>
/// 一条腿的解算过程量。<c>pet-vmd-probe</c> 直接打印它，用来把"腿解歪了"和"采样的帧不对"分开。
/// </summary>
public readonly struct VmdLegSolve
{
    /// <summary>true = 由脚部 IK 反解出来的；false = VMD 里本来就有 FK 腿部角度。</summary>
    public readonly bool FromIk;

    /// <summary>髋→踝直线 ÷ 静止腿长。1 = 完全伸直。</summary>
    public readonly float Extension;

    /// <summary>大腿偏离竖直的角度（度，往前摆为正）。</summary>
    public readonly float ThighDeg;

    /// <summary>膝盖折角（度）。</summary>
    public readonly float KneeDeg;

    /// <summary>踝目标在模型空间里相对髋的位置（单位：模型单位）。</summary>
    public readonly Vector3 AnkleMc;

    /// <summary>解出来的大腿指向（模型空间单位向量）。拿它和渲染器搭出来的方向对，就知道骨盆那层减干净了没有。</summary>
    public readonly Vector3 ThighDirMc;

    public VmdLegSolve(bool fromIk, float extension, float thighDeg, float kneeDeg, Vector3 ankleMc, Vector3 thighDirMc)
    {
        FromIk = fromIk;
        Extension = extension;
        ThighDeg = thighDeg;
        KneeDeg = kneeDeg;
        AnkleMc = ankleMc;
        ThighDirMc = thighDirMc;
    }

    public override string ToString() => FromIk
        ? $"ik ext={Extension:F3} thigh={ThighDeg:F1}deg knee={KneeDeg:F1}deg ankle=({AnkleMc.X:F2},{AnkleMc.Y:F2},{AnkleMc.Z:F2}) dir=({ThighDirMc.X:F2},{ThighDirMc.Y:F2},{ThighDirMc.Z:F2})"
        : $"fk thigh={ThighDeg:F1}deg knee={KneeDeg:F1}deg dir=({ThighDirMc.X:F2},{ThighDirMc.Y:F2},{ThighDirMc.Z:F2})";
}

/// <summary>骨盆的解算过程量。</summary>
public readonly struct VmdHipSolve
{
    /// <summary>MMD 世界系里骨盆被搬到的位置（全ての親 + センター + グルーブ 的位移和）。</summary>
    public readonly Vector3 MmdOffset;

    /// <summary>换算成模型单位后喂给渲染器的 HipPos。</summary>
    public readonly Vector3 HipPos;

    /// <summary>骨盆欧拉角（度，按渲染器入参 X=侧倾 / Y=前后 / Z=竖转 的顺序）。</summary>
    public readonly Vector3 RotateDeg;

    public VmdHipSolve(Vector3 mmdOffset, Vector3 hipPos, Vector3 rotateDeg)
    {
        MmdOffset = mmdOffset;
        HipPos = hipPos;
        RotateDeg = rotateDeg;
    }

    public override string ToString() =>
        $"mmd=({MmdOffset.X:F2},{MmdOffset.Y:F2},{MmdOffset.Z:F2}) " +
        $"pos=({HipPos.X:F3},{HipPos.Y:F3},{HipPos.Z:F3}) " +
        $"rot=({RotateDeg.X:F1},{RotateDeg.Y:F1},{RotateDeg.Z:F1})deg";
}

/// <summary>
/// MMD VMD 动画剪辑与重定向求解器。
/// 零依赖纯 C# 解析标准 .vmd 文件，并将 MMD 人形骨骼映射到 Minecraft 角色与自由形变 (FFD) 骨架上。
/// </summary>
public sealed class VmdMotionClip
{
    public string Name { get; set; } = string.Empty;
    public string ModelName { get; set; } = string.Empty;
    public double DurationSeconds { get; private set; }
    public uint MaxFrameIndex { get; private set; }

    public Dictionary<string, VmdBoneTrack> Tracks { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>上一次采样时左腿的解算过程量（调试探针读，不重算）。</summary>
    public VmdLegSolve LeftLegSolve { get; private set; }

    /// <summary>上一次采样时右腿的解算过程量。</summary>
    public VmdLegSolve RightLegSolve { get; private set; }

    /// <summary>上一次采样时骨盆的解算过程量。</summary>
    public VmdHipSolve HipSolve { get; private set; }

    // ---- 腿部重定向用的小骨架 ---------------------------------------------------
    //
    // 静止位置是**从 PMD 里读出来的真值**（初音ミク v2；design/_pmdcheck 那个探针打的），不是反推的：
    //   下半身(腰枢轴) (0, 13.243, -0.255)   ← 这是腰顶，不是胯线
    //   左足  (髋孔)   (0.797, 10.753, -0.073)  尾=左ひざ
    //   左ひざ (膝)    (0.674, 5.927, -0.053)   尾=左足首
    //   左足首 (踝)    (0.875, 1.340, 0.000)    尾=左つま先
    //   左つま先(脚尖) (0.929, 0.000, -2.464)
    // 模型自己还声明了 左足ＩＫ→末端=左足首、链=左ひざ→左足，以及 左つま先ＩＫ→末端=左つま先、链=左足首：
    // 腿是 IK 驱动的、脚的朝向归脚尖目标管，这两条都是写在模型里的，不用猜。
    //
    // ⚠️ 脚尖 Z = **-2.464** —— MMD 模型朝 -Z，而渲染器 +Z 才是镜头（View 的眼睛在 (0,0,7)）。
    // 所以换轴要翻 Z：位置乘 AxisMap、旋转做共轭。只翻一边就会出现"胯往一边转、腿往另一边倒"，
    // 而膝盖极性再顶反方向就成了反关节。
    //
    // 桌宠的骨架比例和 MMD 不一样（本项目腿 = 1.5 单位 / 全身 4 单位，MMD 腿 ≈ 9.41 / 18），
    // 照搬绝对位移一定错位，所以只搬两个无量纲量 —— 髋→踝"伸得多直"和"朝哪边倒"。
    private static readonly Vector3 MmdPelvisPivot = new(0f, 13.243f, -0.255f);
    private static readonly Vector3 MmdHipLeft = new(0.797f, 10.753f, -0.073f);
    private static readonly Vector3 MmdHipRight = new(-0.797f, 10.753f, -0.073f);
    private static readonly Vector3 MmdAnkleLeft = new(0.875f, 1.340f, 0f);
    private static readonly Vector3 MmdAnkleRight = new(-0.875f, 1.340f, 0f);
    private static readonly Vector3 MmdToeLeft = new(0.929f, 0f, -2.464f);
    private static readonly Vector3 MmdToeRight = new(-0.929f, 0f, -2.464f);

    // 本项目腿骨局部系：髋 +0.75 / 膝 0 / 脚底 -0.75（Steve3DModel.HipY、SplitY，CubeModel.Value = 0.5）。
    private const float McThigh = 0.75f;
    private const float McShin = 0.75f;
    private const float McSoleY = -0.75f;
    private static readonly Vector3 McDown = new(0f, -1f, 0f);

    private static readonly float MmdLegRest = Vector3.Distance(MmdHipLeft, MmdAnkleLeft);

    /// <summary>MMD 单位 → 模型单位。按腿长归一，骨盆平移和踝目标共用这一个比例，两边才会互相抵消。</summary>
    private static readonly float LegUnit = (McThigh + McShin) / MmdLegRest;

    /// <summary>
    /// MMD 世界系 → 渲染器世界系：翻 Z（MMD 的正面是 -Z，渲染器的正面是 +Z），X/Y 同向。
    /// 位置用 <c>p·AxisMap</c>，旋转共轭 <c>AxisMap⁻¹·M·AxisMap</c> —— 反射之下两者规律不同。
    /// </summary>
    private static readonly Matrix4x4 AxisMap = Matrix4x4.CreateScale(1f, 1f, -1f);

    /// <summary>膝盖的偏向：膝盖朝角色正面顶（模型空间 +Z = 朝镜头）。</summary>
    private static readonly Vector3 KneeBias = new(0f, 0f, 1f);

    /// <summary>
    /// 足首骨骼的静止基架：骨轴从踝指向脚尖，真值 (0.054,-1.340,-2.464)（MMD 系，朝下朝前）。
    /// 局部 Y 沿骨轴，所以把 (0,1,0) 打到该方向（换到模型系）就是它的静止基架。
    /// </summary>
    private static readonly Quaternion FootBasis = FromTo(
        Vector3.UnitY, Vector3.Normalize(MapPos(MmdToeLeft - MmdAnkleLeft)));

    private static readonly string[] BoneIkParentLeft = { "左足IK親", "左足IK", "Left leg ik parent" };
    private static readonly string[] BoneIkParentRight = { "右足IK親", "右足IK", "Right leg ik parent" };
    private static readonly string[] BoneIkFootLeft = { "左足ＩＫ", "左足IK", "Left leg IK" };
    private static readonly string[] BoneIkFootRight = { "右足ＩＫ", "右足IK", "Right leg IK" };
    private static readonly string[] BoneAnkleLeft = { "左足首", "Left ankle" };
    private static readonly string[] BoneAnkleRight = { "右足首", "Right ankle" };
    private static readonly string[] BoneToeIkLeft = { "左つま先ＩＫ", "左つま先IK", "Left toe IK" };
    private static readonly string[] BoneToeIkRight = { "右つま先ＩＫ", "右つま先IK", "Right toe IK" };
    private static readonly string[] BoneLegLeft = { "左足", "Left leg" };
    private static readonly string[] BoneLegRight = { "右足", "Right leg" };
    private static readonly string[] BoneKneeLeft = { "左ひざ", "Left knee" };
    private static readonly string[] BoneKneeRight = { "右ひざ", "Right knee" };

    private static readonly Encoding SjisEncoding = GetShiftJisEncoding();

    private static Encoding GetShiftJisEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding("shift-jis");
            }
            catch
            {
                return Encoding.UTF8;
            }
        }
    }

    /// <summary>
    /// 从文件加载 VMD 动画。
    /// </summary>
    public static VmdMotionClip Load(string filePath)
    {
        using var stream = File.OpenRead(filePath);
        return Read(stream, Path.GetFileNameWithoutExtension(filePath));
    }

    /// <summary>
    /// 从字节数组加载 VMD 动画。
    /// </summary>
    public static VmdMotionClip Load(byte[] data, string name = "")
    {
        using var stream = new MemoryStream(data);
        return Read(stream, name);
    }

    /// <summary>
    /// 从流解析 VMD 二进制结构。
    /// </summary>
    public static VmdMotionClip Read(Stream stream, string name = "")
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);

        // 1. 读取头部 (Header) 30 字节
        var headerBytes = reader.ReadBytes(30);
        var headerStr = Encoding.ASCII.GetString(headerBytes).TrimEnd('\0');
        if (!headerStr.StartsWith("Vocaloid Motion Data", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"无效的 VMD 文件：头部标识为 '{headerStr}'");
        }

        var clip = new VmdMotionClip { Name = name };

        // 2. 读取模型名 (Model Name) 20 字节
        var modelBytes = reader.ReadBytes(20);
        clip.ModelName = SjisEncoding.GetString(modelBytes).TrimEnd('\0');

        // 3. 读取骨骼关键帧数量
        if (stream.Position + 4 > stream.Length) return clip;
        var boneCount = reader.ReadUInt32();

        uint maxFrame = 0;
        for (uint i = 0; i < boneCount; i++)
        {
            var boneNameBytes = reader.ReadBytes(15);
            var boneName = SjisEncoding.GetString(boneNameBytes).TrimEnd('\0');

            var frameIndex = reader.ReadUInt32();
            var px = reader.ReadSingle();
            var py = reader.ReadSingle();
            var pz = reader.ReadSingle();

            var qx = reader.ReadSingle();
            var qy = reader.ReadSingle();
            var qz = reader.ReadSingle();
            var qw = reader.ReadSingle();

            // 64 字节插值曲线：位置 X/Y/Z 各一条 + 旋转一条
            var iv = reader.ReadBytes(64);
            var curves = VmdCurveSet.Read(iv);

            if (frameIndex > maxFrame) maxFrame = frameIndex;

            if (!clip.Tracks.TryGetValue(boneName, out var track))
            {
                track = new VmdBoneTrack(boneName);
                clip.Tracks[boneName] = track;
            }

            var rot = new Quaternion(qx, qy, qz, qw);
            if (rot.LengthSquared() < 1e-4f) rot = Quaternion.Identity;
            else rot = Quaternion.Normalize(rot);

            track.AddKeyframe(new VmdBoneKeyframe(frameIndex, new Vector3(px, py, pz), rot, curves));
        }

        clip.MaxFrameIndex = maxFrame;
        clip.DurationSeconds = maxFrame / 30.0;

        foreach (var track in clip.Tracks.Values)
        {
            track.SortKeyframes();
        }

        return clip;
    }

    /// <summary>
    /// 将四元数分解为渲染器所使用的欧拉角格式 (RadToRotateInput)。
    /// 匹配 SkinRenderBase.Rotation: Rz(z) * Rx(x) * Ry(y)。
    /// </summary>
    public static Vector3 QuaternionToRotateInput(Quaternion q) =>
        MatrixToRotateInput(Matrix4x4.CreateFromQuaternion(q));

    /// <summary>矩阵版：直接吃一个朝向矩阵（换轴共轭之后手上拿的就是矩阵，不必再绕回四元数）。</summary>
    public static Vector3 MatrixToRotateInput(Matrix4x4 m)
    {
        float sinX = Math.Clamp(-m.M32, -1.0f, 1.0f);
        float x = MathF.Asin(sinX);
        float cosX = MathF.Cos(x);

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

        return new Vector3(z * SkinPoseDriver.RadToRotateInput,
                           x * SkinPoseDriver.RadToRotateInput,
                           y * SkinPoseDriver.RadToRotateInput);
    }

    /// <summary>
    /// 这条轨道到底动没动。
    ///
    /// <para>⚠️ 不能按关键帧个数判：IK 驱动的舞蹈会给大腿/膝盖留一整串<b>单位四元数占位帧</b>
    /// （phuthon_1121 的 左足、左ひざ 各 25 帧全是单位四元数），按帧数判就成了"FK 已经动过"，
    /// 于是 IK 分支一次都跑不到，两条腿永远笔直。这里逐帧量它离静止值有多远。</para>
    /// </summary>
    private bool TrackMoves(params string[] boneNames)
    {
        // 单位四元数的 W = ±1；1-|W| ≈ θ²/8，1e-3 约合 8°，占位帧的浮点噪声远不到这个量。
        const float rotTol = 1e-3f;
        const float posTolSq = 1e-6f;

        foreach (var name in boneNames)
        {
            if (!Tracks.TryGetValue(name, out var track)) continue;
            foreach (var kf in track.Keyframes)
            {
                if (kf.Position.LengthSquared() > posTolSq) return true;
                if (1f - MathF.Abs(kf.Rotation.W) > rotTol) return true;
            }
        }
        return false;
    }

    private (Vector3 Pos, Quaternion Rot) SampleBone(float frame, params string[] boneNames)
    {
        foreach (var name in boneNames)
        {
            if (Tracks.TryGetValue(name, out var track))
            {
                return track.Sample(frame);
            }
        }
        return (Vector3.Zero, Quaternion.Identity);
    }

    // ---- 坐标系与旋转的小工具 ---------------------------------------------------
    //
    // ⚠️ **四元数和矩阵的乘序是反的**：System.Numerics 里 `Vector3.Transform(v, qa * qb)`
    // 实测是"先 qb 再 qa"，而 `v * (Ma * Mb)` 是"先 Ma 再 Mb"。同一套骨骼链写成四元数
    // 很容易反，所以这条链上全部改用矩阵（左到右 = 先左后右），只在喂给 LimbDeform 的那一刻转回四元数。
    // 下面每条式子都在临时工程里对着 Transform 逐条对拍过。

    /// <summary>位置换轴：p_mc = p · AxisMap。</summary>
    private static Vector3 MapPos(Vector3 p) => Vector3.Transform(p, AxisMap);

    /// <summary>旋转换轴：M_mc = AxisMap⁻¹ · M · AxisMap。</summary>
    private static Matrix4x4 MapRotM(Matrix4x4 m)
    {
        Matrix4x4.Invert(AxisMap, out var inv);
        return inv * m * AxisMap;
    }

    /// <summary>
    /// MMD 骨骼旋转 → 模型空间朝向矩阵。渲染器实际拿去搭部件的就是这一个，
    /// 所以喂欧拉角、和从部件朝向里减骨盆，都必须用同一份，不能一边共轭一边不。
    /// </summary>
    private static Matrix4x4 Mapped(Quaternion qMmd) => MapRotM(ToMatrix(qMmd));

    /// <summary>
    /// 把"骨骼局部系里的旋转"提到世界系：W = B⁻¹ · L · B，B 是这根骨的静止基架。
    /// 对拍过：绕骨轴 Y 转 θ，等于绕世界里的 B(Y) 方向转同样的 θ。
    /// </summary>
    private static Matrix4x4 ToWorldM(Matrix4x4 local, Quaternion basis)
    {
        var b = Matrix4x4.CreateFromQuaternion(basis);
        Matrix4x4.Invert(b, out var invB);
        return invB * local * b;
    }

    /// <summary>最小弧旋转：把单位向量 a 转到单位向量 b（两向量已归一）。</summary>
    private static Quaternion FromTo(Vector3 a, Vector3 b)
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

    private static Matrix4x4 ToMatrix(Quaternion q) => Matrix4x4.CreateFromQuaternion(q);

    /// <summary>两个<b>已归一</b>向量之间的夹角（度）。System.Numerics 没有 Vector3.Angle，自己 acos。</summary>
    private static float AngleDeg(Vector3 a, Vector3 b) =>
        MathF.Acos(Math.Clamp(Vector3.Dot(a, b), -1f, 1f)) * (180f / MathF.PI);

    /// <summary>
    /// 脚底钉回 IK 目标用的平移补偿。
    ///
    /// <para><see cref="SkinRenderBase.LimbPoint"/> 的形变是绕部件局部原点（= 膝线）转的，
    /// 叠上踮脚之后脚底会跟着画一段弧；<c>Offset</c> 和形变共用同一条权重曲线，
    /// 所以在脚底那一段正好能把落点平移回去 —— 和扭胯钉脚是同一个手法。</para>
    /// </summary>
    private static Vector3 SoleCorrection(Matrix4x4 bendFoldOnly, Matrix4x4 bendFinal)
    {
        var sole = new Vector3(0f, McSoleY, 0f);
        return Vector3.Transform(sole, bendFoldOnly) - Vector3.Transform(sole, bendFinal);
    }

    /// <summary>
    /// 按脚部 IK 目标反解一条两段连杆腿。
    /// 产出的是<b>模型空间</b>的大腿 / 小腿绝对朝向矩阵，骨盆那一层由调用方减掉。
    /// </summary>
    /// <summary>
    /// 足 IK 链在 MMD 世界系里的踝与脚尖位置。
    /// 链是 足IK親 →（足ＩＫ → 踝）／（つま先ＩＫ → 脚尖）—— PMD 里 左つま先ＩＫ 的爹就是 左足ＩＫ，
    /// 所以三层位移要按父子叠起来；足IK親 的旋转绕它自己站在地面上那点转（不然整只脚会绕模型原点甩）。
    /// </summary>
    private (Vector3 Ankle, Vector3 Toe) FootChainMmd(float frame, int side)
    {
        var ankleRest = side > 0 ? MmdAnkleLeft : MmdAnkleRight;
        var toeRest = side > 0 ? MmdToeLeft : MmdToeRight;
        var (ikParentPos, ikParentRot) = SampleBone(frame, side > 0 ? BoneIkParentLeft : BoneIkParentRight);
        var (ikFootPos, _) = SampleBone(frame, side > 0 ? BoneIkFootLeft : BoneIkFootRight);
        var (ikToePos, _) = SampleBone(frame, side > 0 ? BoneToeIkLeft : BoneToeIkRight);

        var pivot = new Vector3(ankleRest.X, 0f, ankleRest.Z);
        var shift = ikParentPos + ikFootPos;
        var ankle = pivot + Vector3.Transform(ankleRest - pivot, ikParentRot) + shift;
        var toe = pivot + Vector3.Transform(toeRest - pivot, ikParentRot) + shift + ikToePos;
        return (ankle, toe);
    }

    private VmdLegSolve SolveLegIk(
        float frame, int side, Vector3 hipTransMmd, Quaternion hipRot,
        out Matrix4x4 thighWorld, out Matrix4x4 shinWorld)
    {
        thighWorld = Matrix4x4.Identity;
        shinWorld = Matrix4x4.Identity;

        var hipRest = side > 0 ? MmdHipLeft : MmdHipRight;
        var ankleRest = side > 0 ? MmdAnkleLeft : MmdAnkleRight;
        var (ankleNow, _) = FootChainMmd(frame, side);

        // 髋孔绕骨盆自己的枢轴走；踝目标是世界系的 —— 足 IK 的爹挂在全ての親上，不跟骨盆转。
        var hipNow = hipTransMmd + MmdPelvisPivot + Vector3.Transform(hipRest - MmdPelvisPivot, hipRot);

        var dMmd = ankleNow - hipNow;
        var len = dMmd.Length();
        if (len < 1e-4f)
            return new VmdLegSolve(true, 1f, 0f, 0f, Vector3.Zero, McDown);

        // 只搬无量纲量：伸得多直 + 倒了多少。上限夹到 0.999 免得解退化（完全伸直时余弦定理要除 0）。
        var extension = Math.Clamp(len / MmdLegRest, 0.05f, 0.999f);
        var tilt = MapRotM(ToMatrix(FromTo(Vector3.Normalize(ankleRest - hipRest), Vector3.Normalize(dMmd))));
        var dirMc = Vector3.Normalize(Vector3.Transform(McDown, tilt));
        var target = dirMc * (extension * (McThigh + McShin));

        // 两段连杆：髋当原点，膝往 KneeBias 那一侧顶。
        var u = Vector3.Normalize(target);
        var dist = target.Length();
        var bias = KneeBias - u * Vector3.Dot(u, KneeBias);
        if (bias.LengthSquared() < 1e-6f) bias = Vector3.UnitX - u * Vector3.Dot(u, Vector3.UnitX);
        bias = Vector3.Normalize(bias);

        var cosPhi = Math.Clamp((dist * dist + McThigh * McThigh - McShin * McShin) / (2f * dist * McThigh), -1f, 1f);
        var thighDir = Vector3.Normalize(
            Vector3.Transform(u, Matrix4x4.CreateFromAxisAngle(Vector3.Cross(u, bias), MathF.Acos(cosPhi))));
        var knee = thighDir * McThigh;
        var shinDir = Vector3.Normalize(target - knee);

        thighWorld = ToMatrix(FromTo(McDown, thighDir));
        shinWorld = thighWorld * ToMatrix(FromTo(thighDir, shinDir));

        var thighSign = Vector3.Dot(thighDir - McDown, KneeBias) >= 0f ? 1f : -1f;
        return new VmdLegSolve(true, extension, AngleDeg(McDown, thighDir) * thighSign,
            AngleDeg(thighDir, shinDir), target, thighDir);
    }

    /// <summary>
    /// 在指定时间点采样并求解重定向姿势。
    /// </summary>
    public VmdPose Sample(double timeSeconds, bool loop = true)
    {
        var pose = new VmdPose();
        if (DurationSeconds <= 0) return pose;

        var sampleTime = timeSeconds;
        if (loop)
        {
            sampleTime %= DurationSeconds;
            if (sampleTime < 0) sampleTime += DurationSeconds;
        }
        else
        {
            sampleTime = Math.Clamp(sampleTime, 0, DurationSeconds);
        }

        var frame = (float)(sampleTime * 30.0);

        // 1. 根骨骼与骨盆：全ての親 / センター / グルーブ / 下半身
        var (posParent, rotParent) = SampleBone(frame, "全ての親", "Root", "master");
        var (posCenter, rotCenter) = SampleBone(frame, "センター", "Center");
        var (posGroove, rotGroove) = SampleBone(frame, "グルーブ", "Groove");
        var (_, rotLower) = SampleBone(frame, "下半身", "LowerBody", "pelvis");

        // 全ての親 的位移是整个世界的（足 IK 的爹也挂在它下面）—— 它搬全身，但**不改变髋和踝的
        // 相对位置**；センター / グルーブ 只搬上半身链，那才是要拿去和脚部 IK 目标对比的那一份。
        // 分错了的表现就是：全身落地下沉被解成膝盖猛弯。
        var hipLiftMmd = posCenter + posGroove;
        var totalPos = MapPos(posParent + hipLiftMmd) * LegUnit;
        var totalHipRot = rotParent * rotCenter * rotGroove * rotLower;

        // 平移按腿长归一（LegUnit），和下面踝目标用的是同一个比例 —— 只有两边同一个比例，
        // "胯抬起来、脚还钉在地上"才会自己抵消掉。
        // 旋转一律先过 Mapped()（翻 Z 的共轭），下面所有部件共用这一份。
        var hipRotMc = Mapped(totalHipRot);
        pose.HipPos = totalPos;
        pose.HipRotate = MatrixToRotateInput(hipRotMc);
        var hipRotInput = pose.HipRotate;
        HipSolve = new VmdHipSolve(hipLiftMmd, totalPos, new Vector3(
            hipRotInput.X / 360f * (180f / MathF.PI),
            hipRotInput.Y / 360f * (180f / MathF.PI),
            hipRotInput.Z / 360f * (180f / MathF.PI)));

        // 2. 脊椎与躯干：上半身 / 上半身2
        var (_, rotUpper) = SampleBone(frame, "上半身", "UpperBody", "spine");
        var (_, rotUpper2) = SampleBone(frame, "上半身2", "UpperBody2", "chest");
        var totalSpineRot = rotUpper * rotUpper2;
        pose.SpineDeform = MatrixToRotateInput(Mapped(totalSpineRot));

        // 3. 头部与颈部：首 / 頭
        var (_, rotNeck) = SampleBone(frame, "首", "Neck");
        var (_, rotHead) = SampleBone(frame, "頭", "Head");
        var totalHeadRot = rotNeck * rotHead;
        pose.HeadRotate = MatrixToRotateInput(Mapped(totalHeadRot));

        // 4. 左手臂与手肘
        // MMD 中左手臂参考姿势为水平向右 (+X)，Minecraft 中自然垂在身侧 (-Y)。
        // 因此需补一个绕 Z 轴 +90° (+π/2) 的参考帧旋转。
        var qTposeToMcArmL = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, MathF.PI / 2f);
        var qTposeToMcArmR = Quaternion.CreateFromAxisAngle(Vector3.UnitZ, -MathF.PI / 2f);

        var (_, rotShoulderP_L) = SampleBone(frame, "左肩P", "左肩親");
        var (_, rotShoulderL) = SampleBone(frame, "左肩", "Left shoulder");
        var (_, rotArmL) = SampleBone(frame, "左腕", "Left arm");
        var (_, rotArmTwistL) = SampleBone(frame, "左腕捩", "Left arm twist");
        var (_, rotElbowL) = SampleBone(frame, "左ひじ", "Left elbow");

        var finalArmL = rotShoulderP_L * rotShoulderL * rotArmL * rotArmTwistL * qTposeToMcArmL;
        pose.LeftArmRotate = MatrixToRotateInput(Mapped(finalArmL));
        pose.LeftArmDeform = new LimbDeform
        {
            BendRotation = Quaternion.CreateFromRotationMatrix(Mapped(rotElbowL)),
            Offset = Vector3.Zero,
            TransitionTop = 0.22f,
            TransitionBottom = -0.22f
        };

        // 5. 右手臂与手肘
        var (_, rotShoulderP_R) = SampleBone(frame, "右肩P", "右肩親");
        var (_, rotShoulderR) = SampleBone(frame, "右肩", "Right shoulder");
        var (_, rotArmR) = SampleBone(frame, "右腕", "Right arm");
        var (_, rotArmTwistR) = SampleBone(frame, "右腕捩", "Right arm twist");
        var (_, rotElbowR) = SampleBone(frame, "右ひじ", "Right elbow");

        var finalArmR = rotShoulderP_R * rotShoulderR * rotArmR * rotArmTwistR * qTposeToMcArmR;
        pose.RightArmRotate = MatrixToRotateInput(Mapped(finalArmR));
        pose.RightArmDeform = new LimbDeform
        {
            BendRotation = Quaternion.CreateFromRotationMatrix(Mapped(rotElbowR)),
            Offset = Vector3.Zero,
            TransitionTop = 0.22f,
            TransitionBottom = -0.22f
        };

        // 6/7. 两条腿：先判腿部 FK 到底动没动。没动就是脚部 IK 驱动的（舞蹈的常态），
        // 拿踝目标反解两段连杆；真动了才照搬骨骼角（和手臂同一套直白映射）。
        var legL = SolveLeg(frame, +1, hipLiftMmd, totalHipRot, hipRotMc, BoneLegLeft, BoneKneeLeft, BoneAnkleLeft);
        pose.LeftLegRotate = legL.Rotate;
        pose.LeftLegDeform = legL.Deform;
        LeftLegSolve = legL.Solve;

        var legR = SolveLeg(frame, -1, hipLiftMmd, totalHipRot, hipRotMc, BoneLegRight, BoneKneeRight, BoneAnkleRight);
        pose.RightLegRotate = legR.Rotate;
        pose.RightLegDeform = legR.Deform;
        RightLegSolve = legR.Solve;

        return pose;
    }

    /// <summary>一条腿的产出：渲染器入参的大腿角 + 膝/踝的自由形变 + 解算过程量。</summary>
    private readonly struct LegResult
    {
        public readonly Vector3 Rotate;
        public readonly LimbDeform Deform;
        public readonly VmdLegSolve Solve;

        public LegResult(Vector3 rotate, LimbDeform deform, VmdLegSolve solve)
        {
            Rotate = rotate;
            Deform = deform;
            Solve = solve;
        }
    }

    private LegResult SolveLeg(
        float frame, int side, Vector3 hipTransMmd, Quaternion hipRotMmd, Matrix4x4 pelvisMc,
        string[] legBone, string[] kneeBone, string[] ankleBone)
    {
        var (_, rotLeg) = SampleBone(frame, legBone);
        var (_, rotKnee) = SampleBone(frame, kneeBone);
        var (_, rotAnkle) = SampleBone(frame, ankleBone);

        Matrix4x4 thighW;
        Matrix4x4 shinW;
        VmdLegSolve solve;

        if (TrackMoves(legBone) || TrackMoves(kneeBone))
        {
            // FK 舞蹈：骨骼角直接当朝向用（和手臂同一条捷径 —— 不做骨骼局部系→世界的共轭）。
            thighW = Mapped(rotLeg);
            shinW = thighW * Mapped(rotKnee);

            var thighDir = Vector3.Transform(McDown, thighW);
            var shinDir = Vector3.Transform(McDown, shinW);
            var sign = Vector3.Dot(thighDir - McDown, KneeBias) >= 0f ? 1f : -1f;
            solve = new VmdLegSolve(false, 1f, AngleDeg(McDown, thighDir) * sign,
                AngleDeg(thighDir, shinDir), Vector3.Zero, thighDir);
        }
        else
        {
            solve = SolveLegIk(frame, side, hipTransMmd, hipRotMmd, out thighW, out shinW);
        }

        // 件矩阵那一路是"先形变、再大腿转、再骨盆"，全是矩阵乘（左到右 = 先左后右）：
        //   大腿入参 = T_w · P⁻¹ —— 减的是渲染器真正拿去搭骨盆的那一份，两边才 glued 得住
        //   bend     = S_w · T_w⁻¹（静止腿部局部系里的小腿折角）
        Matrix4x4.Invert(thighW, out var invThighW);
        Matrix4x4.Invert(pelvisMc, out var invPelvis);
        var rotate = MatrixToRotateInput(thighW * invPelvis);
        var bendFold = shinW * invThighW;

        // 叠在小腿之后 —— 注意得先乘出脚的绝对朝向再减 T_w，(S·T⁻¹)·A 和 S·A·T⁻¹ 不等价。
        var footRot = FootRotation(frame, side, rotAnkle);
        var bendFinal = (shinW * footRot) * invThighW;

        return new LegResult(rotate, new LimbDeform
        {
            BendRotation = Quaternion.CreateFromRotationMatrix(bendFinal),
            Offset = SoleCorrection(bendFold, bendFinal),
            TransitionTop = 0.12f,
            TransitionBottom = -0.58f
        }, solve);
    }

    /// <summary>
    /// "脚"该转多少（模型空间里叠在小腿之后的量）。
    ///
    /// <para>PMD 里写着 左つま先ＩＫ→末端=左つま先、链=左足首 —— 踝的朝向归脚尖目标管：足首 FK 转过去，
    /// IK 会把脚尖拽回它的目标，最后真正有效的信息是"脚尖目标指哪"。所以脚尖目标动过的动作按几何反推；
    /// 没动过的（纯 FK 摆脚）退回用 足首 骨骼角。</para>
    /// </summary>
    private Matrix4x4 FootRotation(float frame, int side, Quaternion rotAnkleMmd)
    {
        var toeBone = side > 0 ? BoneToeIkLeft : BoneToeIkRight;
        if (!TrackMoves(toeBone))
            return MapRotM(ToWorldM(ToMatrix(rotAnkleMmd), FootBasis));

        var ankleRest = side > 0 ? MmdAnkleLeft : MmdAnkleRight;
        var toeRest = side > 0 ? MmdToeLeft : MmdToeRight;
        var (ankleNow, toeNow) = FootChainMmd(frame, side);

        var restDir = Vector3.Normalize(MapPos(toeRest - ankleRest));
        var wantDir = Vector3.Normalize(MapPos(toeNow - ankleNow));

        // 只取"脚尖抬起来 / 压下去"那一份（绕侧向轴俯仰），**丢掉水平偏转**：
        // 重心转移会让脚尖目标相对踝横移 ~1.2 单位（≈26° 内扣），但方块腿的"脚"就是腿盒底面，
        // 绕竖轴拧它只会被读成膝盖转了一下 —— 腿的横移已经由踝目标体现在大腿倾角上了。
        var restElev = MathF.Asin(Math.Clamp(restDir.Y, -1f, 1f));
        var wantElev = MathF.Asin(Math.Clamp(wantDir.Y, -1f, 1f));
        var pitch = wantElev - restElev;
        if (MathF.Abs(pitch) < 1e-4f) return Matrix4x4.Identity;

        var axis = Vector3.Cross(restDir, Vector3.UnitY);
        if (axis.LengthSquared() < 1e-6f) axis = Vector3.UnitX;
        return Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(axis), -pitch);
    }
}
