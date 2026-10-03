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
var fidelityResults = FidelityReplayTests.EvaluateAll();
var fidelityFailures = fidelityResults.Where(r => !r.Ok).ToList();
foreach (var r in fidelityResults)
    Console.WriteLine((r.Ok ? "PASS " : "FAIL ") + $"fidelity[{r.Id}]");
Console.WriteLine($"fidelity replay: {fidelityResults.Count - fidelityFailures.Count}/{fidelityResults.Count} pass");
FidelityReport.Generate(fidelityResults);
if (fidelityFailures.Count > 0)
    throw new Exception("fidelity replay failures: " + string.Join(", ", fidelityFailures.Select(f => f.Error is null ? f.Id : $"{f.Id}: {f.Error}")));
LuckAndOnHitRecoveryTests.SkillMechanics();
LuckAndOnHitRecoveryTests.DodgeBlock();

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

int suiteFailures = 0;
void RunSuite(string name, Action action)
{
    try { action(); }
    catch (Exception ex) { suiteFailures++; Console.WriteLine($"FAILSUITE {name}: {ex.Message}"); }
}
RunSuite("SlotPersistence", SlotPersistenceTests.Run);
RunSuite("BlessingPersistence", BlessingPersistenceTests.Run);
RunSuite("M3", M3Tests.Run);
RunSuite("ConsumeItem", ConsumeItemTests.Run);
RunSuite("WebM0", WebM0Tests.Run);
RunSuite("CombatInstance", CombatInstanceTests.Run);
RunSuite("LootEquipment", LootEquipmentTests.Run);
RunSuite("AffixGeneration", AffixGenerationTests.Run);
RunSuite("E01AffixCatalog", E01AffixCatalogTests.Run);
RunSuite("PowerParameter", PowerParameterTests.Run);
RunSuite("PowerPool", PowerPoolTests.Run);
RunSuite("ManaMastery", ManaMasteryTests.Run);
RunSuite("AcceptanceRegression", AcceptanceRegressionTests.Run);
RunSuite("AttributeFormula", AttributeFormulaTests.Run);
RunSuite("CharacterAttributeChain", CharacterAttributeChainTests.Run);
RunSuite("SkillSpec", SkillSpecTests.Run);
RunSuite("C05Skill", C05SkillTests.Run);
RunSuite("C06Skill", C06SkillTests.Run);
RunSuite("C06Batch2", C06Batch2Tests.Run);
RunSuite("C06Batch3", C06Batch3Tests.Run);
RunSuite("C07Mastery", C07MasteryTests.Run);
if (suiteFailures > 0) throw new Exception($"{suiteFailures} suite(s) failed");
