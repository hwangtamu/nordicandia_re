using MagicOnion;
using MessagePack;

namespace SharedNet.Api;

// Hand-written extension of the generated ICharacterServiceApi (GeneratedContracts.cs).
// The official client has no RPC that uploads skill rank upgrades: LivingPowers.RankUp
// mutates the rank purely in memory, so the server kept Power_Rank = 1 forever and the
// next login overwrote the client's upgraded rank. A patched client calls
// UpgradeCharacterSkillRank right after RankUp so the rank persists server-side.
public partial interface ICharacterServiceApi
{
    MagicOnion.UnaryResult<SharedNet.Api.UpgradeCharacterSkillRankResponse> UpgradeCharacterSkillRank(SharedNet.Api.UpgradeCharacterSkillRankRequest req);
}

[MessagePackObject(true)]
public class UpgradeCharacterSkillRankRequest
{
    public System.Guid CharacterId { get; set; }
    // Matches the powerHashSafe argument of the client's LivingPowers.RankUp.
    public int PowerHashSafe { get; set; }
    // The rank the client just upgraded to (client-authoritative gameplay math,
    // same trust model as drops/buffs on this server).
    public double NewRank { get; set; }
}

[MessagePackObject(true)]
public class UpgradeCharacterSkillRankResponse
{
    // Server-confirmed rank after persisting.
    public double NewRank { get; set; }
}
