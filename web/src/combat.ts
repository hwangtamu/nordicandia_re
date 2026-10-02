// Client-side mirror of server/Nordicandia.Simulation/CombatModel. The M0 web loop resolves
// hits locally for responsiveness, but every value here is tagged Provisional: the
// authoritative, calibrated formulas will live on the server (see docs/web/M0_RULES.md).

export const XP_ADD = 350;
export const XP_MULT = 20;
export const XP_POWER = 1.7;

export function experienceForLevel(level: number): number {
  return level <= 1 ? 0 : XP_ADD + XP_MULT * Math.pow(level, XP_POWER);
}

export function levelForExperience(experience: number): number {
  if (!Number.isFinite(experience) || experience <= XP_ADD) return 1;
  return Math.max(1, Math.floor(Math.round(Math.pow((experience - XP_ADD) / XP_MULT, 1 / XP_POWER) * 1e6) / 1e6));
}

export function mitigation(defense: number, attackerLevel: number): number {
  const scale = 50 + 10 * Math.max(1, attackerLevel);
  const d = Math.max(0, defense);
  return d / (d + scale);
}

export interface DamageOutcome {
  damage: number;
  critical: boolean;
}

export function rollDamage(
  offense: number,
  defense: number,
  attackerLevel: number,
  options: { skillMultiplier?: number; critChance?: number; critMultiplier?: number; variance?: number } = {},
): DamageOutcome {
  const skill = options.skillMultiplier ?? 1;
  const critChance = options.critChance ?? 0.08;
  const critMultiplier = options.critMultiplier ?? 1.6;
  const variance = options.variance ?? 0.12;
  const mitigated = Math.max(0, offense) * skill * (1 - mitigation(defense, attackerLevel));
  const jitter = 1 + (Math.random() * 2 - 1) * variance;
  const critical = Math.random() < critChance;
  const damage = Math.max(1, mitigated * Math.max(0.05, jitter) * (critical ? critMultiplier : 1));
  return { damage, critical };
}
