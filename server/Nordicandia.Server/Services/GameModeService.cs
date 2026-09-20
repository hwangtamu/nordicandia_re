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
    private static readonly DateTime Epoch = new(2024, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly TimeSpan Length = TimeSpan.FromDays(365);

    private static (string Name, DateTime Start, DateTime End) SeasonAt(DateTime utc)
    {
        var index = (long)Math.Floor((utc - Epoch).Ticks / (double)Length.Ticks);
        var start = Epoch.AddTicks(index * Length.Ticks);
        return ($"Season {index + 1}", start, start + Length);
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
        var level = req?.SeasonLevelReward ?? 0;
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
        if (!GameStore.Instance.ClaimSeasonReward(owner, level, pass)) return UnaryResult.FromResult(empty);
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
