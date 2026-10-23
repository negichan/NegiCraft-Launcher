namespace NegiCraftLauncher.ViewModels;

/// <summary>
/// 「所在地区」这块功能的开关 —— <b>目前只在 Debug 构建里开</b>
/// （花子 2026-10-23：「加入的地区功能，目前把它放在仅 debug 先」）。
///
/// <para><b>为什么是编译期常量而不是设置项</b>：它决定的是「发版到底带不带这个功能」，
/// 不是用户偏好，所以不该出现在 settings.json 里让人翻到。</para>
///
/// <para>写成 <c>const</c> 还有个实际好处：<c>if (RegionFeature.Enabled)</c> 在 Release 里
/// 会被编译器折成 <c>if (false)</c>，整段消掉 —— 探测那几次网络请求不会因为
/// "开关藏起来了但代码还在跑"而意外发出去。</para>
///
/// <para>正式开放时把下面的 <c>Enabled</c> 改成 <c>true</c>（或者删掉这个类、把三处引用
/// 粘回原样）即可，XAML 那边不用动。</para>
/// </summary>
public static class RegionFeature
{
#if DEBUG
    public const bool Enabled = true;
#else
    public const bool Enabled = false;
#endif
}
