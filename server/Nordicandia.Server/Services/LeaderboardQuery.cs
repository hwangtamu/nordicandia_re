using System.Security.Cryptography;
using System.Text;
using SharedNet.Dto;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Single source of truth for leaderboard ranking. Both the MagicOnion
/// <see cref="LeaderboardServiceApiImpl"/> and the plain-HTTP JSON endpoint used by the
/// mobile client read the leaderboard through here, so the two can never drift apart.
/// </summary>
internal static class LeaderboardQuery
{
    public static readonly SharedNet.Constants.Game.GameMode[] Modes =
    {
        SharedNet.Constants.Game.GameMode.Normal,
        SharedNet.Constants.Game.GameMode.NormalHardcore,
        SharedNet.Constants.Game.GameMode.Season,
        SharedNet.Constants.Game.GameMode.SeasonHardcore,
        SharedNet.Constants.Game.GameMode.Challenge,
        SharedNet.Constants.Game.GameMode.ChallengeHardcore,
    };

    public static readonly SharedNet.Constants.Game.CharacterClass[] Classes =
    {
        SharedNet.Constants.Game.CharacterClass.Warrior,
        SharedNet.Constants.Game.CharacterClass.Paladin,
        SharedNet.Constants.Game.CharacterClass.Assassin,
        SharedNet.Constants.Game.CharacterClass.Barbarian,
        SharedNet.Constants.Game.CharacterClass.Hunter,
        SharedNet.Constants.Game.CharacterClass.Mage,
        SharedNet.Constants.Game.CharacterClass.Necromancer,
        SharedNet.Constants.Game.CharacterClass.Priest,
    };

    public static string Mode(SharedNet.Constants.Game.GameMode mode) => mode.ToString().ToLowerInvariant();
    public static string Cls(SharedNet.Constants.Game.CharacterClass cls) => cls.ToString().ToLowerInvariant();

    /// <summary>A row ready to be serialised to the client.</summary>
    /// <remarks><c>CharacterId</c> is what lets the client open the "inspect player" panel
    /// when a leaderboard row is tapped; it is emitted to the JSON feed only.</remarks>
    public sealed record Row(long Rank, string Name, double Level, long Score, int ClassId, bool IsPlayer, Guid CharacterId);

    /// <summary>Ranked standings for a leaderboard name (identical filter/sort to the gRPC path).</summary>
    public static List<GameStore.CharacterStanding> Ranked(string name)
    {
        var (mode, classFilter) = Parse(name);
        return GameStore.Instance.Standings()
            .Where(s => mode == null || s.GameMode == mode)
            .Where(s => classFilter == null || s.Class == classFilter)
            .OrderByDescending(s => s.Level)
            .ThenByDescending(s => s.Experience)
            .ToList();
    }

    public static List<Row> Rows(string name, int limit, string substituteFor = null)
    {
        var ranked = Ranked(name);
        return ranked
            .Take(Math.Clamp(limit, 1, 200))
            .Select((s, i) => new Row(
                i + 1,
                string.Equals(s.DisplayName, substituteFor, StringComparison.OrdinalIgnoreCase) ? substituteFor : s.DisplayName,
                s.Level,
                EncodeScore(name, s),
                (int)s.Class,
                substituteFor != null && string.Equals(s.DisplayName, substituteFor, StringComparison.OrdinalIgnoreCase),
                s.CharacterId))
            .ToList();
    }

    public static long EncodeScore(string name, GameStore.CharacterStanding s)
    {
        if (name != null && name.StartsWith("character_level", StringComparison.Ordinal))
            return (long)Math.Round(1_000_000.0 * Math.Log(Math.Max(1.0, s.Level)));
        if (name != null && name.StartsWith("helheim_depth", StringComparison.Ordinal))
            return (long)s.WorldTier * 1_000_000 + (long)s.WorldWaypoint * 1_000;
        return (long)Math.Round(s.Level);
    }

    public static (SharedNet.Constants.Game.GameMode? mode, SharedNet.Constants.Game.CharacterClass? cls) Parse(string name)
    {
        if (string.IsNullOrEmpty(name)) return (null, null);
        SharedNet.Constants.Game.GameMode? mode = null;
        foreach (var m in Modes)
            if (name.EndsWith("_" + Mode(m), StringComparison.Ordinal)) { mode = m; break; }

        SharedNet.Constants.Game.CharacterClass? cls = null;
        if (name.StartsWith("character_level_", StringComparison.Ordinal) && !name.StartsWith("character_level_overall_", StringComparison.Ordinal))
            foreach (var c in Classes)
                if (name.Contains("_" + Cls(c) + "_", StringComparison.Ordinal)) { cls = c; break; }

        return (mode, cls);
    }

    public static Guid DeterministicGuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("nordicandia.lb." + name));
        return new Guid(hash);
    }
}
