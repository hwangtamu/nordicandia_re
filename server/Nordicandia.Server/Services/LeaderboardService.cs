using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MagicOnion;
using SharedNet.Api;
using SharedNet.Constants;
using SharedNet.Dto;
using SharedNet.Dto.Realtime;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

/// <summary>
/// Serves the level leaderboards the client asks for. The client derives the leaderboard
/// name locally (<c>WindowLeaderboard.UpdateCurrentLeaderboardName</c>) as
/// <c>character_level_overall_{mode}</c>, <c>character_level_{class}_{mode}</c> or
/// <c>helheim_depth_{mode}</c>, so the server must publish exactly those names.
/// </summary>
public sealed partial class LeaderboardServiceApiImpl
{
    private static readonly SharedNet.Constants.Game.GameMode[] Modes =
    {
        SharedNet.Constants.Game.GameMode.Normal,
        SharedNet.Constants.Game.GameMode.NormalHardcore,
        SharedNet.Constants.Game.GameMode.Season,
        SharedNet.Constants.Game.GameMode.SeasonHardcore,
        SharedNet.Constants.Game.GameMode.Challenge,
        SharedNet.Constants.Game.GameMode.ChallengeHardcore,
    };

    private static readonly SharedNet.Constants.Game.CharacterClass[] Classes =
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

    private static string Mode(SharedNet.Constants.Game.GameMode mode) => mode.ToString().ToLowerInvariant();
    private static string Cls(SharedNet.Constants.Game.CharacterClass cls) => cls.ToString().ToLowerInvariant();

    public UnaryResult<GetRelevantLeaderboardsAndTournamentsResponse> GetRelevantLeaderboards(GetRelevantLeaderboardsAndTournamentsRequest req)
    {
        var now = DateTime.UtcNow;
        var leaderboards = new List<LeaderboardDto>();
        foreach (var mode in Modes)
        {
            leaderboards.Add(Define($"character_level_overall_{Mode(mode)}", "Overall Level", mode, 0, null, now));
            foreach (var cls in Classes)
                leaderboards.Add(Define($"character_level_{Cls(cls)}_{Mode(mode)}", $"{cls} Level", mode, 1, (short)cls, now));
            leaderboards.Add(Define($"helheim_depth_{Mode(mode)}", "Helheim Depth", mode, 2, null, now));
        }

        return UnaryResult.FromResult(new GetRelevantLeaderboardsAndTournamentsResponse
        {
            LeaderboardList = new LeaderboardListDto { Leaderboards = leaderboards, Cursor = null },
            TournamentList = new TournamentListDto { Tournaments = new List<TournamentDto>(), Cursor = null },
        });
    }

    private static LeaderboardDto Define(string name, string title, SharedNet.Constants.Game.GameMode mode, short category,
        short? classIntegerId, DateTime now) => new()
    {
        Id = DeterministicGuid(name),
        Name = name,
        SortOrder = SharedNet.Constants.LeaderboardSortOrder.Descending,
        Operator = SharedNet.Constants.LeaderboardOperator.Best,
        ScoreOwnerType = SharedNet.Constants.LeaderboardScoreOwnerType.Character,
        CreateTime = now,
        GameMode = mode,
        Category = category,
        Subcategory = classIntegerId ?? 0,
        Metadata = JsonSerializer.Serialize(new { classIntegerId = classIntegerId ?? -1, isHardcoreDead = false }),
        PrevReset = now.AddDays(-7),
        NextReset = now.AddDays(7),
    };

    public UnaryResult<ListLeaderboardRecordsResponse> ListLeaderboardRecords(ListLeaderboardRecordsRequest req)
        => UnaryResult.FromResult(new ListLeaderboardRecordsResponse { Records = Records(req?.LeaderboardName, req?.Limit) });

    public UnaryResult<ListLeaderboardRecordsAroundOwnerResponse> ListLeaderboardRecordsAroundOwner(ListLeaderboardRecordsAroundOwnerRequest req)
        => UnaryResult.FromResult(new ListLeaderboardRecordsAroundOwnerResponse
        {
            Records = AroundOwner(req?.LeaderboardName, req?.OwnerId ?? Guid.Empty, req?.Limit),
        });

    private static LeaderboardRecordListDto Records(string name, int? limit)
    {
        var (mode, classFilter) = Parse(name);
        var ranked = GameStore.Instance.Standings()
            .Where(s => mode == null || s.GameMode == mode)
            .Where(s => classFilter == null || s.Class == classFilter)
            .OrderByDescending(s => s.Level)
            .ThenByDescending(s => s.Experience)
            .ToList();

        var records = ranked.Take(Math.Clamp(limit ?? 50, 1, 200))
            .Select((s, i) => ToRecord(name, s, i + 1))
            .ToList();

        return new LeaderboardRecordListDto
        {
            Records = records,
            OwnerRecords = new List<LeaderboardRecordDto>(),
            NextCursor = null,
            PrevCursor = null,
        };
    }

    private static LeaderboardRecordListDto AroundOwner(string name, Guid ownerId, int? limit)
    {
        var (mode, classFilter) = Parse(name);
        var ranked = GameStore.Instance.Standings()
            .Where(s => mode == null || s.GameMode == mode)
            .Where(s => classFilter == null || s.Class == classFilter)
            .OrderByDescending(s => s.Level)
            .ThenByDescending(s => s.Experience)
            .ToList();

        var index = ranked.FindIndex(s => s.CharacterId == ownerId || s.Owner == ownerId);
        if (index < 0) index = 0;
        var half = Math.Clamp((limit ?? 20) / 2, 1, 50);
        var start = Math.Max(0, index - half);
        var window = ranked.Skip(start).Take(half * 2 + 1).ToList();

        return new LeaderboardRecordListDto
        {
            Records = window.Select((s, i) => ToRecord(name, s, start + i + 1)).ToList(),
            OwnerRecords = window.Where(s => s.CharacterId == ownerId || s.Owner == ownerId)
                .Select(s => ToRecord(name, s, ranked.IndexOf(s) + 1)).ToList(),
            NextCursor = null,
            PrevCursor = null,
        };
    }

    private static LeaderboardRecordDto ToRecord(string name, GameStore.CharacterStanding s, long rank) => new()
    {
        LeaderboardId = DeterministicGuid(name),
        OwnerId = s.CharacterId,
        OwnerDisplayName = s.DisplayName,
        // The client renders character_level scores as exp(score / 1_000_000), i.e. the
        // score is a log-encoded level (see WindowLeaderboard.CreateLeaderboardRow).
        // Sending the raw level made every row read "Level 1".
        Score = EncodeScore(name, s),
        Subscore = (long)s.Experience,
        NumScores = 1,
        Metadata = JsonSerializer.Serialize(new { classIntegerId = (int)s.Class, isHardcoreDead = false }),
        CreateTime = s.LastLogin,
        UpdateTime = s.LastLogin,
        Rank = rank,
        LeaderboardName = name,
        OwnerType = SharedNet.Constants.LeaderboardScoreOwnerType.Character,
    };

    private static long EncodeScore(string name, GameStore.CharacterStanding s)
    {
        if (name != null && name.StartsWith("character_level", StringComparison.Ordinal))
            return (long)Math.Round(1_000_000.0 * Math.Log(Math.Max(1.0, s.Level)));
        if (name != null && name.StartsWith("helheim_depth", StringComparison.Ordinal))
            return (long)s.WorldTier * 1_000_000 + (long)s.WorldWaypoint * 1_000;
        return (long)Math.Round(s.Level);
    }

    private static (SharedNet.Constants.Game.GameMode? mode, SharedNet.Constants.Game.CharacterClass? cls) Parse(string name)
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

    private static Guid DeterministicGuid(string name)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("nordicandia.lb." + name));
        return new Guid(hash);
    }
}
