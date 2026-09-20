using MagicOnion;
using Nordicandia.Server.State;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

public sealed partial class UserServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    /// <summary>
    /// The season reward UI reads the player's season level and claim state here. The level
    /// is the highest Season / Season-Hardcore character level on the account.
    /// </summary>
    public UnaryResult<GetSeasonMetadataResponse> GetSeasonMetadata(GetSeasonMetadataRequest req)
    {
        var season = GameStore.Instance.GetSeasonData(Owner);
        Console.WriteLine($"[SEASON] metadata level={season.SeasonLevel} claimedRegular={season.ClaimedSeasonRewardsByLevel.Count} claimedPass={season.ClaimedSeasonPassRewardsByLevel.Count}");
        return UnaryResult.FromResult(new GetSeasonMetadataResponse { SeasonMetadata = season });
    }
}