using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace NegiThemes;

internal sealed class CompileSession(XDocument source)
{
    public static readonly XNamespace Wpf = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    public static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";
    public static readonly XNamespace Avalonia = "https://github.com/avaloniaui";

    private static readonly Regex TargetTypePattern = new(
        @"^\{x:Type\s+(?<type>[A-Za-z_][A-Za-z0-9_.:]*)\}$",
        RegexOptions.Compiled);

    public XDocument Source { get; } = source;
    public IReadOnlyList<XElement> Styles { get; } = source.Root?.Elements(Wpf + "Style").ToList() ?? [];
    public IReadOnlyDictionary<string, XElement> Resources { get; } = source.Descendants()
        .Where(element => element.Attribute(X + "Key") is not null)
        .GroupBy(element => (string)element.Attribute(X + "Key")!, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal);

    public HashSet<XElement> Consumed { get; } = [];
    public List<string> Notes { get; } = [];

    public IEnumerable<XElement> StylesFor(string targetType) =>
        Styles.Where(style => TargetTypeOf(style) == targetType);

    public XElement? ImplicitStyle(string targetType) =>
        StylesFor(targetType).FirstOrDefault(style => KeyOf(style) is null);

    public XElement? KeyedStyle(string key) =>
        Styles.FirstOrDefault(style => KeyOf(style) == key);

    public XElement? Resource(string key) => Resources.GetValueOrDefault(key);

    public void Consume(XElement style) => Consumed.Add(style);

    public void EnsureEverythingConsumed()
    {
        var remaining = Styles.Where(style => !Consumed.Contains(style)).ToList();
        if (remaining.Count == 0)
            return;

        var names = remaining.Select(style => $"{TargetTypeOf(style)}({KeyOf(style) ?? "implicit"})");
        throw new InvalidOperationException("Unconsumed WPF styles: " + string.Join(", ", names));
    }

    public static string? TargetTypeOf(XElement style)
    {
        var attribute = style.Attribute("TargetType");
        if (attribute is null)
            return null;

        var match = TargetTypePattern.Match(attribute.Value.Trim());
        if (!match.Success)
            throw new NotSupportedException($"Unsupported TargetType syntax: {attribute.Value}");

        return match.Groups["type"].Value;
    }

    public static string? KeyOf(XElement style) => (string?)style.Attribute(X + "Key");

    public static string ThemeKeyFor(string avaloniaTargetType, string? wpfKey) =>
        string.IsNullOrWhiteSpace(wpfKey) || wpfKey.StartsWith('{')
            ? $"{{x:Type {avaloniaTargetType}}}"
            : wpfKey;
}
