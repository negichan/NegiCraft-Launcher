using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace NegiCraftLauncher.Pet.Wpf;

/// <summary>
/// 修改桌宠名字的模态小窗。<c>ShowAsync</c> 契约与 Avalonia 版（<c>Pet/PromptPetNameDialog.cs</c>）
/// 完全一致：返回 <c>null</c> = 取消，返回空串 = 恢复继承首页名字，返回非空串 = 设为专属名字。
///
/// <para>平台映射：<c>WindowDecorations=0</c> + <c>TransparencyLevelHint=Transparent</c>
/// → <c>WindowStyle=None</c> + <c>AllowsTransparency=True</c>；<c>BoxShadows</c> →
/// <see cref="DropShadowEffect"/>；<c>TextBox.PlaceholderText</c>（Avalonia 专有）→
/// 主题层的 <c>Negi.Placeholder</c> 附加属性（由 <c>Theme.Wpf/Controls.xaml</c> 的
/// TextBox 模板读取）。</para>
///
/// <para><b>非模态，与 Avalonia 版一致</b>：那边用 <c>dlg.Show(owner)</c> 而不是 <c>ShowDialog</c>，
/// 因为宿主桌宠窗口带 <c>WS_EX_NOACTIVATE</c>，模态窗口挂上去可能永远拿不到激活。
/// 结果通过 <see cref="TaskCompletionSource{TResult}"/> 回传，调用方照旧 <c>await</c>。</para>
/// </summary>
public class PromptPetNameDialog : Window
{
    private readonly TextBox _inputBox;
    private readonly TaskCompletionSource<string?> _tcs = new();

    public PromptPetNameDialog(string currentName)
    {
        Title = "修改桌宠名字";
        Width = 340;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ResizeMode = ResizeMode.NoResize;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;

        // **高度不能像 Avalonia 版那样写死 180**。Avalonia 那边内容恰好装得下 180，
        // 而 WPF 的默认字体（Segoe UI）行高比 Avalonia 的 Inter 大一截，
        // 同样的四段内容量出来约 199px —— 写死 180 的话最下面那排按钮会被裁掉一半，
        // 「确定」点不到（截图里只剩两个小色块）。交给 SizeToContent 自己算就不会错。
        SizeToContent = SizeToContent.Height;

        var root = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x11, 0x11, 0x14)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2b)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Effect = new DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 180.0 / 255.0, Color = Colors.Black },
            Padding = new Thickness(20),
            ClipToBounds = true,
        };

        var mainStack = new StackPanel();

        var titleBlock = new TextBlock
        {
            Text = "修改桌宠名字",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xfa, 0xfa, 0xfa)),
        };

        var hintBlock = new TextBlock
        {
            Text = "为桌面宠物设置专属名字（留空则恢复继承首页名字）：",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa)),
            TextWrapping = TextWrapping.Wrap,
        };

        _inputBox = new TextBox
        {
            Text = currentName,
            Height = 36,
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x21)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xfa, 0xfa, 0xfa)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2b)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0, 10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        // Avalonia 的 TextBox.PlaceholderText 在 WPF 里没有，走主题层的附加属性。
        Theme.Wpf.Negi.SetPlaceholder(_inputBox, "输入新名字，留空恢复继承");
        Theme.Wpf.Negi.SetCornerRadius(_inputBox, new CornerRadius(8));

        _inputBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Confirm();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                Cancel();
                e.Handled = true;
            }
        };

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 4, 0, 0),
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x1c, 0x1c, 0x21)),
            Foreground = new SolidColorBrush(Color.FromRgb(0xa1, 0xa1, 0xaa)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2b)),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        Theme.Wpf.Negi.SetCornerRadius(cancelBtn, new CornerRadius(8));
        cancelBtn.Click += (_, _) => Cancel();

        var confirmBtn = new Button
        {
            Content = "确定",
            Height = 32,
            Padding = new Thickness(16, 0, 16, 0),
            Background = new SolidColorBrush(Color.FromRgb(0xfa, 0xfa, 0xfa)),
            Foreground = new SolidColorBrush(Color.FromRgb(0x18, 0x18, 0x1b)),
            FontWeight = FontWeights.Medium,
            Cursor = Cursors.Hand,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Theme.Wpf.Negi.SetCornerRadius(confirmBtn, new CornerRadius(8));
        confirmBtn.Click += (_, _) => Confirm();

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);

        // Avalonia 的 StackPanel.Spacing=12 在 WPF 里没有，用 Margin 手动补。
        titleBlock.Margin = new Thickness(0, 0, 0, 12);
        hintBlock.Margin = new Thickness(0, 0, 0, 12);
        _inputBox.Margin = new Thickness(0, 0, 0, 12);

        mainStack.Children.Add(titleBlock);
        mainStack.Children.Add(hintBlock);
        mainStack.Children.Add(_inputBox);
        mainStack.Children.Add(btnPanel);

        root.Child = mainStack;
        Content = root;

        Loaded += (_, _) =>
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
            {
                _inputBox.Focus();
                _inputBox.SelectAll();
            });
        };

        Closed += (_, _) =>
        {
            if (!_tcs.Task.IsCompleted)
            {
                _tcs.TrySetResult(null);
            }
        };
    }

    private void Confirm()
    {
        _tcs.TrySetResult(_inputBox.Text ?? "");
        Close();
    }

    private void Cancel()
    {
        _tcs.TrySetResult(null);
        Close();
    }

    /// <summary>
    /// 与 Avalonia 版同名同签名。非模态显示，返回的 Task 在用户确定/取消/关窗时完成。
    /// </summary>
    public static Task<string?> ShowAsync(Window? owner, string currentName)
    {
        var dlg = new PromptPetNameDialog(currentName);

        // 桌宠窗口带 WS_EX_NOACTIVATE：给它当 Owner 会让本窗也继承"不被激活"的行为，
        // 所以只在宿主是普通可激活窗口时才挂 Owner。
        if (owner is { IsVisible: true } && (owner.ShowActivated || owner.IsActive))
        {
            dlg.Owner = owner;
        }

        dlg.Show();
        return dlg._tcs.Task;
    }
}
