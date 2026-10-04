using System.Windows;

// ⚠️ 少了这一行，WPF 不会去本程序集里找 Themes/Generic.xaml，
// MaterialIcon 就完全没有模板 —— 不报错、不崩，只是所有图标都不显示。
[assembly: ThemeInfo(ResourceDictionaryLocation.None, ResourceDictionaryLocation.SourceAssembly)]
