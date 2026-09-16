using MagicOnion;
using SharedNet.Api;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class SocialServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    /// <summary>
    /// Public character card used by the leaderboard "inspect" panel and the party/social
    /// views. Returns the target's equipped items, stats and world progression so the client
    /// can render the character's gear.
    /// </summary>
    public UnaryResult<InspectCharacterResponse> InspectCharacter(InspectCharacterRequest req)
    {
        _ = Owner;
        var target = req?.InspectTargetCharacterId ?? Guid.Empty;
        if (target == Guid.Empty) target = req?.CharacterId ?? Guid.Empty;
        var inspection = GameStore.Instance.Inspection(target);
        if (inspection == null) throw new Grpc.Core.RpcException(new Grpc.Core.Status(Grpc.Core.StatusCode.NotFound, "Character not found"));
        return UnaryResult.FromResult(new InspectCharacterResponse { InspectionData = inspection });
    }
}
