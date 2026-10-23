namespace NegiCraftLauncher.ViewModels;

/// <summary>
/// 界面动画的开法。三档。
///
/// <para><b>为什么要有这档设置</b>：动画引擎原本只读操作系统的"显示动画"开关
/// （Windows 的 <c>SystemParameters.ClientAreaAnimation</c>）。但那个开关管的是
/// <b>整个系统</b>的窗口动画 —— 很多人为了省电或看着不晃把它关掉，顺手就把应用内的
/// 过渡也一起关掉了，而应用内的过渡（页面滑入、浮层弹开）跟系统的窗口动画是两回事。
/// 有了这档设置，想让应用内动起来不必去动系统设置。</para>
///
/// <para>存成字符串而不是枚举值：以后加档位、或者别的版本写进来一个不认识的值，
/// 都能靠 <see cref="MotionPreferenceExtensions.Parse"/> 兜住（认不出的当"跟随系统"）。</para>
/// </summary>
public enum MotionPreference
{
    /// <summary>跟随系统的"显示动画"设置（默认）。</summary>
    System = 0,

    /// <summary>总是开 —— 不管系统设置是什么，应用内的动画都放。</summary>
    AlwaysOn = 1,

    /// <summary>总是关 —— 应用内一律瞬时落地。</summary>
    AlwaysOff = 2,
}

public static class MotionPreferenceExtensions
{
    /// <summary>
    /// 解析存下来的字符串。认不出的值退回 <see cref="MotionPreference.System"/>
    /// —— 存坏了不该让应用起不来。
    /// </summary>
    public static MotionPreference Parse(string? value) => value switch
    {
        "alwaysOn" => MotionPreference.AlwaysOn,
        "alwaysOff" => MotionPreference.AlwaysOff,
        _ => MotionPreference.System,
    };

    /// <summary>存进 settings.json 的形式。</summary>
    public static string ToStorage(this MotionPreference preference) => preference switch
    {
        MotionPreference.AlwaysOn => "alwaysOn",
        MotionPreference.AlwaysOff => "alwaysOff",
        _ => "system",
    };

    /// <summary>界面上显示的文案。</summary>
    public static string Label(this MotionPreference preference) => preference switch
    {
        MotionPreference.AlwaysOn => "总是开",
        MotionPreference.AlwaysOff => "总是关",
        _ => "跟随系统",
    };

    /// <summary>
    /// 这一档对应的覆盖值：<c>null</c> = 跟随系统，<c>true</c>/<c>false</c> = 强制开/关。
    ///
    /// <para><b>为什么用可空而不是直接给 bool</b>：跟随系统这一档必须**继续跟着系统走** ——
    /// 用户在应用开着的时候去改了 Windows 的"显示动画"，应用得跟上。所以"跟随"不是
    /// "把系统的当前值抄下来"，而是"别插手"。可空的覆盖值正好能表达这个区别。</para>
    ///
    /// <para><b>为什么"总是开"能盖过系统的"减少动态效果"</b>：那是个无障碍开关，
    /// 认真对待它的人是真的需要（动会引发不适）。所以默认档（跟随系统）老老实实听系统的；
    /// 只有用户**主动**选了"总是开"才覆盖它 —— 主动选择优先于默认值，但得是明确的主动。</para>
    ///
    /// <para>两端共用这一份判据：WPF 把它写进动画引擎的覆盖值，
    /// Avalonia 那边用它决定要不要起动画。</para>
    /// </summary>
    public static bool? Override(this MotionPreference preference) => preference switch
    {
        MotionPreference.AlwaysOn => true,
        MotionPreference.AlwaysOff => false,
        _ => null,
    };
}
