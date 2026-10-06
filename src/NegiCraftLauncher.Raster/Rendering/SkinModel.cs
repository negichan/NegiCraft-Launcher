using System.Numerics;
using MinecraftSkinRender;

namespace NegiCraftLauncher.Raster.Rendering;

/// <summary>
/// 一个要光栅化的面片：4 个模型空间顶点 + 各自绑定的 UV + 面法线（4 个顶点共用，平面着色）。
///
/// <para>顶点顺序是 <b>从面外侧看的 左上 → 右上 → 右下 → 左下</b>，也就是"从外侧看顺时针"。
/// 这是 <see cref="SoftwareRenderer"/> 的约定 —— <see cref="SkinModel"/> 负责把
/// <c>MinecraftSkinRender</c> 的原始顺序翻过来。</para>
/// </summary>
public readonly struct SkinQuad(
    Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3,
    Vector2 t0, Vector2 t1, Vector2 t2, Vector2 t3,
    Vector3 normal)
{
    public readonly Vector3 P0 = p0, P1 = p1, P2 = p2, P3 = p3;
    public readonly Vector2 T0 = t0, T1 = t1, T2 = t2, T3 = t3;
    public readonly Vector3 Normal = normal;
}

/// <summary>一个身体部件：它是什么、几何、以及贴图 UV 表。</summary>
public sealed record SkinPart(ModelPartType Type, float[] Model, float[] Uv);

/// <summary>
/// 把 <c>MinecraftSkinRender.Core</c> 的原始模型/UV 表转成 <see cref="SkinQuad"/> 列表。
///
/// <para><b>为什么要把顶点顺序翻过来</b>：上游的每个面按"从外侧看 左上→左下→右下→右上"给点
/// （它喂给 OpenGL，配合 <c>GL_CCW</c> 做正面判定）。本光栅器用的是相反的绕序，
/// 所以取 <c>v0, v3, v2, v1</c>，UV 跟着各自顶点走。翻错的话表现是<b>左右镜像、
/// 且第二层永远被本体挡住</b> —— 因为盒子的近端面和远端面投影到屏幕上形状一样，
/// 光看单张正面截图发现不了。</para>
///
/// <para>UV 表是按"纹素"给的，<c>GetTex</c> 里已经除过 64（老皮肤除 32），所以拿到的就是 0..1。
/// 老皮肤（64x32）的 V 按 32 归一化，采样时纹理高度要传 32。</para>
/// </summary>
public static class SkinModel
{
    /// <summary>
    /// 组装一层（本体或第二层）的所有部件。
    /// 老皮肤没有第二层，<c>GetSteveTop</c> 只会给出头部 —— 这里如实返回，不补齐。
    ///
    /// <para><paramref name="jointed"/> 把胳膊和腿各拆成上下两段（肘 / 膝）。不拆时那四个字段是
    /// null，<see cref="Add"/> 直接跳过，拿到的东西和以前逐字节一样 —— OpenGL 后端就走这条路。</para>
    ///
    /// <para><paramref name="spineSegments"/> 把躯干的四个侧壁各竖切这么多段，供逐顶点脊椎变形用
    /// （1 = 不切，保持原来的整盒）。切了之后躯干一个部件就有 <c>4×段数 + 2</c> 个面，
    /// 所以 <see cref="AppendQuads"/> 不能再假设"一个部件六个面"。</para>
    ///
    /// <para><paramref name="limbSegments"/> 把四肢的四个侧壁各竖切这么多段，供逐顶点四肢自由变形用
    /// （1 = 不切，保持原来的整盒）。只在未分段（<paramref name="jointed"/> == false）时生效。</para>
    /// </summary>
    public static List<SkinPart> Build(SkinType type, bool top, bool jointed = false, int spineSegments = 1, int limbSegments = 1)
    {
        var model = top ? Steve3DModel.GetSteveTop(type, jointed) : Steve3DModel.GetSteve(type, jointed);
        var texture = top ? Steve3DTexture.GetSteveTextureTop(type, jointed) : Steve3DTexture.GetSteveTexture(type, jointed);

        var parts = new List<SkinPart>(10);

        // 躯干的几何和 UV 必须一起切，而且切完仍是"一个部件"（只是面数变多），
        // 这样两个后端都只认一个 Body 矩阵、外加逐顶点变形，不用新增部件类型。
        var body = model.Body;
        if (spineSegments > 1 && body?.Model != null && texture.Body != null)
        {
            var (mesh, point, uv) = SpineMesh.Build(body.Model, texture.Body, spineSegments);
            body = new CubeModelItemObj { Model = mesh, Point = point };
            parts.Add(new SkinPart(ModelPartType.Body, body.Model, uv));
        }
        else
        {
            Add(ModelPartType.Body, body, texture.Body);
        }

        Add(ModelPartType.Head, model.Head, texture.Head);

        // 四肢在整段且启用自由形变时，四个侧壁竖切细分，供逐顶点弯曲
        AddLimb(ModelPartType.LeftArm, model.LeftArm, texture.LeftArm);
        AddLimb(ModelPartType.RightArm, model.RightArm, texture.RightArm);
        AddLimb(ModelPartType.LeftLeg, model.LeftLeg, texture.LeftLeg);
        AddLimb(ModelPartType.RightLeg, model.RightLeg, texture.RightLeg);

        Add(ModelPartType.Cape, model.Cape, texture.Cape);

        // 关节段的顺序跟着各自的父段走，别打乱 —— 本体层靠深度测试无所谓，
        // 但第二层是不写深度的混合绘制，同层的先后就是遮挡关系。
        Add(ModelPartType.LeftForeArm, model.LeftForeArm, texture.LeftForeArm);
        Add(ModelPartType.RightForeArm, model.RightForeArm, texture.RightForeArm);
        Add(ModelPartType.LeftLowerLeg, model.LeftLowerLeg, texture.LeftLowerLeg);
        Add(ModelPartType.RightLowerLeg, model.RightLowerLeg, texture.RightLowerLeg);

        return parts;

        void AddLimb(ModelPartType partType, CubeModelItemObj? part, float[]? uv)
        {
            if (part?.Model == null || uv == null) return;
            if (limbSegments > 1 && !jointed)
            {
                var (mesh, _, limbUv) = SpineMesh.Build(part.Model, uv, limbSegments);
                parts.Add(new SkinPart(partType, mesh, limbUv));
            }
            else
            {
                parts.Add(new SkinPart(partType, part.Model, uv));
            }
        }

        void Add(ModelPartType partType, CubeModelItemObj? part, float[]? uv)
        {
            // 老皮肤的第二层只有头；上游的 record 字段声明成非空但实际会给 null，别信。
            if (part?.Model == null || uv == null) return;
            parts.Add(new SkinPart(partType, part.Model, uv));
        }
    }

    /// <summary>把一个部件展开成面片（每面 2 个三角形由光栅器自己拆）。</summary>
    public static void AppendQuads(SkinPart part, List<SkinQuad> destination)
    {
        var pos = part.Model;
        var uv = part.Uv;

        // 面数按顶点数算：整盒是 6 个面，细分过的躯干会更多。
        for (var face = 0; face < pos.Length / 12; face++)
        {
            var v = face * 4;

            var p0 = Vertex(pos, v + 0);
            var p1 = Vertex(pos, v + 1);
            var p2 = Vertex(pos, v + 2);
            var p3 = Vertex(pos, v + 3);

            // 上游顺序 (p0,p1,p2,p3) = 左上,左下,右下,右上；本光栅器要 左上,右上,右下,左下。
            destination.Add(new SkinQuad(
                p0, p3, p2, p1,
                Uv(uv, v + 0), Uv(uv, v + 3), Uv(uv, v + 2), Uv(uv, v + 1),
                Normal(p0, p3, p2)));
        }
    }

    private static Vector3 Vertex(float[] positions, int index)
    {
        var i = index * 3;
        return new Vector3(positions[i], positions[i + 1], positions[i + 2]);
    }

    private static Vector2 Uv(float[] uvs, int index)
    {
        var i = index * 2;
        return new Vector2(uvs[i], uvs[i + 1]);
    }

    /// <summary>
    /// 面法线：从三个角现算，不再去 <c>CubeModel.Vertices</c> 里按下标取 ——
    /// 那个表只有 24 项（六个面），细分过的躯干面数超过它，按下标会越界。
    ///
    /// <para>目前光栅器是平光、也没有背面剔除，这个值实际没人读；留着是为了以后真上光照时
    /// 不用再改一遍调用点。</para>
    /// </summary>
    private static Vector3 Normal(Vector3 a, Vector3 b, Vector3 c)
    {
        var n = Vector3.Cross(b - a, c - a);
        return n.LengthSquared() > 1e-9f ? Vector3.Normalize(n) : Vector3.Zero;
    }
}
