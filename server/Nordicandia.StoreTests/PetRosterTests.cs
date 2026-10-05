using Game;
using Grpc.Core;
using Nordicandia.Server.State;
using Nordicandia.Server.WebApi;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class PetRosterTests
{
    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-pet-roster-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        void Reject(Action action)
        {
            try { action(); }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.FailedPrecondition) { return; }
            throw new Exception("Expected pet purchase rejection");
        }
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir))
            {
                owner = store.GetOrCreateUser("device:pet-roster-test").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "pet-roster", CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;

                var petCrow = PetCatalog.ByIntegerId(98)!;
                var durnir = PetCatalog.ByIntegerId(186)!;
                var hala = PetCatalog.ByIntegerId(188)!;
                var companionPrices = PetCatalog.CompanionPets.Where(p => p.UnlockCost.HasValue).ToList();
                var expectedCombatPrices = new Dictionary<int, (string Currency, int Cost)>
                {
                    [186] = ("SL", 6_000_000), [188] = ("OP", 1_000), [189] = ("SL", 6_000_000),
                    [190] = ("OP", 1_200), [191] = ("OP", 1_000),
                };
                Check(PetCatalog.CompanionPets.Count() == 8 && companionPrices.Count == 5
                    && companionPrices.All(p => p.UnlockCurrency == "OP" && p.UnlockCost == 600)
                    && PetCatalog.CombatPets.Count() == 5 && expectedCombatPrices.All(pair => PetCatalog.ByIntegerId(pair.Key) is { } p
                        && p.UnlockCurrency == pair.Value.Currency && p.UnlockCost == pair.Value.Cost),
                    "pet purchase prices recovered from PetRow affix attributes");
                Check(durnir.Effects.Any(e => e.AttributeId == 971 && e.Value == 5)
                    && hala.Effects.Any(e => e.AttributeId == 974 && e.Value == 0.5)
                    && PetCatalog.ByIntegerId(190)!.Effects.Any(e => e.AttributeId == 972 && e.Value == 0.01),
                    "combat-pet passive bonus affixes exported from monster data");
                store.SaveRealtimeProgress(owner, id, 0, durnir.UnlockCost.Value, petCrow.UnlockCost.Value);
                var forgedPriceRejected = false;
                try { store.UnlockPet(owner, id, petCrow.IntegerId, 0); }
                catch (RpcException ex) when (ex.StatusCode == StatusCode.InvalidArgument) { forgedPriceRejected = true; }
                Check(forgedPriceRejected, "pet purchase rejects client-supplied underpricing");
                store.UnlockPet(owner, id, petCrow.IntegerId, petCrow.UnlockCost.Value);
                store.UnlockCombatPet(owner, id, false, durnir.IntegerId, durnir.UnlockCost.Value);
                Check(store.GetRealtimeProgress(owner, id).Opals == 0 && store.GetRealtimeProgress(owner, id).Silver == 0,
                    "pet purchases charge the recovered currency and exact price");
                store.UnlockPet(owner, id, petCrow.IntegerId, petCrow.UnlockCost.Value);
                store.UnlockCombatPet(owner, id, false, durnir.IntegerId, durnir.UnlockCost.Value);
                Check(store.GetRealtimeProgress(owner, id).Opals == 0 && store.GetRealtimeProgress(owner, id).Silver == 0
                    && store.GetPetRoster(owner, id).PetDefinitionIntegerIds.Count == 1
                    && store.GetPetRoster(owner, id).CombatPets.Count == 1,
                    "repeated pet purchases are idempotent");
                Reject(() => store.UnlockCombatPet(owner, id, true, hala.IntegerId, hala.UnlockCost.Value));
                Check(!store.GetPetRoster(owner, id).CombatPets.Any(p => p.DefinitionIntegerId == hala.IntegerId),
                    "insufficient pet currency does not grant pet");
                store.SaveCombatPetProgress(owner, id, 186, 4, 27);
                store.UpdatePet(owner, id, 98);
                store.UpdateCombatPet(owner, id, 186);
                store.UpdatePet(owner, id, 162); // PetSnake is catalogued but not owned.
                store.UpdateCombatPet(owner, id, 188); // CombatPetHala is catalogued but not owned.
                var roster = store.GetPetRoster(owner, id);
                Check(roster.PetDefinitionIntegerIds.SequenceEqual(new[] { 98 })
                    && roster.CurrentPetDefinitionIntegerId == 98, "petkeeper stores owned and selected companion");
                Check(roster.CombatPets.Count == 1 && roster.CombatPets[0].DefinitionIntegerId == 186
                    && roster.CurrentCombatPetDefinitionIntegerId == 186
                    && roster.CombatPets[0].Level == 4 && roster.CombatPets[0].Experience == 27,
                    "combat petkeeper stores selected pet and progress");
            }
            using (var store = new GameStore(dir))
            {
                var roster = store.GetPetRoster(owner, id);
                Check(roster.CurrentPetDefinitionIntegerId == 98 && roster.CurrentCombatPetDefinitionIntegerId == 186
                    && roster.CombatPets.Single().Level == 4 && roster.CombatPets.Single().Experience == 27,
                    "pet selection and combat pet progress survive restart");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
