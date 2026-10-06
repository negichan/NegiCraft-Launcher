namespace MinecraftSkinRender;

/// <summary>
/// 生成史蒂夫贴图UV数据
/// </summary>
public static class Steve3DTexture
{
    private static readonly float[] _headTex =
    [
        // back
        32f, 8f, 32f, 16f, 24f, 16f, 24f, 8f,
        // front
        8f, 8f, 8f, 16f, 16f, 16f, 16f, 8f,
        // left
        0f, 8f, 0f, 16f, 8f, 16f, 8f, 8f,
        // right
        16f, 8f, 16f, 16f, 24f, 16f, 24f, 8f,
        // top
        8f, 0f, 8f, 8f, 16f, 8f, 16f, 0f,
        // bottom
        24f, 0f, 24f, 8f, 16f, 8f, 16f, 0f
    ];

    private static readonly float[] _legArmTex =
    [
        // back
        12f, 4f, 12f, 16f, 16f, 16f, 16f, 4f,
        // front
        4f, 4f, 4f, 16f, 8f, 16f, 8f, 4f,
        // left
        0f, 4f, 0f, 16f, 4f, 16f, 4f, 4f,
        // right
        8f, 4f, 8f, 16f, 12f, 16f, 12f, 4f,
        // top
        4f, 0f, 4f, 4f, 8f, 4f, 8f, 0f,
        // bottom
        12f, 0f, 12f, 4f, 8f, 4f, 8f, 0f,
    ];

    private static readonly float[] _slimArmTex =
    [
        // back
        11f, 4f, 11f, 16f, 14f, 16f, 14f, 4f,
        // front
        4f, 4f, 4f, 16f, 7f, 16f, 7f, 4f,
        // left
        0f, 4f, 0f, 16f, 4f, 16f, 4f, 4f,
        // right
        7f, 4f, 7f, 16f, 11f, 16f, 11f, 4f,
        // top
        4f, 0f, 4f, 4f, 7f, 4f, 7f, 0f,
        // bottom
        10f, 0f, 10f, 4f, 7f, 4f, 7f, 0f,
    ];

    private static readonly float[] _bodyTex =
    [
        // back
        24f, 4f, 24f, 16f, 16f, 16f, 16f, 4f,
        // front
        4f, 4f, 4f, 16f, 12f, 16f, 12f, 4f,
        // left
        0f, 4f, 0f, 16f, 4f, 16f, 4f, 4f,
        // right
        12f, 4f, 12f, 16f, 16f, 16f, 16f, 4f,
        // top
        4f, 0f, 4f, 4f, 12f, 4f, 12f, 0f,
        // bottom
        20f, 0f, 20f, 4f, 12f, 4f, 12f, 0f
    ];

    private static readonly float[] _capeTex =
    [
        // back
        11f, 1f, 11f, 17f, 1f, 17f, 1f, 1f,
        // front
        12f, 1f, 12f, 17f, 22f, 17f, 22f, 1f,
        // left
        11f, 1f, 11f, 17f, 12f, 17f, 12f, 1f, 
        // right
        0f, 1f, 0f, 17f, 1f, 17f, 1f, 1f,
        // top
        1f, 0f,1f, 1f, 11f, 1f, 11f, 0f, 
        // bottom
        21f, 0f, 21f, 1f, 11f, 1f, 11f, 0f,
    ];

    /// <summary>
    /// 顶层数据
    /// </summary>
    /// <param name="type">类型</param>
    /// <returns></returns>
    public static SteveTextureObj GetSteveTextureTop(SkinType type) => GetSteveTextureTop(type, jointed: false);

    /// <summary>
    /// 顶层数据
    /// </summary>
    /// <param name="type">类型</param>
    /// <param name="jointed">四肢切两段，UV 跟着切，见 <see cref="LimbSegmentTex"/></param>
    /// <returns></returns>
    public static SteveTextureObj GetSteveTextureTop(SkinType type, bool jointed)
    {
        var armTex = type == SkinType.NewSlim ? _slimArmTex : _legArmTex;

        SteveTextureObj tex = new()
        {
            Head = GetTex(_headTex, type, 32f, 0f),
        };

        if (type != SkinType.Old)
        {
            tex.Body = GetTex(_bodyTex, type, 16f, 32f);
            tex.LeftArm = Limb(armTex, type, 48f, 48f, jointed, upper: true);
            tex.RightArm = Limb(armTex, type, 40f, 32f, jointed, upper: true);
            tex.LeftLeg = Limb(_legArmTex, type, 0f, 48f, jointed, upper: true);
            tex.RightLeg = Limb(_legArmTex, type, 0f, 32f, jointed, upper: true);

            if (jointed)
            {
                tex.LeftForeArm = Limb(armTex, type, 48f, 48f, jointed, upper: false);
                tex.RightForeArm = Limb(armTex, type, 40f, 32f, jointed, upper: false);
                tex.LeftLowerLeg = Limb(_legArmTex, type, 0f, 48f, jointed, upper: false);
                tex.RightLowerLeg = Limb(_legArmTex, type, 0f, 32f, jointed, upper: false);
            }
        }

        return tex;
    }

    /// <summary>
    /// 本体数据
    /// </summary>
    /// <param name="type">类型</param>
    /// <returns></returns>
    public static SteveTextureObj GetSteveTexture(SkinType type) => GetSteveTexture(type, jointed: false);

    /// <summary>
    /// 本体数据
    /// </summary>
    /// <param name="type">类型</param>
    /// <param name="jointed">四肢切两段，UV 跟着切，见 <see cref="LimbSegmentTex"/></param>
    /// <returns></returns>
    public static SteveTextureObj GetSteveTexture(SkinType type, bool jointed)
    {
        var armTex = type == SkinType.NewSlim ? _slimArmTex : _legArmTex;

        SteveTextureObj tex = new()
        {
            Head = GetTex(_headTex, type),
            Body = GetTex(_bodyTex, type, 16f, 16f),
            Cape = GetCapTex(_capeTex),
        };

        if (type == SkinType.Old)
        {
            // 老皮肤（64x32）里左右两侧用的是同一块贴图。
            tex.LeftArm = Limb(_legArmTex, type, 40f, 16f, jointed, upper: true);
            tex.RightArm = Limb(_legArmTex, type, 40f, 16f, jointed, upper: true);
            tex.LeftLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: true);
            tex.RightLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: true);

            if (jointed)
            {
                tex.LeftForeArm = Limb(_legArmTex, type, 40f, 16f, jointed, upper: false);
                tex.RightForeArm = Limb(_legArmTex, type, 40f, 16f, jointed, upper: false);
                tex.LeftLowerLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: false);
                tex.RightLowerLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: false);
            }
        }
        else
        {
            tex.LeftArm = Limb(armTex, type, 32f, 48f, jointed, upper: true);
            tex.RightArm = Limb(armTex, type, 40f, 16f, jointed, upper: true);
            tex.LeftLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: true);
            tex.RightLeg = Limb(_legArmTex, type, 16f, 48f, jointed, upper: true);

            if (jointed)
            {
                tex.LeftForeArm = Limb(armTex, type, 32f, 48f, jointed, upper: false);
                tex.RightForeArm = Limb(armTex, type, 40f, 16f, jointed, upper: false);
                tex.LeftLowerLeg = Limb(_legArmTex, type, 0f, 16f, jointed, upper: false);
                tex.RightLowerLeg = Limb(_legArmTex, type, 16f, 48f, jointed, upper: false);
            }
        }

        return tex;
    }

    private const int TopFace = 4;
    private const int BottomFace = 5;

    private static float[] Limb(float[] input, SkinType type, float offsetU, float offsetV,
        bool jointed, bool upper)
    {
        if (!jointed) return GetTex(input, type, offsetU, offsetV);

        // 两段在切口处各往对方那边多伸 JointOverlap，贴着的像素也跟着搭过界 ——
        // 于是弯起来时袖子的颜色是连续接上的，而不是在肘部断成两截。
        var ov = Steve3DModel.JointOverlap * Steve3DModel.PixelsPerUnit;

        return upper
            ? LimbSegmentTex(input, type, offsetU, offsetV, BandTop, BandSplit + ov, BottomFace)
            : LimbSegmentTex(input, type, offsetU, offsetV, BandSplit - ov, BandBottom, TopFace);
    }

    // 切分线在皮肤像素上：四肢四个侧面的 V 占 [4, 16]（12 像素 = 整条四肢），
    // 上下两半各 6 像素，切口在 V = 10。两个端面（top / bottom）在模板里占 V ∈ [0, 4]，
    // 那是真正的肩 / 手脚底，只有一头用得上。

    private const float BandTop = 4f;
    private const float BandSplit = 10f;
    private const float BandBottom = 16f;

    /// <summary>切口端面铺的是切分线往下这么几条像素带（皮肤里根本没有"手肘横截面"这块像素）。</summary>
    private const float JointStripHeight = 1f;

    /// <summary>端面在自己的模板里占的 V 带高度。</summary>
    private const float CapBandHeight = 4f;

    /// <summary>
    /// 把一条四肢的贴图模板切成一段。
    ///
    /// <para><b>改的是像素模板、不是归一化后的 UV</b> —— 处理完再交给 <see cref="GetTex"/> 去加原点、
    /// 除 64（老皮肤除 32）。这样 <see cref="SkinType.Old"/> 的归一化差异只有一处，不用为老皮肤特判。</para>
    ///
    /// <para><b>按面下标改、不能按值判</b>：侧面的 V=4 和端面的 V 带重叠，看值会连端面一起改错。
    /// 面序固定是 back, front, left, right, top, bottom。</para>
    ///
    /// <para><paramref name="vFrom"/> / <paramref name="vTo"/> 是这一段**上下两条边**各自对应的皮肤 V，
    /// 由调用方按几何的真实世界高度给 —— 两段在切口处故意重叠（见 Steve3DModel.JointOverlap），
    /// 所以这两条带是互相搭过界的，重叠那部分埋在另一段里面看不见，但**贴着的像素是真的**，
    /// 弯起来时袖子的颜色能连续接上，而不是在肘部断成两截。</para>
    /// </summary>
    private static float[] LimbSegmentTex(float[] input, SkinType type,
        float offsetU, float offsetV, float vFrom, float vTo, int capFace)
    {
        const int faceFloats = 8;

        var temp = (float[])input.Clone();

        // 四个侧面：整条四肢的 V ∈ [4,16] 线性映到这一段实际占的那条带。
        for (var face = 0; face < 4; face++)
        {
            for (var v = 1; v < faceFloats; v += 2)
            {
                var i = face * faceFloats + v;
                var t = (temp[i] - BandTop) / (BandBottom - BandTop);
                temp[i] = vFrom + t * (vTo - vFrom);
            }
        }

        // 切口那个端面：U 保持它自己的（那是袖子背面 / 正面各一列真像素，比糊一个纯色块像样），
        // 只把 V 压到切分线下方 1 像素那条带 —— 于是它显示的就是"肘部那一圈袖子"的颜色。
        if (capFace >= 0)
        {
            for (var v = 1; v < faceFloats; v += 2)
            {
                var i = capFace * faceFloats + v;
                temp[i] = BandSplit + temp[i] / CapBandHeight * JointStripHeight;
            }
        }

        return GetTex(temp, type, offsetU, offsetV);
    }

    /// <summary>
    /// 获取UV
    /// </summary>
    /// <param name="input"></param>
    /// <param name="type"></param>
    /// <param name="offsetU"></param>
    /// <param name="offsetV"></param>
    /// <returns></returns>
    public static float[] GetTex(float[] input, SkinType type,
        float offsetU = 0f,
        float offsetV = 0f)
    {
        var temp = new float[input.Length];
        for (int a = 0; a < input.Length; a++)
        {
            if (a % 2 == 0)
            {
                temp[a] = input[a] + offsetU;
            }
            else
            {
                temp[a] = input[a] + offsetV;
            }

            if (a % 2 != 0 && type == SkinType.Old)
            {
                temp[a] /= 32f;
            }
            else
            {
                temp[a] /= 64f;
            }
        }

        return temp;
    }

    /// <summary>
    /// 获取披风UV
    /// </summary>
    /// <param name="input"></param>
    /// <returns></returns>
    public static float[] GetCapTex(float[] input)
    {
        var temp = new float[input.Length];
        for (int a = 0; a < input.Length; a++)
        {
            temp[a] = input[a];
            if (a % 2 == 0)
            {
                temp[a] /= 64f;
            }
            else
            {
                temp[a] /= 32f;
            }
        }

        return temp;
    }
}
