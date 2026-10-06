namespace MinecraftSkinRender;

/// <summary>
/// 一个方块模型数据
/// </summary>
public record CubeModelItemObj
{
    public float[] Model;
    public ushort[] Point;
}

/// <summary>
/// 一个史蒂夫模型数据
/// </summary>
public record SteveModelObj
{
    public CubeModelItemObj Head;
    public CubeModelItemObj Body;
    public CubeModelItemObj LeftArm;
    public CubeModelItemObj RightArm;
    public CubeModelItemObj LeftLeg;
    public CubeModelItemObj RightLeg;
    public CubeModelItemObj Cape;

    // Lower limb segments. Null unless the model was built with jointed: true, which is what
    // lets the OpenGL backend keep drawing the single-piece limbs it knows how to draw.
    public CubeModelItemObj? LeftForeArm;
    public CubeModelItemObj? RightForeArm;
    public CubeModelItemObj? LeftLowerLeg;
    public CubeModelItemObj? RightLowerLeg;
}

/// <summary>
/// 模型贴图数据
/// </summary>
public record SteveTextureObj
{
    public float[] Head;
    public float[] Body;
    public float[] LeftArm;
    public float[] RightArm;
    public float[] LeftLeg;
    public float[] RightLeg;
    public float[] Cape;

    public float[]? LeftForeArm;
    public float[]? RightForeArm;
    public float[]? LeftLowerLeg;
    public float[]? RightLowerLeg;
}