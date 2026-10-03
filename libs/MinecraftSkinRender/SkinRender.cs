using SkiaSharp;

namespace MinecraftSkinRender;

/// <summary>
/// OpenGL 后端用的渲染器：在 <see cref="SkinRenderBase"/> 之上只补"贴图"这一件事。
///
/// <para>这里唯一被 SkiaSharp 拴住的地方就是贴图字段的类型（<c>SKBitmap</c>，因为 GL 上传要按
/// Skia 的色彩类型做通道翻转）。位姿、动画、鼠标交互、画布尺寸全在零依赖的基类里，
/// 所以 WPF 侧的软件光栅化后端能直接复用同一份数学，而不用把 SkiaSharp 拖进 Windows 包。</para>
/// </summary>
public abstract class SkinRender : SkinRenderBase
{
    /// <summary>
    /// 皮肤贴图
    /// </summary>
    protected SKBitmap? _skinTex;
    /// <summary>
    /// 披风贴图
    /// </summary>
    protected SKBitmap? _cape;

    /// <summary>
    /// 设置皮肤贴图
    /// </summary>
    /// <param name="skin"></param>
    /// <exception cref="Exception"></exception>
    public void SetSkinTex(SKBitmap? skin, SkinType? explicitType = null)
    {
        _skinTex?.Dispose();
        if (skin == null)
        {
            HaveSkin = false;
            return;
        }
        if (skin.Width != 64)
        {
            throw new Exception("This is not skin image");
        }

        _skinTex = skin;

        SkinType targetType;
        if (explicitType.HasValue && explicitType.Value != SkinType.Unkonw)
        {
            targetType = explicitType.Value;
        }
        else
        {
            targetType = SkinTypeChecker.GetTextType(skin);
        }

        if (targetType != SkinType.Unkonw)
        {
            _skinType = targetType;
            _skina.SkinType = targetType;
        }
        _switchSkin = true;
        _switchModel = true;
        HaveSkin = true;
    }

    /// <summary>
    /// 设置披风贴图
    /// </summary>
    /// <param name="cape"></param>
    public void SetCapeTex(SKBitmap? cape)
    {
        _cape = cape;
        if (cape == null)
        {
            HaveCape = false;
            return;
        }
        _switchSkin = true;
        HaveCape = true;
    }
}
