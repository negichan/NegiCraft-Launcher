using System.Xml.Linq;

namespace NegiThemes;

internal sealed class GenericOptions
{
    public bool DropButtonMotion { get; init; }
    public bool SkipMarkupKeyStyles { get; init; }
    public Func<string, string>? MapProperty { get; init; }
    public Func<string, string>? MapBinding { get; init; }
    public Func<XAttribute, (string Name, string Value)?>? MapPlainAttribute { get; init; }
    public Func<XElement, bool>? SkipTrigger { get; init; }
    public Action<XElement, XElement>? AdaptTemplateElement { get; init; }
    public Action<XElement>? AdaptControlTheme { get; init; }
}

internal sealed class GenericAdapter(string wpfType, string avaloniaType, GenericOptions? options = null) : IControlAdapter
{
    private readonly GenericOptions _options = options ?? new GenericOptions();

    public IEnumerable<XElement> Compile(CompileSession session)
    {
        foreach (var style in session.StylesFor(wpfType))
        {
            var key = CompileSession.KeyOf(style);
            if (key is not null && key.StartsWith('{'))
            {
                if (_options.SkipMarkupKeyStyles)
                {
                    session.Consume(style);
                    session.Notes.Add($"skipped {wpfType} style with markup key {key}");
                    continue;
                }

                throw new NotSupportedException($"Markup-extension style key is not supported: {key}");
            }

            var builder = new Builder(session, wpfType, avaloniaType, _options);
            yield return builder.Build(style);
        }
    }

    private sealed class Builder(
        CompileSession session,
        string wpfType,
        string avaloniaType,
        GenericOptions options)
        : ThemeBuilder(session, wpfType, avaloniaType)
    {
        protected override string MapProperty(string property) =>
            options.MapProperty?.Invoke(property) ?? base.MapProperty(property);

        protected override string MapBinding(string expression) =>
            options.MapBinding?.Invoke(expression) ?? base.MapBinding(expression);

        protected override (string Name, string Value)? MapPlainAttribute(XAttribute attribute) =>
            options.MapPlainAttribute?.Invoke(attribute) ?? base.MapPlainAttribute(attribute);

        protected override XAttribute? MapNamespacedAttribute(XAttribute attribute)
        {
            if (options.DropButtonMotion &&
                attribute.Name.LocalName.StartsWith("ButtonMotion.", StringComparison.Ordinal))
                return null;

            return base.MapNamespacedAttribute(attribute);
        }

        protected override bool SkipTrigger(XElement trigger) =>
            options.SkipTrigger?.Invoke(trigger) ?? false;

        protected override void AdaptTemplateElement(XElement source, XElement target) =>
            options.AdaptTemplateElement?.Invoke(source, target);

        protected override void AdaptControlTheme(XElement theme) =>
            options.AdaptControlTheme?.Invoke(theme);
    }
}

internal sealed class SliderAdapter : IControlAdapter
{
    public IEnumerable<XElement> Compile(CompileSession session)
    {
        var style = session.ImplicitStyle("Slider")
                    ?? throw new InvalidOperationException("Missing implicit Slider style.");

        yield return new Builder(session).Build(style);
    }

    private sealed class Builder(CompileSession session) : ThemeBuilder(session, "Slider", "Slider")
    {
        protected override void AdaptTemplateElement(XElement source, XElement target)
        {
            if (target.Name.LocalName != "Track")
                return;

            foreach (var child in target.Elements().ToList())
            {
                if (child.Name == Avalonia + "Track.DecreaseRepeatButton")
                    child.Name = Avalonia + "Track.DecreaseButton";
                else if (child.Name == Avalonia + "Track.IncreaseRepeatButton")
                    child.Name = Avalonia + "Track.IncreaseButton";
                else if (child.Name == Avalonia + "Track.Thumb")
                    child.ReplaceWith(child.Elements().Single());
            }

            target.SetAttributeValue("Orientation", "Horizontal");
            target.SetAttributeValue("Minimum", "{TemplateBinding Minimum}");
            target.SetAttributeValue("Maximum", "{TemplateBinding Maximum}");
            target.SetAttributeValue("Value", "{TemplateBinding Value, Mode=TwoWay}");
        }
    }
}

internal sealed class ScrollBarAdapter : IControlAdapter
{
    public IEnumerable<XElement> Compile(CompileSession session)
    {
        var style = session.ImplicitStyle("ScrollBar")
                    ?? throw new InvalidOperationException("Missing implicit ScrollBar style.");

        yield return new Builder(session).Build(style);
    }

    private sealed class Builder(CompileSession session) : ThemeBuilder(session, "ScrollBar", "ScrollBar")
    {
        protected override bool SkipSetter(XElement setter) =>
            (string?)setter.Attribute("Property") == "Width";

        protected override bool SkipTriggerSetter(XElement setter) =>
            (string?)setter.Attribute("Property") == "Width";

        protected override void AdaptControlTheme(XElement theme)
        {
            theme.Add(new XElement(Avalonia + "Style",
                new XAttribute("Selector", "^:vertical"),
                new XElement(Avalonia + "Setter",
                    new XAttribute("Property", "Width"),
                    new XAttribute("Value", "10"))));
        }

        protected override void AdaptTemplateElement(XElement source, XElement target)
        {
            if (target.Name.LocalName == "Track")
            {
                foreach (var child in target.Elements().ToList())
                {
                    if (child.Name == Avalonia + "Track.DecreaseRepeatButton")
                        child.Name = Avalonia + "Track.DecreaseButton";
                    else if (child.Name == Avalonia + "Track.IncreaseRepeatButton")
                        child.Name = Avalonia + "Track.IncreaseButton";
                    else if (child.Name == Avalonia + "Track.Thumb")
                        child.ReplaceWith(child.Elements().Single());
                }

                target.SetAttributeValue("Orientation", "{TemplateBinding Orientation}");
                target.SetAttributeValue("Minimum", "{TemplateBinding Minimum}");
                target.SetAttributeValue("Maximum", "{TemplateBinding Maximum}");
                target.SetAttributeValue("Value", "{TemplateBinding Value, Mode=TwoWay}");
                target.SetAttributeValue("ViewportSize", "{TemplateBinding ViewportSize}");
            }
            else if (target.Name.LocalName == "RepeatButton")
            {
                target.Attribute("Command")?.Remove();
            }
        }
    }
}

internal sealed class ScrollViewerAdapter : IControlAdapter
{
    public IEnumerable<XElement> Compile(CompileSession session)
    {
        var style = session.ImplicitStyle("ScrollViewer")
                    ?? throw new InvalidOperationException("Missing implicit ScrollViewer style.");

        yield return new Builder(session).Build(style);
    }

    private sealed class Builder(CompileSession session) : ThemeBuilder(session, "ScrollViewer", "ScrollViewer")
    {
        protected override (string Name, string Value)? MapPlainAttribute(XAttribute attribute)
        {
            if (attribute.Name.LocalName == "Visibility" && attribute.Parent?.Name == Wpf + "ScrollBar")
                return null;

            return base.MapPlainAttribute(attribute);
        }

        protected override void AdaptTemplateElement(XElement source, XElement target)
        {
            if (target.Name.LocalName == "ScrollContentPresenter")
            {
                if ((string?)target.Attribute("Name") == "PART_ScrollContentPresenter")
                    target.SetAttributeValue("Name", "PART_ContentPresenter");

                target.Attribute("CanContentScroll")?.Remove();
            }
            else if (target.Name.LocalName == "ScrollBar")
            {
                target.Attribute("Maximum")?.Remove();
                target.Attribute("ViewportSize")?.Remove();
                target.Attribute("Value")?.Remove();
                target.Attribute("Visibility")?.Remove();
            }
            else if (target.Name.LocalName == "Rectangle")
            {
                target.Name = Avalonia + "Panel";
                target.Attribute("Fill")?.Remove();
                target.SetAttributeValue("Background", "Transparent");
                if ((string?)target.Attribute("Name") == "PART_Corner")
                    target.SetAttributeValue("Name", "PART_ScrollBarsSeparator");
            }
        }
    }
}

internal sealed class ToolTipAdapter : IControlAdapter
{
    public IEnumerable<XElement> Compile(CompileSession session)
    {
        var style = session.ImplicitStyle("ToolTip")
                    ?? throw new InvalidOperationException("Missing implicit ToolTip style.");

        yield return new Builder(session).Build(style);
    }

    private sealed class Builder(CompileSession session) : ThemeBuilder(session, "ToolTip", "ToolTip")
    {
        protected override void AdaptTemplateElement(XElement source, XElement target)
        {
            if (target.Name.LocalName != "ContentPresenter")
                return;

            target.SetAttributeValue("TextBlock.TextWrapping", "Wrap");
            target.SetAttributeValue("TextBlock.Foreground", "{TemplateBinding Foreground}");
        }
    }
}

internal abstract class RawThemeAdapter : IControlAdapter
{
    protected abstract string WpfTargetType { get; }
    protected abstract string RawXaml { get; }
    protected virtual IEnumerable<string> ExtraKeys => [];

    public IEnumerable<XElement> Compile(CompileSession session)
    {
        var style = session.ImplicitStyle(WpfTargetType)
                    ?? throw new InvalidOperationException($"Missing implicit {WpfTargetType} style.");
        session.Consume(style);

        foreach (var key in ExtraKeys)
        {
            var keyed = session.KeyedStyle(key)
                        ?? throw new InvalidOperationException($"Missing keyed style '{key}'.");
            session.Consume(keyed);
        }

        var theme = XElement.Parse(RawXaml);
        if (theme.Name != CompileSession.Avalonia + "ControlTheme")
            throw new InvalidOperationException("Raw theme must be a ControlTheme element.");

        // Avalonia only allows xmlns declarations on the document root; the generated
        // ResourceDictionary already declares x and conv.
        theme.Attributes().Where(attribute => attribute.IsNamespaceDeclaration).Remove();

        yield return theme;
    }
}

internal sealed class TextBoxAdapter : RawThemeAdapter
{
    protected override string WpfTargetType => "TextBox";
    protected override string RawXaml => RawThemes.TextBox;
    protected override IEnumerable<string> ExtraKeys => ["SearchBoxStyle"];
}

internal sealed class SwitchAdapter : RawThemeAdapter
{
    protected override string WpfTargetType => "CheckBox";
    protected override string RawXaml => RawThemes.ToggleSwitch;
}

internal sealed class MenuFlyoutAdapter : RawThemeAdapter
{
    protected override string WpfTargetType => "ContextMenu";
    protected override string RawXaml => RawThemes.MenuFlyoutPresenter;
}

internal sealed class MenuItemAdapter : RawThemeAdapter
{
    protected override string WpfTargetType => "MenuItem";
    protected override string RawXaml => RawThemes.MenuItem;
    protected override IEnumerable<string> ExtraKeys => ["NegiMenuPopupBorderStyle"];
}
