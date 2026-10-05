namespace Nordicandia.Simulation;

/// <summary>
/// C03: projectiles are persistent combat entities. They advance on the simulation's
/// fixed 50ms clock, collide against actors at their current positions, and stop at
/// map walls. Fork/chain/pierce behavior follows the recovered HandleForkAndChain
/// precedence; the speed, radius, and range defaults remain Provisional.
/// </summary>
public sealed class Projectile
{
    public long Id;
    public double X, Z;
    public double DirX, DirZ = 1;
    /// <summary>Yards per second (Provisional until the attribute slots are mapped).</summary>
    public double Speed = 20.0;
    /// <summary>Seconds of flight left.</summary>
    public double Remaining;
    /// <summary>Collider radius in yards (Provisional).</summary>
    public double Radius = 0.5;
    /// <summary>Damage multiplier (0.5 for fork children).</summary>
    public double DamageMult = 1.0;
    public bool CanFork = true;
    public bool CanChain = true;
    /// <summary>Actors already hit by this projectile.</summary>
    public readonly HashSet<int> HitActors = new();
    public string Source = "";
}

public sealed partial class CombatInstance
{
    /// <summary>Maximum projectile flight distance in yards (Provisional).</summary>
    public const double ProjectileRange = 14.0;

    private sealed class ProjectileFlight
    {
        public Projectile Projectile { get; init; }
        public Func<CombatMonster, double, double> ResolveHit { get; init; }
        public Func<double> ForkChance { get; init; }
        public Func<double> ChainChance { get; init; }
        public Func<double> PierceChance { get; init; }
        public Action<CombatMonster>? AfterHit { get; init; }
    }

    private readonly List<ProjectileFlight> projectileFlights = new();
    private double projectileTimeRemainder;
    private long nextProjectileId;

    /// <summary>Currently flying projectiles, exposed as a snapshot for API/tests.</summary>
    public IReadOnlyList<Projectile> ActiveProjectiles
        => projectileFlights.Select(f => f.Projectile).ToArray();

    /// <summary>
    /// Launches a projectile toward the aim point. Damage is resolved only after a
    /// later fixed-step collision; this preserves flight time for moving targets and
    /// means casting alone cannot damage a distant actor.
    /// </summary>
    private double FireProjectiles(Projectile projectile, double aimX, double aimZ,
        Func<CombatMonster, double, double> resolveHit,
        Func<double> forkChance, Func<double> chainChance, Func<double> pierceChance,
        Action<CombatMonster>? afterHit = null)
    {
        var dx = aimX - projectile.X;
        var dz = aimZ - projectile.Z;
        var len = Math.Sqrt(dx * dx + dz * dz);
        if (len > 1e-9) { projectile.DirX = dx / len; projectile.DirZ = dz / len; }
        if (projectile.Speed <= 0) return 0;
        projectile.Remaining = ProjectileRange / projectile.Speed;
        if (projectile.Id == 0) projectile.Id = ++nextProjectileId;
        projectileFlights.Add(new ProjectileFlight
        {
            Projectile = projectile,
            ResolveHit = resolveHit,
            ForkChance = forkChance,
            ChainChance = chainChance,
            PierceChance = pierceChance,
            AfterHit = afterHit,
        });
        EmitEvent("projectile", -1, 0, $"spawn {projectile.Source} {projectile.X:F1},{projectile.Z:F1}");
        return 0;
    }

    /// <summary>Advances projectiles on an accumulated 50ms fixed clock.</summary>
    private void TickProjectiles(double dt)
    {
        projectileTimeRemainder += dt;
        while (projectileTimeRemainder + 1e-10 >= StepSeconds)
        {
            projectileTimeRemainder -= StepSeconds;
            foreach (var flight in projectileFlights.ToArray())
                AdvanceProjectile(flight, StepSeconds);
            projectileFlights.RemoveAll(f => f.Projectile.Remaining <= 1e-9);
        }
    }

    private void AdvanceProjectile(ProjectileFlight flight, double dt)
    {
        var p = flight.Projectile;
        if (p.Remaining <= 0) return;
        var travel = Math.Min(p.Speed * dt, p.Speed * p.Remaining);
        var fromX = p.X;
        var fromZ = p.Z;
        var toX = fromX + p.DirX * travel;
        var toZ = fromZ + p.DirZ * travel;

        var blocked = FirstWallFraction(fromX, fromZ, toX, toZ, out var wallFraction);
        var target = FirstProjectileCollision(p, fromX, fromZ, toX, toZ,
            out var hitX, out var hitZ, out var hitFraction);
        if (blocked && (target is null || wallFraction <= hitFraction))
        {
            p.X = fromX + (toX - fromX) * wallFraction;
            p.Z = fromZ + (toZ - fromZ) * wallFraction;
            EmitEvent("projectile", -1, 0, $"wall {p.Source} {p.X:F1},{p.Z:F1}");
            p.Remaining = 0;
            return;
        }
        p.X = target is null ? toX : hitX;
        p.Z = target is null ? toZ : hitZ;
        p.Remaining = Math.Max(0, p.Remaining - travel / p.Speed);
        if (target is null) return;

        p.HitActors.Add(target.Index);
        var dealt = flight.ResolveHit(target, p.DamageMult);
        flight.AfterHit?.Invoke(target);
        EmitEvent("projectile", target.Index, dealt, $"hit {p.Source} {p.X:F1},{p.Z:F1}");

        // Fork first (client: fork takes precedence over chain); fork consumes parent.
        if (p.CanFork && CombatModel.RollChance(flight.ForkChance(), rng))
        {
            foreach (var child in SpawnForkChildren(p))
            {
                child.Id = ++nextProjectileId;
                projectileFlights.Add(new ProjectileFlight
                {
                    Projectile = child,
                    ResolveHit = flight.ResolveHit,
                    ForkChance = flight.ForkChance,
                    ChainChance = flight.ChainChance,
                    PierceChance = flight.PierceChance,
                    AfterHit = flight.AfterHit,
                });
            }
            EmitEvent("projectile", target.Index, 0, $"fork {p.Source} {p.X:F1},{p.Z:F1}");
            p.Remaining = 0;
            return;
        }

        // Chain is one retarget of the same projectile, full damage.
        if (p.CanChain && CombatModel.RollChance(flight.ChainChance(), rng))
        {
            var next = NearestProjectileTarget(p.X, p.Z, 10.0, p.HitActors);
            if (next is not null)
            {
                p.CanChain = false;
                AimAt(p, next.X, next.Z);
                EmitEvent("projectile", next.Index, 0, $"chain {p.Source} {p.X:F1},{p.Z:F1}");
                return;
            }
        }

        if (!CombatModel.RollChance(flight.PierceChance(), rng))
            p.Remaining = 0;
        else
            EmitEvent("projectile", target.Index, 0, $"pierce {p.Source} {p.X:F1},{p.Z:F1}");
    }

    private bool FirstWallFraction(double fromX, double fromZ, double toX, double toZ, out double fraction)
    {
        fraction = 1;
        if (Layout is null) return false;
        var dx = toX - fromX;
        var dz = toZ - fromZ;
        // Sample well below a map cell width so a narrow wall cannot be skipped.
        var samples = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(dx * dx + dz * dz) / 0.1));
        for (var i = 1; i <= samples; i++)
        {
            var t = (double)i / samples;
            var cell = Layout.Cell(fromX + dx * t, fromZ + dz * t, ArenaHalf);
            if (Layout.IsFloor(cell.X, cell.Z)) continue;
            fraction = (double)(i - 1) / samples;
            return true;
        }
        return false;
    }

    /// <summary>Finds the earliest actor contact along this step's swept segment.</summary>
    private CombatMonster? FirstProjectileCollision(Projectile p,
        double fromX, double fromZ, double toX, double toZ,
        out double hitX, out double hitZ, out double hitFraction)
    {
        CombatMonster? best = null;
        var bestT = double.PositiveInfinity;
        var dx = toX - fromX;
        var dz = toZ - fromZ;
        var lenSq = dx * dx + dz * dz;
        hitX = toX;
        hitZ = toZ;
        hitFraction = double.PositiveInfinity;

        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive || p.HitActors.Contains(m.Index)) continue;
            var t = lenSq <= 1e-12 ? 0 : Math.Clamp(((m.X - fromX) * dx + (m.Z - fromZ) * dz) / lenSq, 0, 1);
            var cx = fromX + t * dx;
            var cz = fromZ + t * dz;
            var ox = m.X - cx;
            var oz = m.Z - cz;
            if (ox * ox + oz * oz > p.Radius * p.Radius || t >= bestT) continue;
            best = m;
            bestT = t;
            hitX = cx;
            hitZ = cz;
            hitFraction = t;
        }
        return best;
    }

    private CombatMonster? NearestProjectileTarget(double x, double z, double radius, HashSet<int> exclude)
    {
        CombatMonster? best = null;
        var bestDistSq = radius * radius;
        foreach (var m in AllCombatMonsters())
        {
            if (!m.Alive || exclude.Contains(m.Index)) continue;
            var dx = m.X - x;
            var dz = m.Z - z;
            var distSq = dx * dx + dz * dz;
            if (distSq > bestDistSq) continue;
            bestDistSq = distSq;
            best = m;
        }
        return best;
    }

    private static void AimAt(Projectile p, double x, double z)
    {
        var dx = x - p.X;
        var dz = z - p.Z;
        var length = Math.Sqrt(dx * dx + dz * dz);
        if (length > 1e-9) { p.DirX = dx / length; p.DirZ = dz / length; }
    }

    /// <summary>Two 0.5x fork children, which cannot fork/chain and inherit the hit-set.</summary>
    private IEnumerable<Projectile> SpawnForkChildren(Projectile parent)
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
