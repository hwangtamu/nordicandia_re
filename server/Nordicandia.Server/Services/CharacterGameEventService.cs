using MagicOnion;
using SharedNet.Api;
using Nordicandia.Server.State;

namespace Nordicandia.Server.Services;

public sealed partial class CharacterGameEventServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    // The client only uploads area progress through these game-event calls. Every mode
    // carries worldTier/worldWaypoint, so record whichever one the player is running; this
    // is what makes the "highest area level" survive a relogin.

    public UnaryResult<CharacterDungeonRunStartedResponse> OnDungeonRunStarted(CharacterDungeonRunStartedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterDungeonRunStartedResponse());
    }

    public UnaryResult<CharacterDungeonRunCompletedResponse> OnDungeonRunCompleted(CharacterDungeonRunCompletedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, req?.Duration ?? default);
        return UnaryResult.FromResult(new CharacterDungeonRunCompletedResponse());
    }

    public UnaryResult<CharacterNiflheimStartedResponse> OnNiflheimStarted(CharacterNiflheimStartedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterNiflheimStartedResponse());
    }

    public UnaryResult<CharacterNiflheimCompletedResponse> OnNiflheimCompleted(CharacterNiflheimCompletedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterNiflheimCompletedResponse());
    }

    public UnaryResult<CharacterHelheimStartedResponse> OnHelheimStarted(CharacterHelheimStartedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default, req?.Depth ?? 0);
        return UnaryResult.FromResult(new CharacterHelheimStartedResponse());
    }

    public UnaryResult<CharacterHelheimCompletedResponse> OnHelheimCompleted(CharacterHelheimCompletedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default, req?.Depth ?? 0);
        return UnaryResult.FromResult(new CharacterHelheimCompletedResponse());
    }

    public UnaryResult<CharacterOdrsTrailStartedResponse> OnOdrsTrailStarted(CharacterOdrsTrailStartedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterOdrsTrailStartedResponse());
    }

    public UnaryResult<CharacterOdrsTrailCompletedResponse> OnOdrsTrailCompleted(CharacterOdrsTrailCompletedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterOdrsTrailCompletedResponse());
    }

    public UnaryResult<CharacterVanaheimStartedResponse> OnVanaheimStarted(CharacterVanaheimStartedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterVanaheimStartedResponse());
    }

    public UnaryResult<CharacterVanaheimCompletedResponse> OnVanaheimCompleted(CharacterVanaheimCompletedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterVanaheimCompletedResponse());
    }

    public UnaryResult<CharacterDiedResponse> OnCharacterDied(CharacterDiedRequest req)
    {
        Progress(req?.CharacterId ?? Guid.Empty, req?.WorldTier ?? 0, req?.WorldWaypoint ?? 0, default);
        return UnaryResult.FromResult(new CharacterDiedResponse());
    }

    private void Progress(Guid characterId, int tier, int waypoint, TimeSpan duration, int depth = 0)
    {
        if (characterId == Guid.Empty || (tier <= 0 && waypoint <= 0 && depth <= 0)) return;
        Console.WriteLine($"[PROG] {Context.CallContext.Method} character={characterId} tier={tier} waypoint={waypoint} depth={depth}");
        GameStore.Instance.ApplyWorldProgress(Owner, characterId, tier, waypoint, duration, depth);
    }
}
