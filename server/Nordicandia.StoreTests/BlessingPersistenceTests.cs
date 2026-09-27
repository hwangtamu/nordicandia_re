using Game;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class BlessingPersistenceTests
{
    sealed class TestClock : TimeProvider
    {
        public DateTimeOffset Current = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Current;
    }

    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-blessings-" + Guid.NewGuid());
        var clock = new TestClock();
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        double Seconds(SerializedCharacterData.SerializedBuff buff)
        {
            // Exercise the actual MessagePack formatter, including fractional seconds.
            var decoded = GameStore.Unpack<SerializedCharacterData.SerializedBuff>(GameStore.Pack(buff));
            Check(!decoded.IsCharacterContext && decoded.Attributes.Values.Count == 1, "blessing uses native buff context");
            var values = decoded.Attributes.Values[AttributeOrigin.Buff];
            Check(values.Count == 3 && values[279].ValueD == 1 && values[289].ValueD == 6
                && !values.ContainsKey(465), "native blessing attributes, no scripted total override");
            return values[166].ValueD;
        }
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir, clock))
            {
                owner = store.GetOrCreateUser("device:blessings-test").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest {
                    DisplayName = "blessings", CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;
                store.SaveRealtimeProgress(owner, id, 0, 0, 1000);
                for (var type = 1; type <= 4; type++) store.MakeOffering(owner, id, type, type, 10);
                var buffs = store.ActiveBlessingBuffs(owner, id);
                Check(buffs.Select(b=>b.DefinitionIntegerId).SequenceEqual(new[]{276,278,280,282}), "four blessing definitions");
                Check(buffs.Select(Seconds).SequenceEqual(new double[]{600,1800,3600,14400}), "offering durations 10m/30m/1h/4h");
            }
            clock.Current = clock.Current.AddSeconds(30.25);
            using (var store = new GameStore(dir, clock))
            {
                var blessings = store.GetActiveBlessings(owner,id);
                Check(Seconds(blessings.Odin)==569.75, "remaining duration survives restart with fractional precision");
                for (var i=0;i<2;i++)
                {
                    var entered = store.Enter(owner,id);
                    var buffs=entered.Character.Buffs.Buffs.Where(b=>b.DefinitionIntegerId is 276 or 278 or 280 or 282).ToList();
                    Check(buffs.Count==4 && Seconds(buffs[0])==569.75, "relogin neither duplicates nor extends blessings");
                }
            }
            clock.Current=clock.Current.AddSeconds(570);
            using (var store=new GameStore(dir,clock))
            {
                Check(store.GetActiveBlessings(owner,id).Odin==null, "expired blessing is omitted from RPC");
                Check(store.Enter(owner,id).Character.Buffs.Buffs.All(b=>b.DefinitionIntegerId!=276), "expired blessing removed from login data");
                Check(store.ActiveBlessingBuffs(owner,id).Count==3, "only unexpired blessings are pushed");
            }
        }
        finally { Directory.Delete(dir,true); }
    }
}
