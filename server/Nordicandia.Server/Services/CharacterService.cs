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

    // The client pushes its per-character filter set and its account-wide shared filters
    // whenever the player edits the loot filter. Persisting both is what stops the filter
    // from resetting to default on the next login.
    public UnaryResult<UpdateItemFilterResponse> UpdateItemFilter(UpdateItemFilterRequest req)
    {
        var owner = Owner;
        if (req?.CharacterFilters != null)
            GameStore.Instance.SaveCharacterLootFilters(owner, req.CharacterId, req.CharacterFilters);
        if (req?.UserFilters != null)
            GameStore.Instance.SaveUserLootFilters(owner, req.UserFilters);
        Console.WriteLine($"[FILTER] char={req?.CharacterId} charFilters={(req?.CharacterFilters?.Filters?.Filters?.Count ?? 0)} userFilters={(req?.UserFilters?.Filters?.Count ?? 0)}");
        return UnaryResult.FromResult(new UpdateItemFilterResponse());
    }
}
