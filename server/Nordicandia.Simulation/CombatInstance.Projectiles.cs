namespace Nordicandia.Simulation;

/// <summary>
/// C03: geometric projectile simulation. Replaces the old "nearest enemies within
/// 10 yards" stand-in for fork/chain (D08) with real flight: projectiles move in
/// small steps, collide by circle test, carry a hit-set (已命中集合), and resolve
/// fork/chain/pierce per the recovered <c>HandleForkAndChain</c> rules
/// (docs/web/FORK_CHAIN_RECOVERY_2026-10-03.md):
/// <list type="bullet">
/// <item>fork: on hit, roll; success spawns 2 new projectiles at the hit point with
/// 0.5x damage, canFork=canChain=false, the original target pre-added to their
/// hit-sets, scattered at ±(45°–90°) — the parent is consumed;</item>
/// <item>chain: if fork misses, roll; success retargets the SAME projectile toward
/// the nearest other enemy within 10 yards, full damage;</item>
/// <item>pierce: per-hit roll; success keeps the projectile flying straight.</item>
/// </list>
/// The flight is simulated to completion synchronously inside the firing call, so the
/// synchronous UseSkill contract (damage known at cast time) is preserved; the path is
/// geometrically exact. "projectile" combat events (spawn/hit/fork/chain/pierce) carry
/// positions for future rendering.
/// <para/>
/// Provisional numbers (client: speed = base attribute × (1+bonus) × product,
/// <c>GetProjectileSpeed</c> 0x2c38a4c; exact attribute ids not yet mapped):
/// speed 20 y/s, collider radius 0.5 y. Line of sight: the arena has no obstacles,
/// so LoS is trivially true.
/// </summary>
public sealed class Projectile
{
    public double X, Z;
    public double DirX, DirZ = 1;
    /// <summary>Yards per second (Provisional).</summary>
    public double Speed = 20.0;
    /// <summary>Seconds of flight left.</summary>
    public double Remaining;
    /// <summary>Collider radius in yards (Provisional).</summary>
    public double Radius = 0.5;
    /// <summary>Damage multiplier (0.5 for fork children).</summary>
    public double DamageMult = 1.0;
    public bool CanFork = true;
    public bool CanChain = true;
    /// <summary>已命中集合: monster indices this projectile must not hit again.</summary>
    public readonly HashSet<int> HitActors = new();
    public string Source = "";
}

public partial class CombatInstance
{
    /// <summary>Maximum projectile flight distance in yards (Provisional).</summary>
    public const double ProjectileRange = 14.0;

    /// <summary>
    /// Fires <paramref name="projectile"/> from the player's position toward
    /// (<paramref name="aimX"/>, <paramref name="aimZ"/>), simulating every projectile
    /// (including fork children) to completion. <paramref name="resolveHit"/> deals
    /// damage for one contact: (monster, damageMultiplier) -> damage dealt.
    /// Returns total damage dealt.
    /// </summary>
    private double FireProjectiles(Projectile projectile, double aimX, double aimZ,
        Func<CombatMonster, double, double> resolveHit,
        Func<double> forkChance, Func<double> chainChance, Func<double> pierceChance)
    {
        var dx = aimX - projectile.X;
        var dz = aimZ - projectile.Z;
        var len = Math.Sqrt(dx * dx + dz * dz);
        if (len > 1e-9) { projectile.DirX = dx / len; projectile.DirZ = dz / len; }
        projectile.Remaining = ProjectileRange / projectile.Speed;
        EmitEvent("projectile", -1, 0, $"spawn {projectile.Source} {projectile.X:F1},{projectile.Z:F1}");

        var total = 0.0;
        var queue = new Queue<Projectile>();
        queue.Enqueue(projectile);
        while (queue.Count > 0)
            total += FlyProjectile(queue.Dequeue(), queue, resolveHit, forkChance, chainChance, pierceChance);
        return total;
    }

    private double FlyProjectile(Projectile p, Queue<Projectile> queue,
        Func<CombatMonster, double, double> resolveHit,
        Func<double> forkChance, Func<double> chainChance, Func<double> pierceChance)
    {
        var total = 0.0;
        // Step smaller than the collider diameter: no tunneling through monsters.
        const double stepLen = 0.25;
        while (p.Remaining > 0)
        {
            var step = Math.Min(stepLen, p.Speed * p.Remaining);
            p.X += p.DirX * step;
            p.Z += p.DirZ * step;
            p.Remaining -= step / p.Speed;
            var target = FirstProjectileCollision(p);
            if (target is null) continue;

            p.HitActors.Add(target.Index);
            var dealt = resolveHit(target, p.DamageMult);
            total += dealt;
            EmitEvent("projectile", target.Index, dealt, $"hit {p.Source} {p.X:F1},{p.Z:F1}");

            // Fork first (client: fork takes precedence over chain).
            if (p.CanFork && CombatModel.RollChance(forkChance(), rng))
            {
                foreach (var child in SpawnForkChildren(p, target))
                    queue.Enqueue(child);
                EmitEvent("projectile", target.Index, 0, $"fork {p.Source} {p.X:F1},{p.Z:F1}");
                break; // parent consumed (HandleForkAndChain returns true)
            }
            // Chain: same projectile retargets to the nearest other enemy within 10y.
            // Client clears the chance on success (ClearValues): at most one chain hop.
            if (p.CanChain && CombatModel.RollChance(chainChance(), rng))
            {
                var next = NearestProjectileTarget(p.X, p.Z, 10.0, p.HitActors);
                if (next is not null)
                {
                    p.CanChain = false;
                    var rdx = next.X - p.X;
                    var rdz = next.Z - p.Z;
                    var rlen = Math.Sqrt(rdx * rdx + rdz * rdz);
                    if (rlen > 1e-9) { p.DirX = rdx / rlen; p.DirZ = rdz / rlen; }
                    EmitEvent("projectile", next.Index, 0, $"chain {p.Source} {p.X:F1},{p.Z:F1}");
                    continue;
                }
            }
            // Pierce: keep flying straight on a per-hit roll.
            if (CombatModel.RollChance(pierceChance(), rng))
            {
                EmitEvent("projectile", target.Index, 0, $"pierce {p.Source} {p.X:F1},{p.Z:F1}");
                continue;
            }
            break; // projectile dies on the target
        }
        return total;
    }

    /// <summary>Nearest alive monster within <paramref name="p"/>'s collider, excluding
    /// the hit-set. Small steps make the first contact found the first along the path.</summary>
    private CombatMonster FirstProjectileCollision(Projectile p)
    {
        CombatMonster best = null;
        var bestDist = double.MaxValue;
        foreach (var m in monsters)
        {
            if (!m.Alive || p.HitActors.Contains(m.Index)) continue;
            var d = Math.Sqrt(Math.Pow(m.X - p.X, 2) + Math.Pow(m.Z - p.Z, 2));
            if (d <= p.Radius && d < bestDist) { bestDist = d; best = m; }
        }
        if (boss is { Alive: true } && !p.HitActors.Contains(boss.Index))
        {
            var d = Math.Sqrt(Math.Pow(boss.X - p.X, 2) + Math.Pow(boss.Z - p.Z, 2));
            if (d <= p.Radius && d < bestDist) best = boss;
        }
        return best;
    }

    private CombatMonster NearestProjectileTarget(double x, double z, double radius, HashSet<int> exclude)
    {
        CombatMonster best = null;
        var bestDist = radius;
        foreach (var m in monsters)
        {
            if (!m.Alive || exclude.Contains(m.Index)) continue;
            var d = Math.Sqrt(Math.Pow(m.X - x, 2) + Math.Pow(m.Z - z, 2));
            if (d <= bestDist) { bestDist = d; best = m; }
        }
        if (boss is { Alive: true } && !exclude.Contains(boss.Index))
        {
            var d = Math.Sqrt(Math.Pow(boss.X - x, 2) + Math.Pow(boss.Z - z, 2));
            if (d <= bestDist) best = boss;
        }
        return best;
    }

    /// <summary>
    /// Fork children per the recovery: 2 new projectiles at the hit point, 0.5x damage,
    /// no further fork/chain, original target pre-added to the hit-set, scattered at
    /// ±(45°–90°) random. The single-vs-total spread-angle convention is unconfirmed
    /// (GenerateFixedSpacingSpreadPositions not expanded); symmetric ±θ chosen.
    /// </summary>
    private IEnumerable<Projectile> SpawnForkChildren(Projectile parent, CombatMonster hit)
    {
        var theta = (45.0 + rng.NextDouble() * 45.0) * Math.PI / 180.0;
        foreach (var sign in new[] { 1.0, -1.0 })
        {
            var cos = Math.Cos(sign * theta);
            var sin = Math.Sin(sign * theta);
            var child = new Projectile
            {
                X = parent.X,
                Z = parent.Z,
                DirX = parent.DirX * cos - parent.DirZ * sin,
                DirZ = parent.DirX * sin + parent.DirZ * cos,
                Speed = parent.Speed,
                Remaining = ProjectileRange / parent.Speed,
                Radius = parent.Radius,
                DamageMult = parent.DamageMult * 0.5,
                CanFork = false,
                CanChain = false,
                Source = parent.Source,
            };
            child.HitActors.UnionWith(parent.HitActors);
            yield return child;
        }
    }
}
