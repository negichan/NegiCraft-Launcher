namespace MinecraftSkinRender;

/// <summary>
/// 生成史蒂夫模型
/// </summary>
public static class Steve3DModel
{
    // 四肢的骨架高度，单位是模型单位（8 皮肤像素 = 1 单位，CubeModel.Value = 0.5）。
    // 一条四肢的盒子在自己的中心坐标系里占 y ∈ [-0.75, +0.75]（12 像素）。原版把肩放在盒顶往下
    // 2 像素、把髋放在盒顶本身，肘和膝都在盒子自己的高度中点。
    //
    // 这几个数是位姿数学（SkinRenderBase.GetMatrix4）唯一要和几何对齐的量，所以公开在这里让 rig
    // 直接读 —— 同一份数值写两处迟早会漂。

    /// <summary>肩相对四肢中心的高度（盒顶往下 2 像素）。</summary>
    public const float ShoulderY = 0.5f;

    /// <summary>髋相对四肢中心的高度（就是盒顶）。</summary>
    public const float HipY = 0.75f;

    /// <summary>肘 / 膝的高度，也就是把四肢切成两段的那条线。</summary>
    public const float SplitY = 0f;

    /// <summary>1 个模型单位 = 8 个皮肤像素。</summary>
    public const float PixelsPerUnit = 8f;

    /// <summary>
    /// 两段在切口处各往对方那边多伸这么多（1 皮肤像素）。
    ///
    /// <para><b>为什么必须伸</b>：两个严丝合缝对接的盒子一弯，外侧就张开一个楔形口子、内侧互相顶掉，
    /// 看着就是"两截木棍硬接在一起"。重叠之后内侧那个角被填满，切口端面也整个藏进对方盒子里
    /// （顺带消掉了两个共面端面之间的深度冲突）。<b>外轮廓不变</b> —— 多伸的部分在 [-0.75, +0.75] 里面。</para>
    ///
    /// <para>UV 那边跟着搭过界，见 <c>Steve3DTexture.Limb</c>。</para>
    /// </summary>
    public const float JointOverlap = 0.125f;

    private const float LimbTop = 0.75f;
    private const float LimbBottom = -0.75f;

    /// <summary>
    /// 生成一个模型
    /// </summary>
    /// <param name="type">类型</param>
    /// <returns></returns>
    public static SteveModelObj GetSteve(SkinType type) => GetSteve(type, jointed: false);

    /// <summary>
    /// 生成一个模型。
    /// </summary>
    /// <param name="type">类型</param>
    /// <param name="jointed">
    /// 把胳膊和腿各切成上下两段（肘 / 膝）。开了之后 <c>LeftArm</c> / <c>LeftLeg</c> 这些原有字段
    /// 装的不再是整条四肢，而是<b>上段</b>，并且每一段的几何都按"自己的关节落在局部原点"来排 ——
    /// 必须配 <see cref="SkinRenderBase.LimbJoints"/> 一起用，否则矩阵会拿整条四肢的轴心去摆半条胳膊。
    /// 默认 false：OpenGL 后端按名字建 VAO，只认单段四肢，喂它两段会只画出上半截且不报错。
    /// </param>
    public static SteveModelObj GetSteve(SkinType type, bool jointed)
    {
        // Classic 和 slim 只差胳膊的宽度，Y 方向完全一样。
        var armX = type == SkinType.NewSlim ? 0.375f : 0.5f;

        var model = new SteveModelObj
        {
            Head = new()
            {
                Model = CubeModel.GetSquare(),
                Point = CubeModel.GetSquareIndicies()
            },
            Body = new()
            {
                Model = CubeModel.GetSquare(multiplyZ: 0.5f, multiplyY: 1.5f),
                Point = CubeModel.GetSquareIndicies()
            },
            Cape = new()
            {
                Model = CubeModel.GetSquare(
                    multiplyX: 1.25f,
                    multiplyZ: 0.1f,
                    multiplyY: 2f
                ),
                Point = CubeModel.GetSquareIndicies()
            }
        };

        if (jointed)
        {
            model.LeftArm = Segment(armX, ShoulderY, LimbTop, SplitY - JointOverlap);
            model.RightArm = Segment(armX, ShoulderY, LimbTop, SplitY - JointOverlap);
            model.LeftForeArm = Segment(armX, SplitY, SplitY + JointOverlap, LimbBottom);
            model.RightForeArm = Segment(armX, SplitY, SplitY + JointOverlap, LimbBottom);

            model.LeftLeg = Segment(0.5f, HipY, LimbTop, SplitY - JointOverlap);
            model.RightLeg = Segment(0.5f, HipY, LimbTop, SplitY - JointOverlap);
            model.LeftLowerLeg = Segment(0.5f, SplitY, SplitY + JointOverlap, LimbBottom);
            model.RightLowerLeg = Segment(0.5f, SplitY, SplitY + JointOverlap, LimbBottom);
        }
        else
        {
            model.LeftArm = WholeLimb(armX);
            model.RightArm = WholeLimb(armX);
            model.LeftLeg = WholeLimb(0.5f);
            model.RightLeg = WholeLimb(0.5f);
        }

        return model;
    }

    /// <summary>
    /// 生成第二层模型
    /// </summary>
    /// <param name="type">类型</param>
    /// <returns></returns>
    public static SteveModelObj GetSteveTop(SkinType type) => GetSteveTop(type, jointed: false);

    /// <summary>
    /// 生成第二层模型。
    /// </summary>
    /// <param name="type">类型</param>
    /// <param name="jointed">切两段，见 <see cref="GetSteve(SkinType, bool)"/>。</param>
    public static SteveModelObj GetSteveTop(SkinType type, bool jointed)
    {
        const float enlarge = 1.125f;

        var armX = type == SkinType.NewSlim ? 0.375f : 0.5f;

        var model = new SteveModelObj
        {
            Head = new()
            {
                Model = CubeModel.GetSquare(
                    enlarge: enlarge
                ),
                Point = CubeModel.GetSquareIndicies()
            }
        };

        // 老皮肤（64x32）的贴图里只有头部这一层覆盖。
        if (type == SkinType.Old) return model;

        model.Body = new()
        {
            Model = CubeModel.GetSquare(
                multiplyZ: 0.5f,
                multiplyY: 1.5f,
                enlarge: enlarge
            ),
            Point = CubeModel.GetSquareIndicies()
        };

        // 外套层是同心放大：盒子的上下边各往外挪了一点，但关节必须落在和本体层**同一个世界高度**上
        // （肩还是那个肩、肘还是那个肘），所以传给 Segment 的 jointY 两组完全一样，只有边长变了。
        // 这么切还有个好处：切分线 SplitY 正好是两层盒子共同的中截面，两段严丝合缝拼回原来的整盒。
        var top = LimbTop * enlarge;
        var bottom = LimbBottom * enlarge;

        if (jointed)
        {
            model.LeftArm = Segment(armX, ShoulderY, top, SplitY - JointOverlap, enlarge);
            model.RightArm = Segment(armX, ShoulderY, top, SplitY - JointOverlap, enlarge);
            model.LeftForeArm = Segment(armX, SplitY, SplitY + JointOverlap, bottom, enlarge);
            model.RightForeArm = Segment(armX, SplitY, SplitY + JointOverlap, bottom, enlarge);

            model.LeftLeg = Segment(0.5f, HipY, top, SplitY - JointOverlap, enlarge);
            model.RightLeg = Segment(0.5f, HipY, top, SplitY - JointOverlap, enlarge);
            model.LeftLowerLeg = Segment(0.5f, SplitY, SplitY + JointOverlap, bottom, enlarge);
            model.RightLowerLeg = Segment(0.5f, SplitY, SplitY + JointOverlap, bottom, enlarge);
        }
        else
        {
            model.LeftArm = WholeLimb(armX, enlarge);
            model.RightArm = WholeLimb(armX, enlarge);
            model.LeftLeg = WholeLimb(0.5f, enlarge);
            model.RightLeg = WholeLimb(0.5f, enlarge);
        }

        return model;
    }

    private static CubeModelItemObj WholeLimb(float multiplyX, float enlarge = 1f) => new()
    {
        Model = CubeModel.GetSquare(
            multiplyX: multiplyX,
            multiplyZ: 0.5f,
            multiplyY: 1.5f,
            enlarge: enlarge
        ),
        Point = CubeModel.GetSquareIndicies()
    };

    /// <summary>
    /// 切一段四肢：盒子夹在 <paramref name="fromY"/>（上边）和 <paramref name="toY"/>（下边）之间，
    /// 几何平移成 <paramref name="jointY"/> 落在局部原点。
    ///
    /// <para>上下两段共用"自己的关节当局部原点"这一个约定，父段转动时子段才跟得上
    /// （见 SkinRenderBase 里的 Chain）。所以上段传肩 / 髋，下段必须传<b>肘 / 膝</b>而不是父关节 ——
    /// 传错的表现是下半截胳膊凭空低 4 像素，而且不报错。</para>
    /// </summary>
    private static CubeModelItemObj Segment(
        float multiplyX, float jointY, float fromY, float toY, float enlarge = 1f)
    {
        // GetSquare 给的是 y = ±CubeModel.Value * enlarge * multiplyY + addY，反解出这一段的厚度。
        var multiplyY = (fromY - toY) / (2f * CubeModel.Value * enlarge);

        return new()
        {
            Model = CubeModel.GetSquare(
                multiplyX: multiplyX,
                multiplyY: multiplyY,
                multiplyZ: 0.5f,
                addY: (fromY + toY) / 2f - jointY,
                enlarge: enlarge
            ),
            Point = CubeModel.GetSquareIndicies()
        };
    }
}
