using Game;
using Grpc.Core;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants.Game;
using System.Text.Json;

static class SlotPersistenceTests
{
    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-slots-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        void Reject(Action action) { try { action(); } catch (RpcException) { return; } throw new Exception("Expected rejection"); }
        int Attr(EnterGameWithCharacterResponse r, int id) => (int)r.Character.Attributes.Values[AttributeOrigin.Character][id].ValueD;
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir))
            {
                owner = store.GetOrCreateUser("device:slot-test").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest {
                    DisplayName = "slots", CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;
                store.SaveRealtimeProgress(owner, id, 0, 0, 1000);
                var r = store.Enter(owner, id);
                Check(Attr(r,45)==4 && Attr(r,56)==1 && Attr(r,49)==1 && Attr(r,44)==2, "fresh client slot defaults");
                foreach (var type in new[] { ExpandCharacterSkillSlotTypes.Active, ExpandCharacterSkillSlotTypes.Passive, ExpandCharacterSkillSlotTypes.PassiveTraining })
                    store.ExpandSkillSlots(owner, id, type, 10);
                store.ExpandInventoryRow(owner, id, ExpandInventoryRowType.Potions, 10);
                var balance = store.GetRealtimeProgress(owner,id).Opals;
                Reject(()=>store.ExpandInventoryRow(owner,id,ExpandInventoryRowType.Potions,10));
                Reject(()=>store.ExpandSkillSlots(owner,id,(ExpandCharacterSkillSlotTypes)999,10));
                Reject(()=>store.ExpandInventoryRow(owner,id,(ExpandInventoryRowType)999,10));
                Reject(()=>store.ExpandSkillSlots(Guid.NewGuid(),id,ExpandCharacterSkillSlotTypes.Active,10));
                Check(store.GetRealtimeProgress(owner,id).Opals==balance, "rejected purchases do not charge");
            }
            using (var store = new GameStore(dir))
            {
                var r=store.Enter(owner,id);
                Check(Attr(r,45)==5 && Attr(r,56)==2 && Attr(r,49)==2 && Attr(r,44)==3, "all slot types survive process restart");
                Check(r.GameModeAccountData.NumActiveSkillSlots==5 && r.GameModeAccountData.NumPassiveSkillSlots==2
                    && r.GameModeAccountData.NumPassiveTrainingSlots==2 && r.GameModeAccountData.NumPotionSlots==3, "account and character capacities agree");
                store.ExpandSkillSlots(owner,id,ExpandCharacterSkillSlotTypes.Active,10);
                Reject(()=>store.ExpandSkillSlots(owner,id,ExpandCharacterSkillSlotTypes.Active,10));
            }
            // Reconstruct an actual pre-fix save: legacy counters and unchanged base attributes.
            var path=Path.Combine(dir,"world.json");
            var state=JsonSerializer.Deserialize<GameStore.State>(File.ReadAllText(path))!;
            var c=state.Characters[id]; c.SlotCapacitiesMigrated=false;
            var a=GameStore.Unpack<SerializedPlayerGameModeAccountData>(c.GameModeAccount);
            a.NumActiveSkillSlots=4; a.NumPassiveSkillSlots=4; a.NumPassiveTrainingSlots=1; a.NumPotionSlots=3;
            c.GameModeAccount=GameStore.Pack(a);
            var d=GameStore.Unpack<SerializedCharacterData.SerializedData>(c.Data);
            foreach (var (key,value) in new[]{(45,4),(56,1),(49,1),(44,2)})
                d.Attributes.Values[AttributeOrigin.Character][key]=new GameAttributeValue{ValueD=value};
            c.Data=GameStore.Pack(d); File.WriteAllText(path,JsonSerializer.Serialize(state));
            for (int i=0;i<2;i++) using (var store=new GameStore(dir))
            {
                var r=store.Enter(owner,id);
                Check(Attr(r,45)==5 && Attr(r,56)==2 && Attr(r,49)==2 && Attr(r,44)==3, "legacy purchases migrate exactly once, restart " + i);
            }
        }
        finally { Directory.Delete(dir,true); }
    }
}
