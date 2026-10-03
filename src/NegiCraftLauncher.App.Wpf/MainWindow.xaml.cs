using System.Windows;

namespace NegiCraftLauncher.App.Wpf;

/// <summary>
/// P1 阶段的占位主窗口：只用来验证「WPF 空窗能起 + 分层透明窗逐像素 alpha」。
/// P5 会用真正的启动器界面替换掉它。
/// </summary>
public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }
}
