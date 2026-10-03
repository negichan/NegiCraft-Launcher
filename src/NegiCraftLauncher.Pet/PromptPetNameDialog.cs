using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace NegiCraftLauncher.Pet;

public class PromptPetNameDialog : Window
{
    private readonly TextBox _inputBox;
    private readonly TaskCompletionSource<string?> _tcs = new();

    public PromptPetNameDialog(string currentName)
    {
        Title = "修改桌宠名字";
        Width = 340;
        Height = 180;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        CanResize = false;
        WindowDecorations = 0;
        Background = Brushes.Transparent;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };

        var root = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#111114")),
            BorderBrush = new SolidColorBrush(Color.Parse("#26262b")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 30, Spread = -4, Color = Color.FromArgb(180, 0, 0, 0) }),
            Padding = new Thickness(20),
            ClipToBounds = true
        };

        var mainStack = new StackPanel
        {
            Spacing = 12
        };

        var titleBlock = new TextBlock
        {
            Text = "修改桌宠名字",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Foreground = new SolidColorBrush(Color.Parse("#fafafa"))
        };

        var hintBlock = new TextBlock
        {
            Text = "为桌面宠物设置专属名字（留空则恢复继承首页名字）：",
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.Parse("#a1a1aa")),
            TextWrapping = TextWrapping.Wrap
        };

        _inputBox = new TextBox
        {
            Text = currentName,
            Height = 36,
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#1c1c21")),
            Foreground = new SolidColorBrush(Color.Parse("#fafafa")),
            BorderBrush = new SolidColorBrush(Color.Parse("#26262b")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(10, 0),
            VerticalContentAlignment = VerticalAlignment.Center,
            PlaceholderText = "输入新名字，留空恢复继承"
        };

        _inputBox.KeyDown += (s, e) =>
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
            Spacing = 8,
            Margin = new Thickness(0, 4, 0, 0)
        };

        var cancelBtn = new Button
        {
            Content = "取消",
            Height = 32,
            Padding = new Thickness(14, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#1c1c21")),
            Foreground = new SolidColorBrush(Color.Parse("#a1a1aa")),
            BorderBrush = new SolidColorBrush(Color.Parse("#26262b")),
            BorderThickness = new Thickness(1),
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        cancelBtn.Click += (_, _) => Cancel();

        var confirmBtn = new Button
        {
            Content = "确定",
            Height = 32,
            Padding = new Thickness(16, 0),
            CornerRadius = new CornerRadius(8),
            Background = new SolidColorBrush(Color.Parse("#fafafa")),
            Foreground = new SolidColorBrush(Color.Parse("#18181b")),
            FontWeight = FontWeight.Medium,
            Cursor = new Cursor(StandardCursorType.Hand),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        confirmBtn.Click += (_, _) => Confirm();

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(confirmBtn);

        mainStack.Children.Add(titleBlock);
        mainStack.Children.Add(hintBlock);
        mainStack.Children.Add(_inputBox);
        mainStack.Children.Add(btnPanel);

        root.Child = mainStack;
        Content = root;

        Loaded += (_, _) =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                _inputBox.Focus();
                _inputBox.SelectAll();
            }, DispatcherPriority.Input);
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

    public static Task<string?> ShowAsync(Window? owner, string currentName)
    {
        var dlg = new PromptPetNameDialog(currentName);
        if (owner != null && owner.IsVisible)
        {
            dlg.Show(owner);
        }
        else
        {
            dlg.Show();
        }
        return dlg._tcs.Task;
    }
}
