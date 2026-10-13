using System;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NegiCraftLauncher.Core;

public static class NclJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase), new LooseDateTimeOffsetConverter() },
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static T? Deserialize<T>(string json) => JsonSerializer.Deserialize<T>(json, Options);

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Indented);
}

/// <summary>
///     可空 <see cref="DateTimeOffset"/> 的宽松读法。
///
///     <para>Mojang 的元数据里时间是 ISO 8601，而 Fabric meta 给的 profile 写的是
///     <c>"2024-12-05 06:33:37"</c> —— 这种值 System.Text.Json 直接判非法，整份 profile 都读不出来
///     （导入 Fabric 整合包就是这么挂的）。这里两种写法都收。</para>
///
///     <para>读不懂就当没有：这个字段只用来在版本没声明 javaVersion 时推 Java 大版本，
///     缺了有别的兜底，为一行时间戳让整次安装失败不值得。</para>
/// </summary>
public sealed class LooseDateTimeOffsetConverter : JsonConverter<DateTimeOffset?>
{
    private static readonly string[] LooseFormats =
    {
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd HH:mm",
        "yyyy-MM-dd",
    };

    public override DateTimeOffset? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.Null:
                return null;
            case JsonTokenType.Number when reader.TryGetInt64(out var millis):
                return DateTimeOffset.FromUnixTimeMilliseconds(millis);
            case JsonTokenType.String:
            {
                var text = reader.GetString();

                if (DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
                {
                    return iso;
                }

                return DateTime.TryParseExact(text, LooseFormats, CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out var loose)
                    ? new DateTimeOffset(loose, DateTimeOffset.Now.Offset)
                    : null;
            }
            default:
                return null;
        }
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset? value, JsonSerializerOptions options)
    {
        if (value is null)
        {
            writer.WriteNullValue();
            return;
        }

        writer.WriteStringValue(value.Value.ToString("O", CultureInfo.InvariantCulture));
    }
}
