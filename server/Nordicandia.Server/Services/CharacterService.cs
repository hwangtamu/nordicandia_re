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

    public UnaryResult<UpdateCharacterSettingsResponse> UpdateCharacterSettings(UpdateCharacterSettingsRequest req)
    {
        GameStore.Instance.SaveCharacterSettings(Owner, req.CharacterId, req.LowRarityThreshold, req.HighRarityThreshold);
        return UnaryResult.FromResult(new UpdateCharacterSettingsResponse());
    }

    public UnaryResult<UpdatePetResponse> UpdatePet(UpdatePetRequest req)
    {
        GameStore.Instance.UpdatePet(Owner, req.CharacterId, req.PetDefinitionIntegerId);
        return UnaryResult.FromResult(new UpdatePetResponse());
    }

    public UnaryResult<UnlockPetResponse> UnlockPet(UnlockPetRequest req)
    {
        var opals = GameStore.Instance.UnlockPet(Owner, req.CharacterId, req.PetDefinitionIntegerId, req.OpalCost);
        Console.WriteLine($"[PET] unlock def={req.PetDefinitionIntegerId} cost={req.OpalCost} opals={opals}");
        return UnaryResult.FromResult(new UnlockPetResponse { NewOpals = opals });
    }

    public UnaryResult<UpdateCombatPetResponse> UpdateCombatPet(UpdateCombatPetRequest req)
    {
        GameStore.Instance.UpdateCombatPet(Owner, req.CharacterId, req.CombatPetDefinitionIntegerId);
        return UnaryResult.FromResult(new UpdateCombatPetResponse());
    }

    public UnaryResult<UnlockCombatPetResponse> UnlockCombatPet(UnlockCombatPetRequest req)
    {
        var result = GameStore.Instance.UnlockCombatPet(Owner, req.CharacterId, req.PayWithOpals, req.CombatPetDefinitionIntegerId, req.Cost);
        Console.WriteLine($"[PET] unlock combat def={req.CombatPetDefinitionIntegerId} opals={req.PayWithOpals} cost={req.Cost} balance={result.NewCurrency}");
        return UnaryResult.FromResult(new UnlockCombatPetResponse { NewCurrencyValue = result.NewCurrency });
    }

    public UnaryResult<CheckCombatPetStateResponse> CheckCombatPetState(CheckCombatPetStateRequest req)
    {
        var state = GameStore.Instance.CheckCombatPet(Owner, req.CharacterId, req.PetDefinitionId);
        var respawn = state.LastDeath?.AddMinutes(30);
        return UnaryResult.FromResult(new CheckCombatPetStateResponse
        {
            IsAlive = state.IsAlive,
            RespawnAvailableAtUtc = respawn,
            ServerTimeUtc = DateTime.UtcNow,
            RemainingSeconds = respawn is { } r ? Math.Max(0, (r - DateTime.UtcNow).TotalSeconds) : 0,
            AdsLeftToWatch = state.AdsLeft,
        });
    }

    public UnaryResult<OnCombatPetAdWatchResponse> OnCombatPetAdWatch(OnCombatPetAdWatchRequest req)
    {
        GameStore.Instance.MutateCombatPet(Owner, req.CharacterId, req.PetDefinitionId,
            p => { if (p.AdsLeftToWatch > 0) p.AdsLeftToWatch--; });
        var state = GameStore.Instance.CheckCombatPet(Owner, req.CharacterId, req.PetDefinitionId);
        return UnaryResult.FromResult(new OnCombatPetAdWatchResponse { AdsLeftToWatch = state.AdsLeft });
    }

    public UnaryResult<ResurrectCombatPetResponse> ResurrectCombatPet(ResurrectCombatPetRequest req)
    {
        GameStore.Instance.MutateCombatPet(Owner, req.CharacterId, req.PetDefinitionId,
            p => { p.IsAlive = true; p.LastDeathTime = null; });
        return UnaryResult.FromResult(new ResurrectCombatPetResponse());
    }

    public UnaryResult<OnPetDiedResponse> OnPetDied(OnPetDiedRequest req)
    {
        GameStore.Instance.MutateCombatPet(Owner, req.CharacterId, req.PetDefinitionId,
            p => { p.IsAlive = false; p.LastDeathTime = DateTime.UtcNow; });
        return UnaryResult.FromResult(new OnPetDiedResponse());
    }

    public UnaryResult<OnPetExpPotionConsumedResponse> OnPetExpPotionConsumed(OnPetExpPotionConsumedRequest req)
    {
        GameStore.Instance.MutateCombatPet(Owner, req.CharacterId, req.PetDefinitionId, p => p.Experience += 100);
        return UnaryResult.FromResult(new OnPetExpPotionConsumedResponse());
    }
}
