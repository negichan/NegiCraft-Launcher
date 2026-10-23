using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NegiThemes;

internal abstract class ThemeBuilder(CompileSession session, string wpfTargetType, string avaloniaTargetType)
{
    private static readonly Regex StaticResourcePattern = new(
        @"^\{StaticResource\s+(?<key>[^}]+)\}$",
        RegexOptions.Compiled);

    private static readonly Regex AttachedTemplatedParentBinding = new(
        @"^\{Binding\s+Path=\(neg:Negi\.(?<name>\w+)\),\s*RelativeSource=\{RelativeSource\s+TemplatedParent\}\}$",
        RegexOptions.Compiled);

    private static readonly Regex SimpleTemplatedParentBinding = new(
        @"^\{Binding\s+(?<property>[A-Za-z_][A-Za-z0-9_.]*),\s*RelativeSource=\{RelativeSource\s+TemplatedParent\}\}$",
        RegexOptions.Compiled);

    protected CompileSession Session { get; } = session;
    protected string WpfTargetType { get; } = wpfTargetType;
    protected string AvaloniaTargetType { get; } = avaloniaTargetType;
    protected static XNamespace Wpf => CompileSession.Wpf;
    protected static XNamespace Avalonia => CompileSession.Avalonia;

    public XElement Build(XElement style)
    {
        Session.Consume(style);

        var theme = new XElement(Avalonia + "ControlTheme",
            new XAttribute("TargetType", AvaloniaTargetType),
            new XAttribute(CompileSession.X + "Key", ThemeKey(style)));

        if (style.Attribute("BasedOn") is not null)
            throw new NotSupportedException($"Style.BasedOn is not supported ({WpfTargetType}).");

        var templateSetter = style.Elements(Wpf + "Setter")
            .FirstOrDefault(setter => (string?)setter.Attribute("Property") == "Template");

        foreach (var setter in style.Elements(Wpf + "Setter"))
        {
            if (ReferenceEquals(setter, templateSetter) || SkipSetter(setter))
                continue;

            theme.Add(CompileSetter(setter));
        }

        var triggerStyles = new List<XElement>();

        if (templateSetter is not null)
        {
            var custom = BuildCustomTemplate(style, templateSetter);
            if (custom is not null)
            {
                theme.Add(custom);
            }
            else
            {
                var wpfTemplate = ResolveTemplate(templateSetter);
                var (compiledSetter, body) = BuildTemplateSetter(wpfTemplate);
                theme.Add(compiledSetter);

                if (wpfTemplate.Element(Wpf + "ControlTemplate.Triggers") is { } templateTriggers)
                    triggerStyles.AddRange(CompileTriggers(templateTriggers, body));
            }
        }

        if (style.Element(Wpf + "Style.Triggers") is { } styleTriggers)
            triggerStyles.AddRange(CompileTriggers(styleTriggers, null));

        foreach (var triggerStyle in triggerStyles)
            theme.Add(triggerStyle);

        AdaptControlTheme(theme);
        return theme;
    }

    protected virtual string ThemeKey(XElement style) =>
        CompileSession.ThemeKeyFor(AvaloniaTargetType, CompileSession.KeyOf(style));

    protected virtual string MapProperty(string property) => property switch
    {
        "neg:Negi.CornerRadius" => "CornerRadius",
        "neg:Negi.Placeholder" => "PlaceholderText",
        "neg:Negi.LeadingIcon" => "InnerLeftContent",
        "FocusVisualStyle" => "FocusAdorner",
        _ => property,
    };

    protected virtual string MapBinding(string expression)
    {
        var attached = AttachedTemplatedParentBinding.Match(expression);
        if (attached.Success)
        {
            var name = attached.Groups["name"].Value;
            var mapped = name switch
            {
                "CornerRadius" => "CornerRadius",
                "Placeholder" => "PlaceholderText",
                "LeadingIcon" => "InnerLeftContent",
                _ => throw new NotSupportedException($"No Avalonia mapping for attached property 'Negi.{name}'."),
            };
            return $"{{TemplateBinding {mapped}}}";
        }

        var simple = SimpleTemplatedParentBinding.Match(expression);
        if (simple.Success)
            return $"{{TemplateBinding {simple.Groups["property"].Value}}}";

        if (expression.Contains("RelativeSource={RelativeSource TemplatedParent}", StringComparison.Ordinal))
            throw new NotSupportedException($"Unsupported TemplatedParent binding: {expression}");

        return expression;
    }

    protected virtual (string Name, string Value)? MapPlainAttribute(XAttribute attribute)
    {
        var name = attribute.Name.LocalName;
        var value = attribute.Value;

        if (name == "Visibility")
            return ("IsVisible", VisibilityToBoolean(value));

        if (name == "Style")
        {
            if (StaticResourcePattern.IsMatch(value))
                return ("Theme", value);
            throw new NotSupportedException($"Unsupported Style attribute value: {value}");
        }

        return (MapProperty(name), MapBinding(value));
    }

    protected virtual XAttribute? MapNamespacedAttribute(XAttribute attribute)
    {
        var local = attribute.Name.LocalName;
        if (local.StartsWith("Negi.", StringComparison.Ordinal))
        {
            var suffix = local["Negi.".Length..];
            var mapped = suffix switch
            {
                "CornerRadius" => "CornerRadius",
                "Placeholder" => "PlaceholderText",
                "LeadingIcon" => "InnerLeftContent",
                _ => throw new NotSupportedException($"No Avalonia mapping for attached attribute 'Negi.{suffix}'."),
            };
            return new XAttribute(mapped, MapBinding(attribute.Value));
        }

        throw new NotSupportedException($"Unsupported namespaced template attribute '{attribute.Name}'.");
    }

    protected virtual bool SkipSetter(XElement setter) => false;
    protected virtual bool SkipTriggerSetter(XElement setter) => false;
    protected virtual bool SkipTrigger(XElement trigger) => false;
    protected virtual void AdaptTemplateElement(XElement source, XElement target) { }
    protected virtual void AdaptControlTheme(XElement theme) { }
    protected virtual XElement? BuildCustomTemplate(XElement style, XElement templateSetter) => null;

    protected static string VisibilityToBoolean(string value) => value switch
    {
        "Collapsed" or "Hidden" => "False",
        "Visible" => "True",
        _ => throw new NotSupportedException($"Unsupported Visibility value '{value}'."),
    };

    protected XElement CompileSetter(XElement setter)
    {
        var property = (string?)setter.Attribute("Property")
                       ?? throw new NotSupportedException("Setter without a Property attribute.");

        if (property == "Template")
        {
            var template = ResolveTemplate(setter);
            var (compiled, _) = BuildTemplateSetter(template);
            return compiled;
        }

        if (setter.Element(Wpf + "Setter.Value") is not null)
            throw new NotSupportedException($"Non-template Setter.Value is not supported for '{property}'.");

        var value = (string?)setter.Attribute("Value")
                    ?? throw new NotSupportedException($"Setter '{property}' has no Value.");

        property = MapProperty(property);
        value = MapBinding(value);

        if (property == "Visibility")
        {
            property = "IsVisible";
            value = VisibilityToBoolean(value);
        }

        return new XElement(Avalonia + "Setter",
            new XAttribute("Property", property),
            new XAttribute("Value", value));
    }

    private XElement ResolveTemplate(XElement setter)
    {
        var child = setter
            .Elements(Wpf + "Setter.Value")
            .Elements(Wpf + "ControlTemplate")
            .SingleOrDefault();

        if (child is not null)
            return child;

        var value = (string?)setter.Attribute("Value");
        var match = value is null ? Match.Empty : StaticResourcePattern.Match(value);
        if (!match.Success)
            throw new NotSupportedException($"Unsupported Template setter value: {value}");

        var key = match.Groups["key"].Value.Trim();
        var resource = Session.Resource(key)
                       ?? throw new NotSupportedException($"Missing ControlTemplate resource '{key}'.");

        if (resource.Name != Wpf + "ControlTemplate")
            throw new NotSupportedException($"Resource '{key}' is not a ControlTemplate.");

        return resource;
    }

    private (XElement Setter, XElement Body) BuildTemplateSetter(XElement wpfTemplate)
    {
        var body = TransformTemplateElement(wpfTemplate, isRoot: true)
                   ?? throw new InvalidOperationException("ControlTemplate could not be transformed.");

        var setter = new XElement(Avalonia + "Setter",
            new XAttribute("Property", "Template"),
            body);

        return (setter, body);
    }

    private XElement? TransformTemplateElement(XElement source, bool isRoot = false)
    {
        if (source.Name == Wpf + "ControlTemplate.Triggers")
            throw new InvalidOperationException("Trigger containers must not be transformed as elements.");

        if (source.Name == Wpf + "ContentPresenter.Resources")
            return null;

        if (source.Name == Wpf + "ControlTemplate.Resources")
            throw new NotSupportedException("ControlTemplate.Resources is not supported.");

        var isControlTemplate = source.Name == Wpf + "ControlTemplate";
        if (!isRoot && isControlTemplate && source.Element(Wpf + "ControlTemplate.Triggers") is not null)
            throw new NotSupportedException("Nested ControlTemplate triggers are not supported.");

        var target = new XElement(Avalonia + source.Name.LocalName);

        foreach (var attribute in source.Attributes())
        {
            if (attribute.IsNamespaceDeclaration)
                continue;

            if (attribute.Name == CompileSession.X + "Name")
            {
                target.SetAttributeValue("Name", attribute.Value);
                continue;
            }

            if (attribute.Name == CompileSession.X + "Key")
                continue;

            if (isControlTemplate && attribute.Name.LocalName == "TargetType")
                continue;

            if (attribute.Name.Namespace == XNamespace.None)
            {
                var mapped = MapPlainAttribute(attribute);
                if (mapped is not null)
                    target.SetAttributeValue(mapped.Value.Name, mapped.Value.Value);
                continue;
            }

            var mappedAttribute = MapNamespacedAttribute(attribute);
            if (mappedAttribute is not null)
                target.SetAttributeValue(mappedAttribute.Name, mappedAttribute.Value);
        }

        foreach (var child in source.Elements())
        {
            if (child.Name == Wpf + "ControlTemplate.Triggers")
                continue;

            var transformed = TransformTemplateElement(child);
            if (transformed is not null)
                target.Add(transformed);
        }

        AdaptTemplateElement(source, target);
        return target;
    }

    private List<XElement> CompileTriggers(XElement triggerContainer, XElement? templateBody)
    {
        if (triggerContainer.Elements(Wpf + "MultiTrigger").Any())
            throw new NotSupportedException("MultiTrigger is not supported.");

        var styles = new List<XElement>();
        var stylesBySelector = new Dictionary<string, XElement>(StringComparer.Ordinal);

        foreach (var trigger in triggerContainer.Elements(Wpf + "Trigger"))
        {
            if (SkipTrigger(trigger))
                continue;

            if (trigger.Element(Wpf + "Trigger.EnterActions") is not null ||
                trigger.Element(Wpf + "Trigger.ExitActions") is not null)
                throw new NotSupportedException("Trigger enter/exit actions are not supported.");

            var property = (string?)trigger.Attribute("Property")
                           ?? throw new NotSupportedException("Trigger without a Property attribute.");
            var value = (string?)trigger.Attribute("Value")
                        ?? throw new NotSupportedException("Trigger without a Value attribute.");

            var state = MapTriggerState(property, value)
                        ?? throw new NotSupportedException(
                            $"No Avalonia selector mapping for trigger {property}={value}.");

            foreach (var setter in trigger.Elements(Wpf + "Setter"))
            {
                if (SkipTriggerSetter(setter))
                    continue;

                var targetName = (string?)setter.Attribute("TargetName");
                var selector = targetName is null
                    ? $"^{state}"
                    : $"^{state} /template/ {FindElementType(templateBody, targetName)}#{targetName}";

                if (!stylesBySelector.TryGetValue(selector, out var style))
                {
                    style = new XElement(Avalonia + "Style", new XAttribute("Selector", selector));
                    stylesBySelector.Add(selector, style);
                    styles.Add(style);
                }

                var compiled = CompileSetter(setter);
                var compiledProperty = (string?)compiled.Attribute("Property");
                var existing = style.Elements(Avalonia + "Setter")
                    .FirstOrDefault(candidate => (string?)candidate.Attribute("Property") == compiledProperty);
                if (existing is null)
                    style.Add(compiled);
                else
                    existing.ReplaceWith(compiled);
            }
        }

        return styles;
    }

    private static string FindElementType(XElement? templateBody, string name)
    {
        if (templateBody is null)
            throw new NotSupportedException($"TargetName '{name}' is not supported outside a template.");

        var element = templateBody.Descendants()
            .FirstOrDefault(candidate => (string?)candidate.Attribute("Name") == name);

        return element?.Name.LocalName
               ?? throw new NotSupportedException($"Trigger targets unknown element '{name}'.");
    }

    protected virtual string? MapTriggerState(string property, string value) => (property, value) switch
    {
        ("IsMouseOver", "True") => ":pointerover",
        ("IsPressed", "True") => ":pressed",
        ("IsEnabled", "False") => ":disabled",
        ("IsKeyboardFocusWithin", "True") => ":focus",
        ("IsKeyboardFocused", "True") => ":focus",
        ("IsChecked", "True") => ":checked",
        ("IsChecked", "False") => ":unchecked",
        ("IsHighlighted", "True") => ":pointerover",
        ("Orientation", "Horizontal") => ":horizontal",
        ("Orientation", "Vertical") => ":vertical",
        _ => null,
    };
}
