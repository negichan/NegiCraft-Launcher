using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using NegiCraftLauncher.Raster;

namespace NegiCraftLauncher.Pet;

/// <summary>
/// 桌面宠物综合设置窗口。
/// 支持在独立版首次运行（无名字或无皮肤时）充当初始化向导，
/// 也支持在运行中随时通过右键菜单或托盘调出进行全局配置与实时 3D 换肤预览。
/// </summary>
public partial class PetSettingsWindow : Window
{
    private readonly PetSettings _settings;
    private readonly bool _isFirstRunSetup;
    private readonly PetWindow? _petWindow;

    private byte[]? _selectedSkinBytes;
    private string? _selectedSkinPath;
    private string? _selectedSkinPlayerName;

    public PetSettingsWindow(PetSettings settings, bool isFirstRunSetup = false, PetWindow? petWindow = null)
    {
        InitializeComponent();

        _settings = settings;
        _isFirstRunSetup = isFirstRunSetup;
        _petWindow = petWindow;

        InitializeUiState();
    }

    /// <summary>
    /// 从正在运行的桌宠实例直接呼出设置窗口。
    /// </summary>
    public PetSettingsWindow(PetWindow petWindow)
        : this(PetSettings.Load(), isFirstRunSetup: false, petWindow: petWindow)
    {
    }

    private void InitializeUiState()
    {
        if (_isFirstRunSetup)
        {
            TxtTitle.Text = "桌宠初始化向导";
            TxtSubtitle.Text = "欢迎使用桌面宠物！请先起个昵称或挑选一款心仪的皮肤";
            BtnSave.Content = "完成设置，进入桌面";
            BtnCancel.Content = "退出";
        }

        // 1. 昵称
        var currentName = _petWindow?.Host?.CustomName
                          ?? _settings.CustomName
                          ?? _petWindow?.PlayerName
                          ?? (_isFirstRunSetup ? "Steve" : "");
        TxtPetName.Text = currentName;
        TxtOnlineUsername.Text = !string.IsNullOrWhiteSpace(currentName) ? currentName : "Steve";

        // 2. 皮肤回显
        _selectedSkinPath = _settings.SkinPath;
        _selectedSkinPlayerName = _settings.SkinPlayerName;

        if (!string.IsNullOrWhiteSpace(_selectedSkinPath) && File.Exists(_selectedSkinPath))
        {
            RbSkinLocal.IsChecked = true;
            TxtLocalSkinPath.Text = _selectedSkinPath;
            try
            {
                _selectedSkinBytes = File.ReadAllBytes(_selectedSkinPath);
                StagePreview.ApplySkin(_selectedSkinBytes);
            }
            catch
            {
                LoadFallbackSkin();
            }
        }
        else if (!string.IsNullOrWhiteSpace(_selectedSkinPlayerName))
        {
            RbSkinOnline.IsChecked = true;
            TxtOnlineUsername.Text = _selectedSkinPlayerName;
            LoadOnlineOrPresetSkin(_selectedSkinPlayerName);
        }
        else if (!string.IsNullOrWhiteSpace(currentName))
        {
            RbSkinOnline.IsChecked = true;
            LoadOnlineOrPresetSkin(currentName);
        }
        else
        {
            LoadFallbackSkin();
        }

        // 3. 尺寸与视角
        var scale = _petWindow?.CurrentScale ?? _settings.Scale;
        if (scale < 0.5 || scale > 2.0) scale = 1.0;
        SliderScale.Value = scale;
        TxtScaleValue.Text = $"{(int)(scale * 100)}%";

        ChkLookAtMouse.IsChecked = _petWindow?.LookAtMouse ?? _settings.LookAtMouse;
        ChkSpineFlexible.IsChecked = _petWindow?.SpineFlexible ?? _settings.SpineFlexible;

        // 4. 渲染与系统
        ChkUseGpu.IsChecked = _petWindow?.Preview.UseGpu ?? _settings.UseGpu;
        ChkTopmost.IsChecked = _petWindow?.Topmost ?? _settings.Topmost;

        // 5. 交互模式
        var currentMode = _petWindow?.CurrentMode.ToString() ?? _settings.InteractionMode;
        if (string.Equals(currentMode, "Control", StringComparison.OrdinalIgnoreCase))
        {
            RbModeControl.IsChecked = true;
        }
        else if (string.Equals(currentMode, "FollowMouse", StringComparison.OrdinalIgnoreCase))
        {
            RbModeFollow.IsChecked = true;
        }
        else
        {
            RbModeFree.IsChecked = true;
        }
    }

    private void LoadFallbackSkin()
    {
        _selectedSkinBytes = DefaultSkins.Bytes(slim: false);
        _selectedSkinPlayerName = "steve";
        _selectedSkinPath = null;
        StagePreview.ApplySkin(_selectedSkinBytes);
    }

    private async void LoadOnlineOrPresetSkin(string username)
    {
        var preset = DefaultSkins.TryRead(username);
        if (preset != null)
        {
            _selectedSkinBytes = preset;
            _selectedSkinPlayerName = username;
            _selectedSkinPath = null;
            StagePreview.ApplySkin(preset);
            return;
        }

        try
        {
            var data = await SkinRepository.GetOrFetchAsync(username);
            if (data is { Bytes.Length: > 0 })
            {
                _selectedSkinBytes = data.Bytes;
                _selectedSkinPlayerName = username;
                _selectedSkinPath = null;
                StagePreview.ApplySkin(data.Bytes);
            }
            else
            {
                LoadFallbackSkin();
            }
        }
        catch
        {
            LoadFallbackSkin();
        }
    }

    // ==========================================================
    // 事件处理
    // ==========================================================

    /// <summary>
    /// 关窗时用户的取舍。WPF 的 <c>DialogResult</c> 只许在 <c>ShowDialog()</c> 开出来的窗口上设，
    /// 而这个窗口有两副面孔：独立版首运是模态向导，运行中从右键菜单/托盘调出来是非模态面板，
    /// 后者一设就抛 <c>InvalidOperationException</c> 把整个进程带走。所以结果统一记在这里。
    /// </summary>
    public bool? CloseResult { get; private set; }

    private void CloseWithResult(bool result)
    {
        CloseResult = result;
        Close();
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        CloseWithResult(false);
    }

    private void OnPetNameChanged(object sender, TextChangedEventArgs e)
    {
        if (StagePreview == null) return;
        StagePreview.PlayerName = TxtPetName.Text;
    }

    private void OnSkinSourceChanged(object sender, RoutedEventArgs e)
    {
        if (PanelSkinOnline == null || PanelSkinLocal == null) return;

        bool isOnline = RbSkinOnline.IsChecked == true;
        PanelSkinOnline.Visibility = isOnline ? Visibility.Visible : Visibility.Collapsed;
        PanelSkinLocal.Visibility = isOnline ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void OnFetchOnlineSkinClick(object sender, RoutedEventArgs e)
    {
        var username = TxtOnlineUsername.Text?.Trim();
        if (string.IsNullOrWhiteSpace(username)) return;

        BtnFetchOnline.IsEnabled = false;
        BtnFetchOnline.Content = "拉取中…";

        try
        {
            var data = await SkinRepository.GetOrFetchAsync(username);
            if (data is { Bytes.Length: > 0 })
            {
                _selectedSkinBytes = data.Bytes;
                _selectedSkinPlayerName = username;
                _selectedSkinPath = null;
                StagePreview.ApplySkin(data.Bytes);

                if (string.IsNullOrWhiteSpace(TxtPetName.Text))
                {
                    TxtPetName.Text = username;
                }
            }
            else
            {
                MessageBox.Show(this, $"未能在官方服务器上找到玩家 [{username}] 的皮肤。", "获取皮肤",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"获取皮肤失败: {ex.Message}", "获取皮肤错误",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            BtnFetchOnline.IsEnabled = true;
            BtnFetchOnline.Content = "获取并应用";
        }
    }

    private void OnBrowseLocalSkinClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Minecraft 皮肤文件",
            Filter = "Minecraft 皮肤 (*.png)|*.png|所有文件 (*.*)|*.*",
            Multiselect = false,
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            var bytes = File.ReadAllBytes(dialog.FileName);
            _selectedSkinBytes = bytes;
            _selectedSkinPath = dialog.FileName;
            _selectedSkinPlayerName = null;
            TxtLocalSkinPath.Text = dialog.FileName;
            StagePreview.ApplySkin(bytes);

            if (string.IsNullOrWhiteSpace(TxtPetName.Text))
            {
                TxtPetName.Text = Path.GetFileNameWithoutExtension(dialog.FileName);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"读取皮肤文件失败: {ex.Message}", "读取错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnPresetSteveClick(object sender, RoutedEventArgs e)
    {
        _selectedSkinBytes = DefaultSkins.Bytes(slim: false);
        _selectedSkinPlayerName = "steve";
        _selectedSkinPath = null;
        StagePreview.ApplySkin(_selectedSkinBytes);
    }

    private void OnPresetAlexClick(object sender, RoutedEventArgs e)
    {
        _selectedSkinBytes = DefaultSkins.Bytes(slim: true);
        _selectedSkinPlayerName = "alex";
        _selectedSkinPath = null;
        StagePreview.ApplySkin(_selectedSkinBytes);
    }

    private void OnPresetMikuClick(object sender, RoutedEventArgs e)
    {
        var bytes = DefaultSkins.TryRead("miku_mew");
        if (bytes != null)
        {
            _selectedSkinBytes = bytes;
            _selectedSkinPlayerName = "miku_mew";
            _selectedSkinPath = null;
            StagePreview.ApplySkin(bytes);
        }
    }

    private void OnResetRotationClick(object sender, RoutedEventArgs e)
    {
        StagePreview.ResetRotation();
    }

    private void OnPoseStandClick(object sender, RoutedEventArgs e)
    {
        StagePreview.Swaying = false;
    }

    private void OnPoseSwayClick(object sender, RoutedEventArgs e)
    {
        StagePreview.Swaying = !StagePreview.Swaying;
    }

    private void OnScaleValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (TxtScaleValue == null) return;
        TxtScaleValue.Text = $"{(int)(e.NewValue * 100)}%";
    }

    private void OnScaleQuick75Click(object sender, RoutedEventArgs e) => SliderScale.Value = 0.75;
    private void OnScaleQuick100Click(object sender, RoutedEventArgs e) => SliderScale.Value = 1.0;
    private void OnScaleQuick125Click(object sender, RoutedEventArgs e) => SliderScale.Value = 1.25;
    private void OnScaleQuick150Click(object sender, RoutedEventArgs e) => SliderScale.Value = 1.5;

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        var petName = TxtPetName.Text?.Trim();
        if (string.IsNullOrWhiteSpace(petName))
        {
            petName = !string.IsNullOrWhiteSpace(_selectedSkinPlayerName) ? _selectedSkinPlayerName : "Steve";
        }

        _settings.CustomName = petName;
        _settings.SkinPath = _selectedSkinPath;
        _settings.SkinPlayerName = _selectedSkinPlayerName;
        _settings.Scale = SliderScale.Value;
        _settings.LookAtMouse = ChkLookAtMouse.IsChecked == true;
        _settings.SpineFlexible = ChkSpineFlexible.IsChecked == true;
        _settings.UseGpu = ChkUseGpu.IsChecked == true;
        _settings.Topmost = ChkTopmost.IsChecked == true;

        if (RbModeControl.IsChecked == true)
        {
            _settings.InteractionMode = "Control";
        }
        else if (RbModeFollow.IsChecked == true)
        {
            _settings.InteractionMode = "FollowMouse";
        }
        else
        {
            _settings.InteractionMode = "Free";
        }

        _settings.Save();

        // 如果连接了运行中的 PetWindow，立即动态生效
        if (_petWindow != null)
        {
            _petWindow.ApplySettings(_settings);
            if (_selectedSkinBytes != null)
            {
                _petWindow.ApplySkin(_selectedSkinBytes);
            }
        }

        CloseWithResult(true);
    }
}
