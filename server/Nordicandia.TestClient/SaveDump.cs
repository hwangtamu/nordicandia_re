using MessagePack;
using Game;

namespace Nordicandia.TestClient;

/// <summary>Dumps a decrypted character save (see decrypt_nordicandia.py) so the server's
/// attribute-id assumptions can be checked against the real client payload.</summary>
public static class SaveDump
{
    public static int Run(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var lz4 = MessagePackSerializer.DefaultOptions.WithCompression(MessagePackCompression.Lz4BlockArray);
        foreach (var options in new[] { MessagePackSerializer.DefaultOptions, lz4 })
        {
            try
            {
                var character = MessagePackSerializer.Deserialize<SerializedCharacterData>(bytes, options);
                if (character?.Data?.Attributes?.Values is { Count: > 0 })
                {
                    Console.WriteLine($"Character name={character.Header?.Name} id={character.Header?.CharacterId} level={character.Header?.Level}");
                    DumpAttributes(character.Data.Attributes);
                    return 0;
                }
                var data = MessagePackSerializer.Deserialize<SerializedCharacterData.SerializedData>(bytes, options);
                if (data?.Attributes?.Values is { Count: > 0 })
                {
                    Console.WriteLine($"SerializedData account={data.AccountId} character={data.CharacterId}");
                    DumpAttributes(data.Attributes);
                    if (data.Waypoints?.WaypointMap != null)
                        foreach (var (tier, wp) in data.Waypoints.WaypointMap)
                            Console.WriteLine($"Waypoint tier={tier} current={wp.CurrentWaypoint} max={wp.MaxWaypoint} cleared={wp.HighestWaypointCleared}");
                    if (data.Items?.Items != null)
                        foreach (var it in data.Items.Items.Take(50))
                            Console.WriteLine($"Item {it?.Slot,-12} loc=({it?.Location?.Page},{it?.Location?.Row},{it?.Location?.Column}) def={it?.DefinitionIntegerId} name={it?.Name} id={it?.Id}");
                    Console.WriteLine($"Powers={(data.Powers != null)} Skills={(data.Skills != null)} Buffs={(data.Buffs != null)} Quests={(data.Quests != null)} Waypoints={(data.Waypoints != null)}");
                    foreach (var s in data.Skills?.Skills ?? new())
                        Console.WriteLine($"Skill slot={s.Slot} col={s.Column} row={s.Row} hash={s.PowerHash} safe={s.PowerHashSafe}");
                    foreach (var p in data.Powers?.Powers ?? new())
                        Console.WriteLine($"Power hash={p.PowerHash} safe={p.PowerHashSafe} rank={p.Power_Rank} cd={p.Power_Cooldown} cdStart={p.Power_Cooldown_Start} trainStart={p.Power_Training_Start} trainEnd={p.Power_Training_End}");
                    foreach (var b in data.Buffs?.Buffs ?? new())
                        Console.WriteLine($"Buff def={b.DefinitionIntegerId} charCtx={b.IsCharacterContext}");
                    return 0;
                }

                var header = MessagePackSerializer.Deserialize<SerializedCharacterData.SerializedHeader>(bytes, options);
                if (header?.CharacterId != Guid.Empty)
                {
                    Console.WriteLine($"Header name={header.Name} characterId={header.CharacterId} onlineCharacterId={header.OnlineCharacterId} account={header.CreatedWithAccount} level={header.Level}");
                    return 0;
                }
            }
            catch (Exception) { }
        }
        Console.WriteLine($"Failed to decode. First bytes: {Convert.ToHexString(bytes.AsSpan(0, Math.Min(48, bytes.Length)))}");
        return 1;
    }

    private static void DumpAttributes(SerializedAttributes attributes)
    {
        if (attributes?.Values == null) { Console.WriteLine("(no attributes)"); return; }
        foreach (var (origin, map) in attributes.Values)
        {
            Console.WriteLine($"Origin {origin}: {map?.Count ?? 0} attributes");
            foreach (var (id, value) in map ?? new())
                Console.WriteLine($"  id={id,-6} int={value.Value,-8} double={value.ValueD}");
        }
    }
}
