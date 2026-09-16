using MagicOnion;
using Nordicandia.Server.State;
using SharedNet.Api;

namespace Nordicandia.Server.Services;

public sealed partial class CharacterServiceApiImpl
{
    private Guid Owner => GameStore.Instance.RequireUser(Context.CallContext.RequestHeaders.GetValue("authorization"));
    public UnaryResult<CreateCharacterResponse> CreateCharacter(CreateCharacterRequest req)
        => UnaryResult.FromResult(new CreateCharacterResponse { Character = GameStore.Instance.CreateCharacter(Owner, req) });
    public UnaryResult<GetCharacterListResponse> GetCharacterList(GetCharacterListRequest req)
        => UnaryResult.FromResult(new GetCharacterListResponse { Characters = GameStore.Instance.Characters(Owner) });
    public UnaryResult<EnterGameWithCharacterResponse> EnterGameWithCharacter(EnterGameWithCharacterRequest req)
        => UnaryResult.FromResult(GameStore.Instance.Enter(Owner, req.CharacterId));
    public UnaryResult<AllocateCharacterAttributesResponse> AllocateCharacterAttributes(AllocateCharacterAttributesRequest req)
    {
        var owner = Owner;
        var r = GameStore.Instance.ApplyAllocatedAttributes(owner, req.CharacterId, req);
        // The response is authoritative for the allocation UI, so echo the *accumulated* totals
        // (the request only carried the pending delta) and the remaining pool.
        return UnaryResult.FromResult(new AllocateCharacterAttributesResponse
        {
            AttributePoints = r.Available,
            TotalAllocatedStrength = r.Strength,
            TotalAllocatedDexterity = r.Dexterity,
            TotalAllocatedIntelligence = r.Intelligence,
            TotalAllocatedVitality = r.Vitality,
            TotalAllocatedConstitution = r.Constitution,
            TotalAllocatedAgility = r.Agility,
            TotalAllocatedMindpower = r.Mindpower,
        });
    }
    public UnaryResult<DeleteCharacterResponse> DeleteCharacter(DeleteCharacterRequest req)
    {
        GameStore.Instance.Delete(Owner, req.CharacterId);
        return UnaryResult.FromResult(new DeleteCharacterResponse());
    }
}
