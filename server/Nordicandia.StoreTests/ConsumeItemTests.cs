using Game;
using Nordicandia.Server.State;
using SharedNet.Api;
using SharedNet.Constants.Game;

static class ConsumeItemTests
{
    public static void Run()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nord-consume-" + Guid.NewGuid());
        Directory.CreateDirectory(dir);
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        SerializedItem Stack(int count, int max = 1000) => new()
        {
            Id = Guid.NewGuid(), Name = "Iron", DefinitionIntegerId = 63,
            Attributes = new SerializedAttributes
            {
                Values = new()
                {
                    [AttributeOrigin.Item] = new Dictionary<int, GameAttributeValue>
                    {
                        [18] = new GameAttributeValue { Value = max, ValueD = max },
                        [19] = new GameAttributeValue { Value = count, ValueD = count },
                    },
                },
                MultiplicativeValues = new(),
            },
        };
        try
        {
            Guid owner, id;
            using (var store = new GameStore(dir))
            {
                owner = store.GetOrCreateUser("device:consume-test").UserId;
                id = store.CreateCharacter(owner, new CreateCharacterRequest
                {
                    DisplayName = "consume", CharacterGameMode = GameMode.Normal,
                    Data = new SerializedCharacterData { Data = Defaults.Create<SerializedCharacterData.SerializedData>() },
                }).CharacterId;
                var stack = Stack(6);
                var noStack = new SerializedItem { Id = Guid.NewGuid(), Name = "NiflheimPortal", DefinitionIntegerId = 159 };
                store.ApplyItemOperations(owner, id, new List<ItemOperationEntry>
                {
                    new AddItemOperationEntry { Item = stack },
                    new AddItemOperationEntry { Item = noStack },
                });

                var (c1, a1) = store.ConsumeItem(owner, id, stack.Id, 2);
                Check(c1 == 2 && !a1, "partial consume returns amount, not exhausted");
                var (c2, a2) = store.ConsumeItem(owner, id, stack.Id, 100);
                Check(c2 == 4 && a2, "over-consume clamps to remaining and exhausts");
                Check(store.GetItems(owner, id).All(i => i.Id != stack.Id), "exhausted stack is removed");

                // A non-stackable item (no stack attribute) is consumed whole.
                var (c3, a3) = store.ConsumeItem(owner, id, noStack.Id, 1);
                Check(c3 == 1 && a3 && store.GetItems(owner, id).All(i => i.Id != noStack.Id), "non-stackable consumed whole");
                Check(store.ConsumeItem(owner, id, Guid.NewGuid(), 1) == (0, false), "unknown item is a no-op");
            }
            using (var store = new GameStore(dir))
            {
                Check(store.GetItems(owner, id).Count == 0, "consumption persists across restart");
            }
        }
        finally { Directory.Delete(dir, true); }
    }
}
