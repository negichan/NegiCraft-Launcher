using System.Windows;
using System.Windows.Controls;
using NegiCraftLauncher.ViewModels;

namespace NegiCraftLauncher.App;

public partial class MainWindow
{
    private Panel SettingsNavPanel => (Panel)PageSettingsNavHost.Content;
    private Panel SettingsCardsPanel => (Panel)PageSettingsCardsHost.Content;

    private void InitializeSettingsViews()
    {
        var navigation = (DataTemplate)FindResource("SettingsNavTemplate");
        var cards = (DataTemplate)FindResource("SettingsCardsTemplate");
        PageSettingsNavHost.Content = navigation.LoadContent();
        PageSettingsCardsHost.Content = cards.LoadContent();
    }
}
