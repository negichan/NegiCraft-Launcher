using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace NegiCraftLauncher.Theme;

/// <summary>
/// 取负的 Thickness。目前只给 TextBox 的 PART_ContentHost 用。
///
/// <para>TextBoxBase 会把 <c>TextBox.Padding</c> 自己再塞一份到 content host 的
/// <c>Padding</c> 上（模板里写不写都一样，它每次都会覆盖），而模板又给整行铺了同样的
/// Padding —— 于是打字后的文字吃到两份、水印只吃一份，输入前后会往右跳一截。
/// 这里给 content host 一个负 Margin 把那一份抵消掉，视觉上只留模板的 Padding。</para>
/// </summary>
public sealed class NegateThicknessConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is Thickness t ? new Thickness(-t.Left, -t.Top, -t.Right, -t.Bottom) : new Thickness(0);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
