// Content metadata exported from gamedata_decrypted by tools/web-content/export_content.py.
// The client never hardcodes race/class/monster names or icon paths.

export interface RaceInfo {
  integerId: number;
  name: string;
  nameKey?: string;
  icon: string | null;
  attackPower: number;
  magicPower: number;
  defensePower: number;
  thiefPower: number;
  stamina: number;
}

export interface ClassInfo {
  integerId: number;
  name: string;
  nameKey?: string;
  hidden: boolean;
}

export interface MonsterInfo {
  integerId: number;
  typeId: string;
  name: string;
  nameKey?: string;
  portrait: string | null;
}

export interface ContentManifest {
  contentVersion: string;
  races: RaceInfo[];
  classes: ClassInfo[];
  monsters: MonsterInfo[];
  monsterIcons: string[];
  check: { avatarsAvailable: number; iconReferencesMissing: string[] };
}

export interface ActivePower {
  slot: number;
  id: string;
  integerId: number;
  name: string;
  description: string;
  icon: string | null;
  type: string;
  tags: string[];
  effect: string;
  multiplier: number;
  cooldown: number;
  radius: number;
  healPercent: number;
  buffBonus: number;
  buffSeconds: number;
}

export interface PassivePower {
  id: string;
  integerId: number;
  name: string;
  description: string;
  icon: string | null;
  type: string;
  tags: string[];
  effect: string;
  offenseBonus: number;
  healthBonus: number;
}

export interface ClassPowers {
  name: string;
  active: ActivePower[];
  passive: PassivePower;
  activePoolSize: number;
  passivePoolSize: number;
}

export interface PowersDocument {
  contentVersion: string;
  classes: Record<string, ClassPowers>;
}

let cached: ContentManifest | null = null;
let cachedPowers: PowersDocument | null = null;

export async function loadContent(): Promise<ContentManifest> {
  if (cached) return cached;
  const response = await fetch("/assets/content.json");
  if (!response.ok) throw new Error("content.json missing; run tools/web-content/export_content.py");
  cached = (await response.json()) as ContentManifest;
  return cached;
}

export async function loadPowers(): Promise<PowersDocument> {
  if (cachedPowers) return cachedPowers;
  const response = await fetch("/assets/powers.json");
  if (!response.ok) throw new Error("powers.json missing; run tools/web-content/export_powers.py");
  cachedPowers = (await response.json()) as PowersDocument;
  return cachedPowers;
}

export function raceIcon(content: ContentManifest, raceId: number): string {
  const race = content.races.find((r) => r.integerId === raceId);
  return race?.icon ? `/assets/avatars/${race.icon}.png` : "/assets/avatars/RaceHuman.png";
}

export function className(content: ContentManifest, classId: number): string {
  return content.classes.find((c) => c.integerId === classId)?.name ?? `Class ${classId}`;
}

export function monsterIcon(content: ContentManifest, monster: MonsterInfo): string {
  const icon = monster.portrait && content.monsterIcons.includes(monster.portrait)
    ? monster.portrait
    : null;
  if (icon) return `/assets/avatars/${icon}.png`;
  // Deterministic fallback so the same monster always gets the same token.
  const hash = [...monster.name].reduce((acc, ch) => (acc * 31 + ch.charCodeAt(0)) >>> 0, 7);
  const pick = content.monsterIcons[hash % Math.max(1, content.monsterIcons.length)];
  return `/assets/avatars/${pick ?? "MonstersAvatarIcons_48"}.png`;
}
