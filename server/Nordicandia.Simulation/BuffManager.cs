namespace Nordicandia.Simulation;

/// <summary>Element used when resolving a damage-over-time buff against mitigation.</summary>
public enum DamageOverTimeType
{
    Physical,
    Fire,
    Cold,
    Lightning,
    Poison,
}

/// <summary>
/// C02: web port of the client's Buff lifecycle (1.9.3).
///
/// Recovered rules (disassembly):
/// - The web container keys instances by (definition, source) for its local model;
///   the original DistinctComparer call chain is not yet sufficient evidence that this
///   exactly matches the client's cross-source deduplication semantics.
/// - <b>Stack(Buff)</b> (0x2b22f88): a stackable re-application increments the stack
///   counter; the duration is replaced <b>only if the new remaining duration is longer</b>
///   (<c>fcmp</c> + conditional replace), gated by <c>CanRefreshDurationOnStack</c>.
/// - Base <b>IsStrongerThan(Buff)</b> (0x2b21c10): compare Guid, then remaining duration,
///   then stack count. <c>DebuffPoisoned</c> overrides this at 0x02B2F7A0 and first
///   compares <c>Tick_Damage_Per_Second / TimeoutDuration</c>, falling back to the base
///   comparison on a tie or zero duration.
/// - <b>Tick</b>: <c>DebuffPoisoned.DoWork</c> ticks once per second;
///   <c>Tick_Damage_Per_Second * dt</c> is dealt as damage and cannot crit/fork/chain.
///   The elapsed time is clamped to the remaining duration so a critical tick
///   (dt &gt; remaining) neither double-counts nor drops the partial tick.
/// - <b>Dispel / death removal</b>: buffs are removed on dispel and on death/world
///   change unless <c>IsPersistent</c>.
///
/// Poison note: poison re-applications use the subclass rate comparison rather than
/// the base Buff rule; a longer remaining duration alone does not make a weaker poison
/// replace a stronger one. Cross-source identity/deduplication still awaits the deferred
/// S-BUFF-1 field sample.
/// </summary>
public sealed class BuffInstance
{
    /// <summary>Web container key: <c>DefinitionId|Source</c> (original cross-source identity is unverified).</summary>
    public string Key => DefinitionId + "|" + Source;

    public string DefinitionId { get; init; } = "";
    /// <summary>来源: who applied the buff (player, skill name, monster name...).</summary>
    public string Source { get; init; } = "";
    /// <summary>Total duration in seconds.</summary>
    public double Duration { get; init; }
    /// <summary>Remaining duration in seconds.</summary>
    public double Remaining { get; set; }
    /// <summary>Stack count (for stackable buffs).</summary>
    public int Stacks { get; set; } = 1;
    public bool Stackable { get; init; }
    public bool AllowMultipleInstances { get; init; }
    /// <summary>Whether re-application may extend the duration (default true).</summary>
    public bool CanRefreshDurationOnStack { get; init; } = true;
    /// <summary>Survives death/world change when true.</summary>
    public bool IsPersistent { get; init; }
    /// <summary>DoT damage per second (poison, burning...).</summary>
    public double TickDps { get; set; }
    /// <summary>Damage element used to select armor/resistance mitigation for this DoT.</summary>
    public DamageOverTimeType TickDamageType { get; init; } = DamageOverTimeType.Poison;
    /// <summary>Buff magnitude (e.g. offense bonus fraction).</summary>
    public double Magnitude { get; set; }

    /// <summary>
    /// Port of <c>Buff.IsStrongerThan</c> (0x2b21c10): same key is assumed (the Guid
    /// comparison happens in <see cref="BuffManager"/>); compares remaining duration,
    /// then stack count. Standard CompareTo semantics: positive when this is stronger.
    /// </summary>
    public int CompareStrength(BuffInstance other)
    {
        // Client DebuffPoisoned.IsStrongerThan (0x02B2F7A0) first compares the
        // poison rate (Tick_Damage_Per_Second / TimeoutDuration) when both durations
        // are non-zero; ties fall through to Buff.IsStrongerThan's remaining-time,
        // then stack-count comparison.
        if (DefinitionId == "poison" && other.DefinitionId == "poison" && Duration > 0 && other.Duration > 0)
        {
            var byPoisonRate = (TickDps / Duration).CompareTo(other.TickDps / other.Duration);
            if (byPoisonRate != 0) return byPoisonRate;
        }
        var byDuration = Remaining.CompareTo(other.Remaining);
        if (byDuration != 0) return byDuration;
        return Stacks.CompareTo(other.Stacks);
    }
}

/// <summary>Per-entity buff container. Owns timeouts, stacking, dispel and DoT ticks.</summary>
public sealed class BuffManager
{
    private readonly Dictionary<string, BuffInstance> buffs = new();

    /// <summary>
    /// Applies <paramref name="incoming"/> following the recovered rules:
    /// <list type="bullet">
    /// <item>no existing instance with the key → add;</item>
    /// <item><c>AllowMultipleInstances</c> → always add under a suffixed key;</item>
    /// <item>stackable → increment stacks; extend duration only when the new remaining
    /// duration is longer;</item>
    /// <item>otherwise → replace only when the incoming instance is stronger.</item>
    /// </list>
    /// Returns true when the buff is active afterwards.
    /// </summary>
    public bool Add(BuffInstance incoming)
    {
        if (incoming.AllowMultipleInstances)
        {
            var key = incoming.Key + "#" + Guid.NewGuid().ToString("N")[..8];
            buffs[key] = incoming;
            return true;
        }
        if (!buffs.TryGetValue(incoming.Key, out var existing))
        {
            buffs[incoming.Key] = incoming;
            return true;
        }
        if (incoming.Stackable && existing.Stackable)
        {
            existing.Stacks++;
            if (incoming.CanRefreshDurationOnStack && incoming.Remaining > existing.Remaining)
                existing.Remaining = incoming.Remaining;
            if (incoming.TickDps > existing.TickDps) existing.TickDps = incoming.TickDps;
            return true;
        }
        if (incoming.CompareStrength(existing) > 0)
        {
            buffs[incoming.Key] = incoming;
            return true;
        }
        return false;
    }

    public bool Has(string definitionId, string source = "")
        => buffs.ContainsKey(definitionId + "|" + source);

    public BuffInstance? Get(string definitionId, string source = "")
        => buffs.TryGetValue(definitionId + "|" + source, out var b) ? b : null;

    /// <summary>Magnitude of the buff, or 0 when absent/expired.</summary>
    public double MagnitudeOf(string definitionId, string source = "")
        => Get(definitionId, source) is { Remaining: > 0 } b ? b.Magnitude : 0;

    /// <summary>Remaining duration, or 0 when absent.</summary>
    public double RemainingOf(string definitionId, string source = "")
        => Get(definitionId, source)?.Remaining ?? 0;

    /// <summary>Removes one buff by key. Returns true when something was removed.</summary>
    public bool Remove(string definitionId, string source = "")
        => buffs.Remove(definitionId + "|" + source);

    /// <summary>驱散: removes every buff matching <paramref name="predicate"/>.</summary>
    public int Dispel(Func<BuffInstance, bool> predicate)
    {
        var keys = buffs.Where(kv => predicate(kv.Value)).Select(kv => kv.Key).ToList();
        foreach (var key in keys) buffs.Remove(key);
        return keys.Count;
    }

    /// <summary>
    /// Advances all buffs by <paramref name="dt"/>. For DoT buffs, <paramref name="onTick"/>
    /// receives <c>(buff, elapsed)</c> with elapsed clamped to the remaining duration, so a
    /// critical tick (dt &gt; remaining) applies exactly the partial tick — never a
    /// double-count, never a dropped tick. Expired buffs are removed.
    /// </summary>
    public void Tick(double dt, Action<BuffInstance, double>? onTick = null)
    {
        if (dt <= 0) return;
        // Snapshot: onTick may kill the owner and Clear() the container mid-tick.
        var expired = new List<string>();
        foreach (var (key, buff) in buffs.ToList())
        {
            var elapsed = Math.Min(dt, buff.Remaining);
            if (buff.TickDps > 0 && elapsed > 0) onTick?.Invoke(buff, elapsed);
            buff.Remaining = Math.Max(0, buff.Remaining - dt);
            if (buff.Remaining <= 0) expired.Add(key);
        }
        foreach (var key in expired) buffs.Remove(key);
    }

    /// <summary>Removes buffs on death / world change. Persistent buffs survive.</summary>
    public int Clear(bool includePersistent = false)
    {
        if (includePersistent)
        {
            var count = buffs.Count;
            buffs.Clear();
            return count;
        }
        var keys = buffs.Where(kv => !kv.Value.IsPersistent).Select(kv => kv.Key).ToList();
        foreach (var key in keys) buffs.Remove(key);
        return keys.Count;
    }

    public int Count => buffs.Count;
    public IReadOnlyCollection<BuffInstance> All => buffs.Values;
}
