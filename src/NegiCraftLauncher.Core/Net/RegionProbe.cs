using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NegiCraftLauncher.Core.Net;

/// <summary>
/// 一次地区探测的结果。<see cref="Code"/> 是 ISO 3166-1 alpha-2（港澳台按国内口径单独给
/// <c>HK</c> / <c>MO</c> / <c>TW</c>），<b>探不到时为空字符串</b> —— 界面据此显示「未知」，
/// 需要按地区判断的地方也一律按「未知」处理，不去猜。
/// </summary>
public sealed record RegionLocation(string Code, string Name, string? City, string Source);

/// <summary>
/// 按<b>出口 IP 的注册地</b>判断用户所在的国家 / 地区。
///
/// <para><b>用途</b>：有些资源和镜像只在特定地区合法或可用，启动器要按地区决定"给不给、
/// 从哪拿"。所以这里只做归地判断，不做精确定位：不读设备信息、不读系统区域设置、不碰
/// GPS —— 换网络出口结论就跟着变，这正是想要的语义。</para>
///
/// <para><b>三家轮着试</b>，任何一家返回就停：<c>myip.ipip.net</c>（国内最快、带省市，
/// 但只有中文地名没有 ISO 码）、<c>cloudflare.com/cdn-cgi/trace</c>（只给 <c>loc=XX</c>，
/// 全球都通）、<c>ipwho.is</c>（带 ISO 码与英文名）。全部失败返回 <c>null</c>，
/// 界面显示未知，**不缓存失败结果**。</para>
///
/// <para>没走 MaxMind GeoLite2：它的库要账号 + license 才能下，而且几十 MB 的数据要跟着
/// 安装包走，为一个两位地区码不值当。</para>
/// </summary>
public static class RegionProbe
{
    /// <summary>单家超时。三家串起来最坏 ~12s，但那已经三家全挂的情况了。</summary>
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(4);

    /// <summary>探测顺序：先国内快的，再全球通用的，最后带 ISO 码的。</summary>
    private static readonly (string Source, string Url, Func<string, RegionLocation?> Parse)[] Probes =
    {
        ("ipip", "https://myip.ipip.net/json", ParseIpIp),
        ("cloudflare", "https://www.cloudflare.com/cdn-cgi/trace", ParseTrace),
        ("ipwho.is", "https://ipwho.is/?fields=success,country_code,country,city", ParseIpWhoIs),
    };

    /// <summary>依次试；全都探不到返回 <c>null</c>。传进来的取消令牌会取消整条链。</summary>
    public static async Task<RegionLocation?> DetectAsync(CancellationToken cancellationToken = default)
    {
        foreach (var (source, url, parse) in Probes)
        {
            var hit = await RunAsync(source, url, parse, cancellationToken).ConfigureAwait(false);
            if (hit is not null) return hit;
        }

        return null;
    }

    private static async Task<RegionLocation?> RunAsync(
        string source, string url, Func<string, RegionLocation?> parse, CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(Timeout);

            var body = await NclHttp.Shared.GetStringAsync(url, timeout.Token).ConfigureAwait(false);
            return parse(body);
        }
        catch (Exception)
        {
            // 超时、被墙、返回垃圾 —— 都只是"这家不通"，换下一家。
            return null;
        }
    }

    /// <summary>
    /// ipip 的返回：<c>{"ret":"ok","data":{"ip":"…","location":["中国","天津","天津","","联通"]}}</c>。
    /// 数组里第 2 段是省 / 直辖市（直辖市会和第 3 段重复，去重），第 4 段常为空，第 5 段是运营商。
    /// </summary>
    private static RegionLocation? ParseIpIp(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("ret", out var ret) && ret.GetString() is { } status && status != "ok") return null;
        if (!root.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("location", out var location) ||
            location.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        foreach (var item in location.EnumerateArray())
        {
            if (item.GetString() is not { } text) continue;
            text = text.Trim();
            if (text.Length > 0 && !parts.Contains(text)) parts.Add(text);
        }

        if (parts.Count == 0) return null;

        var code = parts[0] switch
        {
            "中国" => "CN",
            "中国香港" or "香港" => "HK",
            "中国澳门" or "澳门" => "MO",
            "中国台湾" or "台湾" => "TW",
            _ => "",
        };

        // 没有 ISO 码说明是 ipip 认得的其它国家 —— 直接拿它给的中文名，比反查码表靠谱。
        var name = code.Length > 0 ? NameOf(code) : parts[0];
        return new RegionLocation(code, name, parts.Count > 1 ? parts[1] : null, "ipip");
    }

    /// <summary>Cloudflare 的 trace：纯文本 <c>key=value</c> 行，只认 <c>loc=</c> 那行。</summary>
    private static RegionLocation? ParseTrace(string body)
    {
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("loc=", StringComparison.Ordinal)) continue;

            var code = line[4..].Trim().ToUpperInvariant();

            // XX 是 Cloudflare 给"认不出来"的占位（匿名代理、部分机房段），当成没探到。
            if (code.Length != 2 || code == "XX") return null;

            return new RegionLocation(code, NameOf(code), null, "cloudflare");
        }

        return null;
    }

    /// <summary>ipwho.is：<c>{"success":true,"country":"China","country_code":"CN","city":"Tianjin"}</c>。</summary>
    private static RegionLocation? ParseIpWhoIs(string body)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        if (root.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False) return null;

        var code = root.TryGetProperty("country_code", out var codeElement)
            ? (codeElement.GetString() ?? "").Trim().ToUpperInvariant()
            : "";

        if (code.Length != 2) return null;

        var city = root.TryGetProperty("city", out var cityElement) ? cityElement.GetString() : null;
        var english = root.TryGetProperty("country", out var countryElement) ? countryElement.GetString() : null;

        // 名字优先按码本地化（中文界面就该显示「日本」而不是 "Japan"），查不到才退回对方给的英文名。
        var name = NameOf(code);
        if (name == code && !string.IsNullOrWhiteSpace(english)) name = english!;

        return new RegionLocation(code, name, string.IsNullOrWhiteSpace(city) ? null : city, "ipwho.is");
    }

    /// <summary>
    /// 两位码 → 显示名。<b>港澳台写全称</b>：CLDR 给的是「台湾」，这里统一盖成「中国台湾」，
    /// 其余按当前界面语言本地化（中文系统给「美国」而不是 "United States"）。
    /// 码不合法时原样返回码本身，调用方据此判断"没认出来"。
    /// </summary>
    public static string NameOf(string code) => code switch
    {
        "CN" => "中国大陆",
        "HK" => "中国香港",
        "MO" => "中国澳门",
        "TW" => "中国台湾",
        _ => LocalisedName(code),
    };

    private static string LocalisedName(string code)
    {
        try
        {
            // 用 BCL 那两个字母码反查显示名 —— 绝不要写成本文件里那个同名的探测结果类型。
            var region = new System.Globalization.RegionInfo(code);

            // DisplayName 按当前 UI 语言本地化；少数条目在 .NET 里是空的，退回它自己的叫法。
            var display = region.DisplayName;
            if (!string.IsNullOrWhiteSpace(display)) return display;

            return string.IsNullOrWhiteSpace(region.NativeName) ? code : region.NativeName;
        }
        catch (Exception)
        {
            // 不是有效的 ISO 3166-1 alpha-2（例如 Cloudflare 偶尔给的保留码）——
            // 它的构造函数会抛，这里不当作错误，交给界面显示码本身。
            return code;
        }
    }
}
