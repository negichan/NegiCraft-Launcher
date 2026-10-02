using System;
using System.Text.RegularExpressions;

namespace NegiCraftLauncher.Core.Auth;

public enum GameAccountType
{
    Offline,
    Microsoft,
}

/// <summary>
/// A playable identity. Offline accounts are fully local; Microsoft accounts carry a real
/// access token and an expiry so the UI can tell the user when to re-authenticate.
/// </summary>
public sealed class GameAccount
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public GameAccountType Type { get; set; } = GameAccountType.Offline;
    public string AccessToken { get; set; } = "";
    public DateTime? ExpiresAt { get; set; }

    public string TypeName => Type switch
    {
        GameAccountType.Microsoft => "微软账户",
        _ => "离线账户",
    };

    public bool IsExpired => ExpiresAt is { } expiry && expiry <= DateTime.UtcNow;

    /// <summary>Dashed form; Minecraft accepts either, but the official launcher emits dashes.</summary>
    public string Uuid => Id;

    public string UuidHex => Id.Replace("-", "");
}

public static partial class OfflineAccountFactory
{
    // The token is never validated for offline play, it only has to be non-empty.
    private const string OfflineAccessToken = "0";

    public static GameAccount Create(string playerName)
    {
        if (!IsValidPlayerName(playerName))
        {
            throw new ArgumentException("玩家名需为 3-16 位的字母、数字或下划线", nameof(playerName));
        }

        return new GameAccount
        {
            Id = OfflineAuthenticator.GenerateOfflineUuid(playerName).ToString(),
            Name = playerName,
            Type = GameAccountType.Offline,
            AccessToken = OfflineAccessToken,
        };
    }

    public static bool IsValidPlayerName(string? name) =>
        !string.IsNullOrWhiteSpace(name) && PlayerNamePattern().IsMatch(name);

    [GeneratedRegex(@"^[A-Za-z0-9_]{3,16}$")]
    private static partial Regex PlayerNamePattern();
}
