using System.Text.Json;
using Nordicandia.Server.State;

LuckAndOnHitRecoveryTests.RarityWeights();
LuckAndOnHitRecoveryTests.OnHit();
LuckAndOnHitRecoveryTests.EquippedWeaponGate();
LuckAndOnHitRecoveryTests.ConversionPenetration();
LuckAndOnHitRecoveryTests.ItemQuantityRemainder();
LuckAndOnHitRecoveryTests.RangedKiting();
LuckAndOnHitRecoveryTests.BrainSelection();
LuckAndOnHitRecoveryTests.BossPowers();
LuckAndOnHitRecoveryTests.Curses();

var directory = Path.Combine(Path.GetTempPath(), "nord-alias-test-" + Guid.NewGuid());
Directory.CreateDirectory(directory);
void Check(bool value, string name) { if (!value) throw new Exception(name); Console.WriteLine("PASS " + name); }
try
{
    Guid owner;
    string auth, refresh;
    using (var store = new GameStore(directory))
    {
        owner = store.GetOrCreateUser("steam:test-alias").UserId;
        var session = store.CreateSession(owner);
        auth = session.AuthToken; refresh = session.RefreshToken;
        Check(store.GetUserByAuthToken(auth)?.UserId == owner, "single identity auth");
    }
    var path = Path.Combine(directory, "world.json");
    var state = JsonSerializer.Deserialize<GameStore.State>(File.ReadAllText(path))!;
    state.Users["device:test-alias"] = state.Users["steam:test-alias"];
    File.WriteAllText(path, JsonSerializer.Serialize(state));
    using (var store = new GameStore(directory))
    {
        Check(store.GetUserByAuthToken(auth)?.UserId == owner, "linked identity auth");
        Check(store.RequireUser("Bearer " + auth) == owner, "linked identity RPC authorization");
        Check(store.FindUserByRefreshToken(refresh)?.UserId == owner, "linked identity refresh");
        Check(store.GetOrCreateUser("device:test-alias").UserId == owner, "device login resolves shared owner");
        Check(store.GetUserByAuthToken(null) == null && store.GetUserByAuthToken("invalid") == null, "invalid token rejected");
        var copy = store.GetUserByAuthToken(auth); copy.DisplayName = "changed";
        Check(store.GetUserByAuthToken(auth)?.DisplayName != "changed", "returned user is detached");
    }
    state = JsonSerializer.Deserialize<GameStore.State>(File.ReadAllText(path))!;
    state.Sessions[auth].ExpireTimestamp = DateTime.UtcNow.AddDays(-1);
    File.WriteAllText(path, JsonSerializer.Serialize(state));
    using (var store = new GameStore(directory)) Check(store.GetUserByAuthToken(auth) == null, "expired token rejected");
    state.Sessions[auth].ExpireTimestamp = DateTime.UtcNow.AddDays(1);
    state.Users.Clear();
    File.WriteAllText(path, JsonSerializer.Serialize(state));
    using (var store = new GameStore(directory)) Check(store.GetUserByAuthToken(auth) == null, "orphaned session rejected");
}
finally { Directory.Delete(directory, true); }

SlotPersistenceTests.Run();

BlessingPersistenceTests.Run();

M3Tests.Run();

ConsumeItemTests.Run();

WebM0Tests.Run();

CombatInstanceTests.Run();

LootEquipmentTests.Run();

AffixGenerationTests.Run();

PowerParameterTests.Run();

PowerPoolTests.Run();

ManaMasteryTests.Run();

AcceptanceRegressionTests.Run();

AttributeFormulaTests.Run();

CharacterAttributeChainTests.Run();
