using System.Text.Json;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Nordicandia.Server.Services;

/// <summary>
/// Plain-HTTP/1.1 JSON leaderboard feed.
///
/// Why this exists: the shipped mobile (Android) client never calls the MagicOnion
/// <c>ILeaderboardServiceApi</c>; its whole leaderboard window is wired to a locally
/// synthesised "offline" board (<c>Nordicandia.Client.Offline.OfflineFakeLeaderboard</c>)
/// and the gRPC leaderboard methods are dead code in that build. To make the real,
/// cross-platform standings (Steam included) visible on Android, the patched client
/// fetches this JSON endpoint instead and renders those rows.
///
/// It is intentionally tiny and read-only, and reuses <see cref="LeaderboardQuery"/> so it
/// can never disagree with the gRPC path.
/// </summary>
internal static class LeaderboardHttp
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static void MapLeaderboardJson(this WebApplication app)
    {
        // Availability probe for the client / operators.
        app.MapGet("/api/leaderboards", () => Results.Json(new
        {
            modes = LeaderboardQuery.Modes.Select(m => LeaderboardQuery.Mode(m)).ToArray(),
            classes = LeaderboardQuery.Classes.Select(c => LeaderboardQuery.Cls(c)).ToArray(),
            categories = new[] { "overall", "class", "helheim" },
        }, Json));

        app.MapGet("/api/leaderboards/{mode}/{category}", Results<Ok<object>, NotFound<string>> (
            string mode, string category, string cls, int? limit, string player) =>
        {
            var name = BuildName(mode, category, cls);
            if (name == null) return TypedResults.NotFound($"unknown leaderboard {mode}/{category}/{cls}");
            var rows = LeaderboardQuery.Rows(name, limit ?? 50, player);
            return TypedResults.Ok<object>(new
            {
                leaderboardName = name,
                generatedAt = DateTime.UtcNow,
                count = rows.Count,
                rows,
            });
        });
    }

    /// <summary>Maps (mode, category, class) onto the same canonical names the game itself uses.</summary>
    private static string BuildName(string mode, string category, string cls)
    {
        if (string.IsNullOrWhiteSpace(mode)) return null;
        var m = mode.Trim().ToLowerInvariant();
        if (LeaderboardQuery.Modes.All(x => LeaderboardQuery.Mode(x) != m)) return null;

        var c = (category ?? "overall").Trim().ToLowerInvariant();
        return c switch
        {
            "overall" => $"character_level_overall_{m}",
            "helheim" => $"helheim_depth_{m}",
            "class" => ClassName(cls, m),
            _ => null,
        };
    }

    private static string ClassName(string cls, string mode)
    {
        if (string.IsNullOrWhiteSpace(cls)) return null;
        var c = cls.Trim().ToLowerInvariant();
        if (LeaderboardQuery.Classes.All(x => LeaderboardQuery.Cls(x) != c)) return null;
        return $"character_level_{c}_{mode}";
    }
}
