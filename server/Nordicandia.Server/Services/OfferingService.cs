using MagicOnion;
using MagicOnion.Server;
using Nordicandia.Server.Realtime;
using Nordicandia.Server.State;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

public sealed partial class OfferingServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));

    /// <summary>
    /// Aesir-offering blessings. A "no blessing" slot must be null: an earlier stub used
    /// <see cref="Defaults.Create{T}"/>, which recursively instantiated the nested
    /// <c>SerializedBuff</c> properties with <c>DefinitionIntegerId == 0</c> — and definition 0
    /// is <c>MightBuff</c>, so the client applied a permanent Might buff to every character.
    ///
    /// Each made offering now persists an expiry (<see cref="GameStore.MakeOffering"/>) and
    /// this call returns the still-active blessings (definition ids 276/278/280/282); expired
    /// ones are pruned.
    /// </summary>
    public UnaryResult<GetCurrentBlessingsResponse> GetCurrentBlessings(GetCurrentBlessingsRequest req)
    {
        var blessings = GameStore.Instance.GetActiveBlessings(Owner, req?.CharacterId ?? Guid.Empty);
        return UnaryResult.FromResult(new GetCurrentBlessingsResponse
        {
            BlessingOdin = blessings.Odin,
            BlessingTyr = blessings.Tyr,
            BlessingFrigg = blessings.Frigg,
            BlessingThor = blessings.Thor,
        });
    }

    public UnaryResult<MakeOfferingResponse> MakeOffering(MakeOfferingRequest req)
    {
        var owner = Owner;
        var characterId = req?.CharacterId ?? Guid.Empty;
        var offeringType = (int)(req?.OfferingType ?? AesirOfferingTypes.Unknown);
        var offeringSize = (int)(req?.OfferingSize ?? AesirOfferingSizes.Unknown);
        var result = GameStore.Instance.MakeOffering(owner, characterId, offeringType, offeringSize, req?.OfferedOpals ?? 0);
        if (result.Applied)
        {
            var name = GameStore.Instance.CharacterDisplayName(owner, characterId);
            RealtimeGateway.AnnounceOffering(name, offeringType, req?.IsAnonymous ?? false);
        }
        return UnaryResult.FromResult(new MakeOfferingResponse { NewOpals = result.NewOpals });
    }
}