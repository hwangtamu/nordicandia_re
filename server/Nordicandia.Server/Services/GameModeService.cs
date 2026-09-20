using Game;
using MagicOnion;
using MagicOnion.Server;
using Nordicandia.Server.State;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

/// <summary>
/// Season support. The client treats a season as joinable when
/// <c>CurrentSeasonStart.AddHours(-1) &lt;= now &lt; CurrentSeasonEnd</c>
/// (see <c>WindowSelectGameMode.UpdateButtonStatus</c>), and it stores the response in
/// <c>OnlineData.SeasonInfo</c> which the season UI and the "Season Ends In" timer read.
///
/// We publish a deterministic season wheel (365-day seasons anchored at <see cref="Epoch"/>)
/// so a season is always active without persisting a schedule. Character creation for
/// <c>GameMode.Season</c>/<c>SeasonHardcore</c> is unlocked in <c>GameStore.CreateCharacter</c>.
/// </summary>
public sealed class GameModeServiceApiImpl : ServiceBase<IGameModeServiceApi>, IGameModeServiceApi
{
    private static (string Name, DateTime Start, DateTime End) SeasonAt(DateTime utc)
    {
        var (_, name, start, end) = GameStore.SeasonAt(utc);
        return (name, start, end);
    }

    public UnaryResult<GetSeasonInfoResponse> GetSeasonInfo(GetSeasonInfoRequest req)
    {
        var now = DateTime.UtcNow;
        var current = SeasonAt(now);
        var previous = SeasonAt(current.Start - TimeSpan.FromTicks(1));
        var next = SeasonAt(current.End + TimeSpan.FromTicks(1));

        Console.WriteLine($"[SEASON] now={now:u} current={current.Name} {current.Start:u}..{current.End:u}");

        return UnaryResult.FromResult(new GetSeasonInfoResponse
        {
            PrevSeasonName = previous.Name,
            PreviousSeasonStart = previous.Start,
            PreviousSeasonEnd = previous.End,
            CurrentSeasonName = current.Name,
            CurrentSeasonStart = current.Start,
            CurrentSeasonEnd = current.End,
            NextSeasonName = next.Name,
            NextSeasonStart = next.Start,
            NextSeasonEnd = next.End,
        });
    }

    public UnaryResult<ClaimSeasonRewardResponse> ClaimSeasonReward(ClaimSeasonRewardRequest req)
    {
        var owner = GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));
        // The client sends the reward milestone's combined level (seasonNumber*1000 + level),
        // the value it parsed from the reward name's first two parts. Strip the season prefix
        // to get the plain track level, but keep the combined value as the claim key so the
        // client's ClaimedSeasonRewardsByLevel comparison matches.
        var combined = req?.SeasonLevelReward ?? 0;
        var level = combined % 1000;
        var pass = req?.IsSeasonPassReward ?? false;
        var empty = new ClaimSeasonRewardResponse
        {
            ReceivedRewards = new Game.SerializedItems { Items = new() },
            NewOpals = 0,
            IsSeasonPassReward = pass,
        };
        // Only award levels the player has actually reached and has not already claimed.
        var maxLevel = Math.Min(GameStore.Instance.SeasonLevel(owner), CatalogServiceApiImpl.SeasonRewardLevels);
        if (level <= 0 || level > maxLevel) return UnaryResult.FromResult(empty);
        if (!GameStore.Instance.ClaimSeasonReward(owner, combined, pass)) return UnaryResult.FromResult(empty);
        var item = CatalogServiceApiImpl.CreateSeasonRewardItem(level, pass);
        var granted = GameStore.Instance.GrantItems(owner, req.CharacterId, new List<Game.SerializedItem> { item });
        Console.WriteLine($"[SEASON] claimed level={level} pass={pass} items={granted.Count}");
        return UnaryResult.FromResult(new ClaimSeasonRewardResponse
        {
            ReceivedRewards = new Game.SerializedItems { Items = granted },
            NewOpals = 0,
            IsSeasonPassReward = pass,
        });
    }
}
