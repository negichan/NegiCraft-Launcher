using System;
using System.IO;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core.Settings;

public enum DownloadSource
{
    Official,
    Bmclapi,
    /// <summary>Probe both and keep whichever answered faster for this run.</summary>
    Auto,
}

/// <summary>
/// Everything the settings page edits, persisted as a single JSON document.
/// </summary>
public sealed class LauncherSettings
{
    public string GameRoot { get; set; } = NclPaths.DefaultGameRoot;

    /// <summary>Explicit javaw.exe; null means pick one automatically per version.</summary>
    public string? JavaPath { get; set; }

    public int MaxMemoryMb { get; set; } = 4096;

    public DownloadSource DownloadSource { get; set; } = DownloadSource.Auto;

    /// <summary>
    ///     Open download connections: shared across files, and a single large file is split into
    ///     at most this many ranged segments.
    /// </summary>
    public int DownloadThreads { get; set; } = 16;

    /// <summary>When true each instance gets its own saves/mods/config under versions/&lt;id&gt;/.</summary>
    public bool VersionIsolation { get; set; } = true;

    public bool HideOnLaunch { get; set; }

    /// <summary>
    /// What the close button does: "ask" prompts every time, "tray" hides the window to the
    /// notification area, "exit" shuts the launcher down. Kept as a code rather than the label
    /// the UI shows, so rewording a button never invalidates a saved config.
    /// </summary>
    public string CloseWindowBehavior { get; set; } = "ask";

    public bool IsDark { get; set; } = true;

    public string? CustomBackgroundPath { get; set; }
    public double BackgroundBlur { get; set; }
    public double BackgroundBrightness { get; set; }

    /// <summary>
    ///     自选壁纸的取景：平移与缩放。
    ///
    ///     <para><b><see cref="BackgroundPanX" /> / <see cref="BackgroundPanY" /> 的单位是"可平移范围的
    ///     百分比"</b>，不是像素也不是窗口尺寸：图片按 cover 铺满后比窗口大出来的那部分就是总余量，
    ///     ±100 恰好看到对侧边。这样换窗口大小、换缩放都自洽，而且参数域本身就限死了不会露边。</para>
    ///
    ///     <para><see cref="BackgroundZoom" /> 的 <c>100</c> 就是今天的"居中裁切铺满"。</para>
    ///
    ///     <para>图片与视频共用这一组参数（数学在 <c>Raster/BackgroundFrame.cs</c> 一份）。
    ///     内置生成场景不吃它 —— 那张是 <c>Fill</c> 硬拉伸的像素画，加分数缩放会让像素宽窄不均。</para>
    /// </summary>
    public double BackgroundPanX { get; set; }
    public double BackgroundPanY { get; set; }
    public double BackgroundZoom { get; set; } = 100;

    /// <summary>
    ///     自选壁纸的调色：对比度 / 饱和度 −100..100（0 = 不变），色相 −180..180 度。
    ///
    ///     <para>与 <see cref="BackgroundBrightness" /> 不同，这三个是色彩矩阵的参数，只在共享层
    ///     <c>Raster/ImageGrade.cs</c> 作用于自选图片的像素上；<b>视频与内置场景都不吃它</b>
    ///     （视频要等着色器那条路，内置场景保持作者调好的原样）。</para>
    /// </summary>
    public double BackgroundContrast { get; set; }
    public double BackgroundSaturation { get; set; }
    public double BackgroundHue { get; set; }

    /// <summary>
    ///     主页那块 3D 皮肤模型是否出现。<b>默认 <c>true</c></b> —— 它本来就一直站在主页上，
    ///     这个开关给的是"今天不想看见它"的出口，不是要改默认长相。
    ///
    ///     <para>关掉时 <see cref="HomeSkinModelX" /> / <see cref="HomeSkinModelY" /> 一起清成
    ///     <c>null</c>：看不见的时候留着一份偏移没有意义，重新打开要回到设计锚点。</para>
    /// </summary>
    public bool HomeSkinModelVisible { get; set; } = true;

    /// <summary>
    ///     用户把主页的 3D 模型拖到了哪儿。<b>单位是首页可用区宽高的比例</b>（0..1），不是像素 ——
    ///     主窗口可缩放（<c>MinWidth=1080</c>），存绝对坐标的话换个窗口大小或换个系统缩放，
    ///     小人就会压到右下停靠卡上或者跑出画面。
    ///
    ///     <para><c>null</c> ＝ "没拖过"，视图走设计锚点 <c>92,44</c>。主页预览有像素回归基准钉在
    ///     那个锚点上，所以 null 这条路一个像素都不许动。</para>
    /// </summary>
    public double? HomeSkinModelX { get; set; }
    public double? HomeSkinModelY { get; set; }

    /// <summary>
    ///     背景调节窗口的位置。
    ///
    ///     <para><see cref="BackgroundTuningX" /> / <see cref="BackgroundTuningY" /> 是屏幕坐标，
    ///     <b>null 表示"用户没拖过"</b> —— 那时由视图按"贴主窗口一侧、那一侧放不下就翻边"现算。
    ///     被拖过之后就不再自动挪：这类小窗每次开机换地方很烦。</para>
    ///
    ///     <para><see cref="BackgroundTuningLeft" /> 记的是上一次贴了哪一边，只作参考 ——
    ///     真正的依据是每次开窗时那侧的屏幕余量，因为窗口可能被拖到另一个显示器旁边。</para>
    /// </summary>
    public bool BackgroundTuningLeft { get; set; }
    public double? BackgroundTuningX { get; set; }
    public double? BackgroundTuningY { get; set; }

    /// <summary>
    ///     视频壁纸。与 <see cref="CustomBackgroundPath" /> 互斥 —— 两个都设了就按视频算。
    ///
    ///     <para>只有 Windows(WPF) 侧会播它：Avalonia 侧没有视频面，读到非空值等于没有背景，
    ///     会回落到生成图。所以这个字段跨平台共享，语义是"用户在这台机器上选了这段视频当背景"，
    ///     而不是"背景一定是视频"。</para>
    /// </summary>
    public string? VideoBackgroundPath { get; set; }

    /// <summary>
    ///     视频壁纸是否出声。<b>默认 <c>false</c>（静音）</b>。
    ///
    ///     <para>⚠️ 现在它只是 <see cref="VideoBackgroundVolume"/> 非 0 的别名 —— 静音就是音量 0，
    ///     没有第二个开关。留着写它有两个理由：旧版本读到这份文件时意思还对得上；
    ///     新版第一次读旧配置时照它压一次音量（"静音着但音量留着 60"是很常见的组合，
    ///     不压就会在更新之后第一次放背景视频时糊脸）。</para>
    ///
    ///     <para>跨平台共享：Avalonia 侧没有视频面，读到什么都不用管。</para>
    /// </summary>
    public bool VideoBackgroundSound { get; set; }

    /// <summary>
    ///     视频壁纸音量，<c>0</c>–<c>100</c>，<b>0 就是静音</b>。<b>默认 <c>0</c></b> ——
    ///     背景视频是替用户放着的，不是他主动点开播的，默认出声太唐突。
    ///
    ///     <para>跨平台共享：Avalonia 侧没有视频面，读到什么都不用管。</para>
    /// </summary>
    public int VideoBackgroundVolume { get; set; }

    public string? CurrentInstanceId { get; set; }

    /// <summary>桌宠独立自定义名称；为 null 或空时继承当前主账号名称。</summary>
    public string? PetCustomName { get; set; }

    /// <summary>
    ///     桌宠是否用 GPU 硬件渲染。
    ///
    ///     <para>只有 Windows(WPF) 侧有这条后端 —— 那边是把模型塞进 <c>Viewport3D</c> 交给显卡；
    ///     Avalonia 侧本来就走 OpenGL，读到了也无处可用，直接忽略即可。所以这个字段跨平台共享、
    ///     语义是"我在这台机器上要不要开硬件后端"。</para>
    ///
    ///     <para>默认 false：软件光栅化是回归基线（确定性、可离屏出图），GPU 是可选加速项。</para>
    /// </summary>
    public bool PetUseGpu { get; set; }

    public string EffectiveGameRoot =>
        string.IsNullOrWhiteSpace(GameRoot) ? NclPaths.DefaultGameRoot : GameRoot;

    [JsonIgnore]
    public string FilePath { get; private set; } = NclPaths.SettingsFile;

    public static LauncherSettings Load(string? path = null)
    {
        path ??= NclPaths.SettingsFile;
        LauncherSettings? loaded = null;
        if (File.Exists(path))
        {
            try
            {
                loaded = NclJson.Deserialize<LauncherSettings>(File.ReadAllText(path));
            }
            // A corrupt settings file must not stop the launcher from starting; fall back to defaults.
            catch (Exception)
            {
                loaded = null;
            }
        }

        var settings = loaded ?? new LauncherSettings();
        settings.FilePath = path;
        settings.Clamp();
        return settings;
    }

    public void Save()
    {
        Clamp();
        NclPaths.EnsureLauncherDirectories();
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, NclJson.Serialize(this));
    }

    private void Clamp()
    {
        MaxMemoryMb = Math.Clamp(MaxMemoryMb, 512, 65536);
        DownloadThreads = Math.Clamp(DownloadThreads, 1, 64);
        BackgroundBlur = Finite(BackgroundBlur, 0, 40, 0);
        BackgroundBrightness = Finite(BackgroundBrightness, -100, 100, 0);
        BackgroundPanX = Finite(BackgroundPanX, -100, 100, 0);
        BackgroundPanY = Finite(BackgroundPanY, -100, 100, 0);
        // 40..300：100 = cover 铺满，往下是"图比窗口小、外面露主题底色"。
        // 下限的真值在 Raster/BackgroundFrame.MinZoom（Core 不引 Raster，所以这里只能写数字 ——
        // 改那边记得改这里，两处都写了注释指认对方）。
        BackgroundZoom = Finite(BackgroundZoom, 40, 300, 100);
        BackgroundContrast = Finite(BackgroundContrast, -100, 100, 0);
        BackgroundSaturation = Finite(BackgroundSaturation, -100, 100, 0);
        BackgroundHue = Finite(BackgroundHue, -180, 180, 0);
        BackgroundTuningX = FiniteOrNull(BackgroundTuningX);
        BackgroundTuningY = FiniteOrNull(BackgroundTuningY);
        HomeSkinModelX = FractionOrNull(HomeSkinModelX);
        HomeSkinModelY = FractionOrNull(HomeSkinModelY);
    }

    /// <summary>
    /// 调节窗位置：非有限值退回 <c>null</c>（＝"没拖过，让视图自己算"），而不是退回 0 ——
    /// 屏幕坐标 0 是左上角，一个坏值会把窗口甩到屏幕外。
    /// </summary>
    private static double? FiniteOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? v : null;

    /// <summary>
    /// 主页模型的位置比例：非有限值退回 <c>null</c>（＝"没拖过"，走设计锚点），有限值夹进 0..1 ——
    /// 夹在范围外的手改配置会把小人甩到首页外面，再也拖不回来。
    /// </summary>
    private static double? FractionOrNull(double? value) =>
        value is { } v && double.IsFinite(v) ? Math.Clamp(v, 0.0, 1.0) : null;

    /// <summary>
    /// 背景这几个键会直接喂给变换矩阵和像素内核：<c>Math.Clamp(NaN,…)</c> 原样返回 NaN，
    /// 一旦漏进去，变换会 NaN 成"背景整个消失"、调色会 NaN 成全黑。所以非有限值一律回默认。
    /// </summary>
    private static double Finite(double value, double min, double max, double fallback) =>
        double.IsFinite(value) ? Math.Clamp(value, min, max) : fallback;
}
