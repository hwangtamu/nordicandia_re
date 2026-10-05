namespace Nordicandia.Simulation;

/// <summary>Server-owned checkpoint for a consumed Niflheim portal. The whole checkpoint is
/// saved with realtime progress, so a restart resumes the current pack rather than granting
/// another portal or resetting the run to a normal dungeon.</summary>
public sealed record NiflheimRunState
{
    public int TotalPacks { get; init; }
    public int PacksCleared { get; init; }
    public int FinalPackExtraMonsters { get; init; }
    public bool NativeTail { get; init; }
    public bool ExitReady { get; init; }
    public int ChestSize { get; init; }
    public bool ChestOpened { get; init; }
    public double ExitX { get; init; }
    public double ExitZ { get; init; }
    public double ChestX { get; init; }
    public double ChestZ { get; init; }
    public int PackIndex { get; init; }
    public double PackSizeRemainder { get; init; }
    public int PendingPackSize { get; init; }
    public double PackSpawnTimer { get; init; }
    public double PackOriginX { get; init; }
    public double PackOriginZ { get; init; }
    public bool PendingPackSpawn { get; init; }
    public ulong RandomState { get; init; }
    public double PlayerX { get; init; }
    public double PlayerZ { get; init; }
    public double PlayerHp { get; init; }
    public double PlayerMana { get; init; }
    public double PlayerShield { get; init; }
    public double PlayerRespawnTimer { get; init; }
    public double AttackCooldown { get; init; }
    public double[] SkillCooldowns { get; init; } = Array.Empty<double>();
    public List<BuffInstance> PlayerBuffs { get; init; } = new();
    public List<CombatMonster> Monsters { get; init; } = new();
    public List<List<BuffInstance>> MonsterBuffs { get; init; } = new();
}
