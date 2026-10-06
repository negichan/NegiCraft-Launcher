using System.Numerics;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 动作系统的**唯一姿势载体**：一帧完整的角色姿势，只有渲染器真能表达的那些通道。
///
/// <para>这套通道是按 <see cref="SkinRenderBase"/> 的输入面定的，不是按任何外部动作格式定的
/// —— 外部格式（MMD 的 VMD、以后可能的 BVH / glTF 动画）都得先解算成这个形状才进得来。
/// 判据很简单：<b>渲染器画不出来的东西不算通道</b>，<b>画得出来的姿势都能用它写下来</b>。</para>
///
/// <para><b>全 0 = 站立</b>。这不是巧合而是契约：任何一条通道单独离开 0，画面就该只动它该动的部分。
/// 混合器因此可以按"谁离开 0"来判断叠加，不用给每个动作开特判。</para>
///
/// <para>骨架只有两节是真的父子链：<see cref="HipRotate"/>/<see cref="HipPos"/>（骨盆，挂两条腿和胸椎）
/// 和 <see cref="SpineRotate"/>（胸椎，挂躯干盒、头和两条胳膊）。其余部件都直接挂在骨盆系上。
/// 渲染器里那个 <c>BodyRotate</c> 是扁平骨架的遗留，<b>不带动任何部件</b>，所以没有收进来 ——
/// 新姿势一律走骨盆 / 胸椎。</para>
/// </summary>
public struct MotionPose
{
    // ---- 全局：站位与朝向 -------------------------------------------------------
    // 部件矩阵都在这之后才乘上，所以这两条是"整个人在屏幕上怎么摆"，不参与姿势叠加。

    /// <summary>整体站位偏移（模型单位，+Y 朝上）。桌宠用它做呼吸起伏和落地。</summary>
    public Vector2 BodyOffset;

    /// <summary>整体朝向（度）。渲染器入参那一套是"弧度 × 360"，这里存度数，施加时换算。</summary>
    public float BodyYawDeg;

    // ---- 两节总关节：骨盆与胸椎 -------------------------------------------------

    /// <summary>骨盆平移（模型单位）。腿是骨盆的孩子，所以这条会整体搬走两条腿。</summary>
    public Vector3 HipPos;

    /// <summary>骨盆朝向（渲染器入参：X=侧倾 / Y=前后 / Z=竖转，单位是弧度 × 360）。</summary>
    public Vector3 HipRotate;

    /// <summary>
    /// 胸椎（脊椎自由形变）从腰到肩**累计**的转角，顺序和别的旋转入参一致：
    /// X=侧弯 / Y=前弯 / Z=扭转。躯干网格顶点、肩和头的挂载点共用同一条曲线，
    /// 所以不会出现"上身转了、肩头那块皮没转"。
    /// </summary>
    public Vector3 SpineRotate;

    // ---- 头 ---------------------------------------------------------------------

    /// <summary>头的朝向（相对脖子）。视线跟随和舞蹈的头部动作都写这里。</summary>
    public Vector3 HeadRotate;

    /// <summary>头的位移偏移，蹲下时要把头从躯干顶上挪开。</summary>
    public Vector3 HeadPos;

    // ---- 四肢：刚体段 + 自由形变 ------------------------------------------------

    /// <summary>上臂朝向（左右各自独立，可以镜像也可以不对称）。</summary>
    public Vector3 LeftArmRotate, RightArmRotate;

    /// <summary>上臂位移偏移。</summary>
    public Vector3 LeftArmPos, RightArmPos;

    /// <summary>
    /// 胳膊的折弯（肘）。整根网格竖切后按高度加权，所以折出来是圆弧不是硬角。
    /// 施加顺序是<b>先形变、再件矩阵</b>，因此这个四元数表达在部件静止局部系里。
    /// </summary>
    public LimbDeform LeftArmDeform, RightArmDeform;

    /// <summary>大腿朝向（绕髋关节）。</summary>
    public Vector3 LeftLegRotate, RightLegRotate;

    /// <summary>大腿位移偏移。</summary>
    public Vector3 LeftLegPos, RightLegPos;

    /// <summary>腿的折弯（膝 + 脚），施加方式同上臂。</summary>
    public LimbDeform LeftLegDeform, RightLegDeform;

    /// <summary>站立姿势：所有通道都是默认值。</summary>
    public static readonly MotionPose Standing = default;

    /// <summary>把这条姿势施加到渲染器上。两端（WPF 软件光栅 / GPU）共用这一份，平台侧不再各写一遍。</summary>
    public readonly void ApplyTo(SkinRenderBase renderer)
    {
        renderer.BodyPos = Vector3.Zero;
        renderer.BodyRotate = Vector3.Zero;

        renderer.HipPos = HipPos;
        renderer.HipRotate = HipRotate;
        renderer.SpineDeform = SpineRotate;

        renderer.HeadRotate = HeadRotate;
        renderer.HeadPos = HeadPos;

        renderer.LeftArmRotate = LeftArmRotate;
        renderer.RightArmRotate = RightArmRotate;
        renderer.LeftArmPos = LeftArmPos;
        renderer.RightArmPos = RightArmPos;
        renderer.LeftArmDeform = LeftArmDeform;
        renderer.RightArmDeform = RightArmDeform;

        renderer.LeftLegRotate = LeftLegRotate;
        renderer.RightLegRotate = RightLegRotate;
        renderer.LeftLegPos = LeftLegPos;
        renderer.RightLegPos = RightLegPos;
        renderer.LeftLegDeform = LeftLegDeform;
        renderer.RightLegDeform = RightLegDeform;
    }
}
