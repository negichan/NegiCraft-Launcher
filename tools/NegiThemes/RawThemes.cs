namespace NegiThemes;

// These controls cannot be derived mechanically from the WPF templates: required
// template parts and control types differ (TextBox host/watermark/clear button,
// CheckBox→ToggleSwitch, ContextMenu→MenuFlyoutPresenter, MenuItem popup).
// This file is the deliberate target-specific escape hatch; the other themes are
// transformed from src/NegiCraftLauncher.Theme/Controls.xaml.
internal static class RawThemes
{
    public const string TextBox = """
        <ControlTheme xmlns="https://github.com/avaloniaui"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      xmlns:conv="using:Avalonia.Data.Converters"
                      TargetType="TextBox" x:Key="{x:Type TextBox}">
            <Setter Property="Background" Value="{DynamicResource CardBackgroundBrush}" />
            <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}" />
            <Setter Property="BorderBrush" Value="{DynamicResource BorderLineBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="CornerRadius" Value="10" />
            <Setter Property="Padding" Value="10,0" />
            <Setter Property="MinHeight" Value="36" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="CaretBrush" Value="{DynamicResource ForegroundBrush}" />
            <Setter Property="SelectionBrush" Value="{DynamicResource RingBrush}" />
            <Setter Property="SelectionForegroundBrush" Value="{DynamicResource ForegroundBrush}" />
            <Setter Property="FocusAdorner" Value="{x:Null}" />
            <Setter Property="Template">
                <ControlTemplate>
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}"
                            MinHeight="{TemplateBinding MinHeight}"
                            TextElement.Foreground="{TemplateBinding Foreground}"
                            Name="Border">
                        <Grid ColumnDefinitions="Auto,*,Auto" Margin="{TemplateBinding Padding}">
                            <Panel Grid.Column="0" VerticalAlignment="Center">
                                <Path Name="PART_SearchIcon"
                                      Data="M9.5,3A6.5,6.5 0 0,1 16,9.5C16,11.11 15.41,12.59 14.44,13.73L14.71,14H15.5L20.5,19L19,20.5L14,15.5V14.71L13.73,14.44C12.59,15.41 11.11,16 9.5,16A6.5,6.5 0 0,1 3,9.5A6.5,6.5 0 0,1 9.5,3M9.5,5C7,5 5,7 5,9.5C5,12 7,14 9.5,14C12,14 14,12 14,9.5C14,7 12,5 9.5,5Z"
                                      Width="13" Height="13"
                                      Fill="#71717a"
                                      Stretch="Uniform"
                                      IsHitTestVisible="False"
                                      Margin="0,0,5,0" />
                                <ContentPresenter Content="{TemplateBinding InnerLeftContent}"
                                                  IsVisible="{TemplateBinding InnerLeftContent, Converter={x:Static conv:ObjectConverters.IsNotNull}}"
                                                  Margin="0,0,5,0"
                                                  IsHitTestVisible="False" />
                            </Panel>
                            <ScrollViewer AllowAutoHide="{TemplateBinding (ScrollViewer.AllowAutoHide)}"
                                          Grid.Column="1"
                                          HorizontalScrollBarVisibility="{TemplateBinding (ScrollViewer.HorizontalScrollBarVisibility)}"
                                          VerticalScrollBarVisibility="{TemplateBinding (ScrollViewer.VerticalScrollBarVisibility)}"
                                          Name="PART_ScrollViewer">
                                <Panel Name="TextContainer">
                                    <TextBlock Foreground="{DynamicResource MutedForegroundBrush}"
                                               HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                               IsVisible="{TemplateBinding Text, Converter={x:Static conv:StringConverters.IsNullOrEmpty}}"
                                               Name="Watermark"
                                               Text="{TemplateBinding PlaceholderText}"
                                               VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                                    <TextPresenter CaretBrush="{TemplateBinding CaretBrush}"
                                                   CaretIndex="{TemplateBinding CaretIndex}"
                                                   HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                   Name="PART_TextPresenter"
                                                   PasswordChar="{TemplateBinding PasswordChar}"
                                                   RevealPassword="{TemplateBinding RevealPassword}"
                                                   SelectionBrush="{TemplateBinding SelectionBrush}"
                                                   SelectionEnd="{TemplateBinding SelectionEnd}"
                                                   SelectionForegroundBrush="{TemplateBinding SelectionForegroundBrush}"
                                                   SelectionStart="{TemplateBinding SelectionStart}"
                                                   Text="{TemplateBinding Text, Mode=TwoWay}"
                                                   TextAlignment="{TemplateBinding TextAlignment}"
                                                   TextWrapping="{TemplateBinding TextWrapping}"
                                                   VerticalAlignment="{TemplateBinding VerticalContentAlignment}" />
                                </Panel>
                            </ScrollViewer>
                            <Panel Grid.Column="2" VerticalAlignment="Center">
                                <ContentPresenter Content="{TemplateBinding InnerRightContent}"
                                                  IsVisible="{TemplateBinding InnerRightContent, Converter={x:Static conv:ObjectConverters.IsNotNull}}"
                                                  Margin="8,0,0,0" />
                                <Button Name="PART_ClearButton"
                                        Command="{CompiledBinding $parent[TextBox].Clear}"
                                        Focusable="False" IsTabStop="False"
                                        Cursor="Hand"
                                        Width="20" Height="20"
                                        Margin="4,0,0,0"
                                        VerticalAlignment="Center">
                                    <Path Name="PART_ClearIcon"
                                          Data="M19 6.41L17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"
                                          Width="12" Height="12"
                                          Fill="#71717a"
                                          Stretch="Uniform"
                                          HorizontalAlignment="Center" VerticalAlignment="Center" />
                                </Button>
                            </Panel>
                        </Grid>
                    </Border>
                </ControlTemplate>
            </Setter>
            <Style Selector="^:pointerover /template/ Border#Border">
                <Setter Property="BorderBrush" Value="{DynamicResource RingBrush}" />
            </Style>
            <Style Selector="^:focus /template/ Border#Border">
                <Setter Property="BorderBrush" Value="{DynamicResource RingBrush}" />
            </Style>
            <Style Selector="^:disabled">
                <Setter Property="Opacity" Value="0.5" />
            </Style>
            <Style Selector="^ /template/ Path#PART_SearchIcon">
                <Setter Property="IsVisible" Value="False" />
            </Style>
            <Style Selector="^ /template/ Button#PART_ClearButton">
                <Setter Property="Background" Value="Transparent" />
                <Setter Property="CornerRadius" Value="4" />
                <Setter Property="Padding" Value="0" />
                <Setter Property="BorderThickness" Value="0" />
                <Setter Property="IsVisible" Value="False" />
            </Style>
            <Style Selector="^ /template/ Button#PART_ClearButton:pointerover">
                <Setter Property="Background" Value="#27272a" />
            </Style>
            <Style Selector="^ /template/ Button#PART_ClearButton:pointerover Path#PART_ClearIcon">
                <Setter Property="Fill" Value="#fafafa" />
            </Style>
            <Style Selector="^ /template/ Button#PART_ClearButton:pressed">
                <Setter Property="Background" Value="#3f3f46" />
            </Style>
            <Style Selector="^.SearchBox /template/ Path#PART_SearchIcon">
                <Setter Property="IsVisible" Value="True" />
            </Style>
            <Style Selector="^.SearchBox:not(:empty) /template/ Button#PART_ClearButton">
                <Setter Property="IsVisible" Value="True" />
            </Style>
            <Style Selector="^.clearButton:not(:empty) /template/ Button#PART_ClearButton">
                <Setter Property="IsVisible" Value="True" />
            </Style>
            <Style Selector="^.SearchBox">
                <Setter Property="Height" Value="32" />
                <Setter Property="MinHeight" Value="32" />
                <Setter Property="CornerRadius" Value="8" />
                <Setter Property="Background" Value="#111114" />
                <Setter Property="BorderBrush" Value="#26262b" />
                <Setter Property="FontSize" Value="12.5" />
                <Setter Property="Padding" Value="6,0,6,0" />
                <Setter Property="VerticalContentAlignment" Value="Center" />
            </Style>
            <Style Selector="^.SearchBox:pointerover /template/ Border#Border">
                <Setter Property="BorderBrush" Value="#3f3f46" />
            </Style>
            <Style Selector="^.SearchBox:focus /template/ Border#Border">
                <Setter Property="BorderBrush" Value="{DynamicResource RingBrush}" />
            </Style>
        </ControlTheme>
        """;

    public const string ToggleSwitch = """
        <ControlTheme xmlns="https://github.com/avaloniaui"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      TargetType="ToggleSwitch" x:Key="{x:Type ToggleSwitch}">
            <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}" />
            <Setter Property="OnContent" Value="" />
            <Setter Property="OffContent" Value="" />
            <Setter Property="HorizontalAlignment" Value="Left" />
            <Setter Property="VerticalAlignment" Value="Center" />
            <Setter Property="HorizontalContentAlignment" Value="Left" />
            <Setter Property="VerticalContentAlignment" Value="Center" />
            <Setter Property="FontSize" Value="14" />
            <Setter Property="Template">
                <ControlTemplate>
                    <StackPanel Name="PART_Container" Orientation="Horizontal" Spacing="8">
                        <Panel>
                            <ContentPresenter Content="{TemplateBinding OffContent}"
                                              ContentTemplate="{TemplateBinding OffContentTemplate}"
                                              HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                              x:Name="PART_OffContentPresenter" />
                            <ContentPresenter Content="{TemplateBinding OnContent}"
                                              ContentTemplate="{TemplateBinding OnContentTemplate}"
                                              HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                              VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                              x:Name="PART_OnContentPresenter" />
                        </Panel>
                        <Grid Background="Transparent" Name="PART_ToggleContainer" RowDefinitions="Auto,*">
                            <ContentPresenter Content="{TemplateBinding Content}"
                                              ContentTemplate="{TemplateBinding ContentTemplate}"
                                              Grid.Row="0"
                                              RecognizesAccessKey="True"
                                              VerticalAlignment="Top"
                                              x:Name="PART_ContentPresenter" />
                            <Grid Grid.Row="1"
                                  HorizontalAlignment="Left"
                                  RowDefinitions="2,Auto,2"
                                  VerticalAlignment="Top">
                                <Border Background="{DynamicResource MutedBrush}"
                                        CornerRadius="999"
                                        Grid.Row="1"
                                        Height="20"
                                        Name="Track"
                                        Width="36">
                                    <Border.Transitions>
                                        <Transitions>
                                            <BrushTransition Duration="0:0:0.15" Property="Background" />
                                        </Transitions>
                                    </Border.Transitions>
                                </Border>
                                <Canvas Grid.Row="1"
                                        Height="16"
                                        HorizontalAlignment="Left"
                                        Name="PART_SwitchKnob"
                                        VerticalAlignment="Center"
                                        Width="16">
                                    <Border Background="{DynamicResource CardBackgroundBrush}"
                                            Canvas.Left="2"
                                            Canvas.Top="0"
                                            ClipToBounds="True"
                                            CornerRadius="999"
                                            Height="16"
                                            Name="SwitchKnob"
                                            Width="16">
                                        <Border.Transitions>
                                            <Transitions>
                                                <DoubleTransition Duration="0:0:0.15" Property="Canvas.Left" />
                                            </Transitions>
                                        </Border.Transitions>
                                    </Border>
                                    <Grid x:Name="PART_MovingKnobs" />
                                </Canvas>
                            </Grid>
                        </Grid>
                    </StackPanel>
                </ControlTemplate>
            </Setter>
            <Style Selector="^[IsEnabled=False] /template/ StackPanel#PART_Container">
                <Setter Property="Opacity" Value="0.5" />
            </Style>
            <Style Selector="^[IsEnabled=True] /template/ Grid#PART_ToggleContainer:pointerover">
                <Setter Property="Cursor" Value="Hand" />
            </Style>
            <Style Selector="^:unchecked">
                <Style Selector="^ /template/ Border#SwitchKnob">
                    <Setter Property="Canvas.Left" Value="2" />
                </Style>
                <Style Selector="^ /template/ ContentPresenter#PART_OffContentPresenter">
                    <Setter Property="IsVisible" Value="True" />
                </Style>
                <Style Selector="^ /template/ ContentPresenter#PART_OnContentPresenter">
                    <Setter Property="IsVisible" Value="False" />
                </Style>
            </Style>
            <Style Selector="^:checked">
                <Style Selector="^ /template/ Border#Track">
                    <Setter Property="Background" Value="{DynamicResource PrimaryBrush}" />
                </Style>
                <Style Selector="^ /template/ Border#SwitchKnob">
                    <Setter Property="Canvas.Left" Value="18" />
                </Style>
                <Style Selector="^ /template/ ContentPresenter#PART_OffContentPresenter">
                    <Setter Property="IsVisible" Value="False" />
                </Style>
                <Style Selector="^ /template/ ContentPresenter#PART_OnContentPresenter">
                    <Setter Property="IsVisible" Value="True" />
                </Style>
            </Style>
        </ControlTheme>
        """;

    public const string MenuFlyoutPresenter = """
        <ControlTheme xmlns="https://github.com/avaloniaui"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      TargetType="MenuFlyoutPresenter" x:Key="{x:Type MenuFlyoutPresenter}">
            <Setter Property="Background" Value="{DynamicResource CardBackgroundBrush}" />
            <Setter Property="BorderBrush" Value="{DynamicResource BorderLineBrush}" />
            <Setter Property="BorderThickness" Value="1" />
            <Setter Property="Padding" Value="4" />
            <Setter Property="CornerRadius" Value="12" />
            <Setter Property="MinWidth" Value="140" />
            <Setter Property="MaxWidth" Value="280" />
            <Setter Property="ScrollViewer.HorizontalScrollBarVisibility" Value="Disabled" />
            <Setter Property="ScrollViewer.VerticalScrollBarVisibility" Value="Auto" />
            <Setter Property="Template">
                <ControlTemplate>
                    <Border Background="{TemplateBinding Background}"
                            BorderBrush="{TemplateBinding BorderBrush}"
                            BorderThickness="{TemplateBinding BorderThickness}"
                            CornerRadius="{TemplateBinding CornerRadius}"
                            Padding="{TemplateBinding Padding}"
                            BoxShadow="0 12 32 -6 #E6000000">
                        <ScrollViewer HorizontalScrollBarVisibility="{TemplateBinding (ScrollViewer.HorizontalScrollBarVisibility)}"
                                      VerticalScrollBarVisibility="{TemplateBinding (ScrollViewer.VerticalScrollBarVisibility)}">
                            <ItemsPresenter Name="PART_ItemsPresenter"
                                            ItemsPanel="{TemplateBinding ItemsPanel}"
                                            KeyboardNavigation.TabNavigation="Continue"
                                            Grid.IsSharedSizeScope="True" />
                        </ScrollViewer>
                    </Border>
                </ControlTemplate>
            </Setter>
        </ControlTheme>
        """;

    public const string MenuItem = """
        <ControlTheme xmlns="https://github.com/avaloniaui"
                      xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                      TargetType="MenuItem" x:Key="{x:Type MenuItem}">
            <Setter Property="Background" Value="Transparent" />
            <Setter Property="Foreground" Value="{DynamicResource ForegroundBrush}" />
            <Setter Property="Padding" Value="12,7" />
            <Setter Property="CornerRadius" Value="8" />
            <Setter Property="FontSize" Value="13" />
            <Setter Property="Cursor" Value="Hand" />
            <Setter Property="Template">
                <ControlTemplate>
                    <Panel>
                        <Border Background="{TemplateBinding Background}"
                                BorderBrush="{TemplateBinding BorderBrush}"
                                BorderThickness="{TemplateBinding BorderThickness}"
                                CornerRadius="{TemplateBinding CornerRadius}"
                                Padding="{TemplateBinding Padding}"
                                Margin="0,1"
                                Name="PART_LayoutRoot">
                            <Grid ColumnDefinitions="Auto,*,Auto">
                                <ContentPresenter Grid.Column="0"
                                                  Content="{TemplateBinding Icon}"
                                                  Margin="0,0,8,0"
                                                  VerticalAlignment="Center"
                                                  IsVisible="{TemplateBinding Icon, Converter={x:Static conv:ObjectConverters.IsNotNull}}"
                                                  Name="PART_IconPresenter" />
                                <ContentPresenter Grid.Column="1"
                                                  Content="{TemplateBinding Header}"
                                                  ContentTemplate="{TemplateBinding HeaderTemplate}"
                                                  RecognizesAccessKey="True"
                                                  VerticalAlignment="Center"
                                                  TextElement.Foreground="{TemplateBinding Foreground}"
                                                  TextElement.FontSize="{TemplateBinding FontSize}"
                                                  Name="PART_HeaderPresenter" />
                                <Path Grid.Column="2"
                                      Fill="{DynamicResource MutedForegroundBrush}"
                                      Data="M 0 0 L 4 4 L 0 8 Z"
                                      VerticalAlignment="Center"
                                      Margin="12,0,2,0"
                                      Name="PART_ChevronPath" />
                            </Grid>
                        </Border>
                        <Popup Name="PART_Popup"
                               WindowManagerAddShadowHint="False"
                               Placement="RightEdgeAlignedTop"
                               HorizontalOffset="4"
                               IsOpen="{TemplateBinding IsSubMenuOpen, Mode=TwoWay}">
                            <Border Background="{DynamicResource CardBackgroundBrush}"
                                    BorderBrush="{DynamicResource BorderLineBrush}"
                                    BorderThickness="1"
                                    CornerRadius="12"
                                    Padding="4"
                                    BoxShadow="0 12 32 -6 #E6000000">
                                <ScrollViewer HorizontalScrollBarVisibility="Disabled"
                                              VerticalScrollBarVisibility="Auto">
                                    <ItemsPresenter Name="PART_ItemsPresenter"
                                                    ItemsPanel="{TemplateBinding ItemsPanel}"
                                                    Grid.IsSharedSizeScope="True" />
                                </ScrollViewer>
                            </Border>
                        </Popup>
                    </Panel>
                </ControlTemplate>
            </Setter>
            <Style Selector="^:empty /template/ Path#PART_ChevronPath">
                <Setter Property="IsVisible" Value="False" />
            </Style>
            <Style Selector="^:pointerover /template/ Border#PART_LayoutRoot">
                <Setter Property="Background" Value="#1c1c21" />
            </Style>
            <Style Selector="^:disabled">
                <Setter Property="Opacity" Value="0.4" />
            </Style>
        </ControlTheme>
        """;
}
