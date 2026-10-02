// Browser API client for /api/web/v1. Sessions ride an HttpOnly cookie, so requests use
// same-origin credentials; mutating calls add the X-Nord-Request CSRF header.

export interface CharacterSummary {
  characterId: string;
  displayName: string;
  class: number;
  race: number;
  gameMode: number;
  level: number;
  hardcore: boolean;
}

export interface WebItem {
  id: string;
  name: string;
  slot: number;
  definitionIntegerId: number;
  rarity: number;
}

export interface Snapshot {
  characterId: string;
  displayName: string;
  class: number;
  race: number;
  gameMode: number;
  level: number;
  experience: number;
  experienceForLevel: number;
  experienceForNextLevel: number;
  silver: number;
  opals: number;
  offense: number;
  defense: number;
  recovery: number;
  monsterKills: number;
  attributes: Record<string, number>;
  equipped: WebItem[];
  inventoryCount: number;
  activeSkillIds: number[];
  serverTimeUtc: string;
}

const BASE = "/api/web/v1";

export interface SkillStatus {
  slot: number;
  name: string;
  effect: string;
  cooldown: number;
  maxCooldown: number;
  manaCost: number;
  confidence: string;
  chains: number;
  special: string;
}

export interface MonsterState {
  index: number;
  name: string;
  level: number;
  isBoss: boolean;
  x: number;
  z: number;
  hp: number;
  maxHp: number;
  alive: boolean;
  stunTimer: number;
}

export interface CombatSnapshot {
  version: number;
  playerX: number;
  playerZ: number;
  playerHp: number;
  playerMaxHp: number;
  playerMana: number;
  playerMaxMana: number;
  playerShield: number;
  playerLevel: number;
  experience: number;
  silver: number;
  opals: number;
  kills: number;
  skills: SkillStatus[];
  offenseBuffRemaining: number;
  dungeonsCleared: number;
  bossKillsRemaining: number;
  bossAlive: boolean;
  monsters: MonsterState[];
}

export interface LootDrop {
  name: string;
  slot: number;
  rarity: number;
  level: number;
  offense: number;
  defense: number;
  recovery: number;
  affixes: string[];
}

/** Authoritative combat snapshot plus loot generated since the previous call. */
export interface CombatEnvelope {
  combat: CombatSnapshot;
  loot: LootDrop[];
}

export interface WebItemDetail {
  id: string;
  name: string;
  slot: number;
  rarity: number;
  equipSlot: number;
  equipped: boolean;
  offense: number;
  defense: number;
  recovery: number;
  affixes: string[];
}

export interface WebInventory {
  items: WebItemDetail[];
  offense: number;
  defense: number;
  recovery: number;
}

export interface MasterySpec {
  attributeId: number;
  attributeName: string;
  value: number;
  startValue: number;
  operator: number;
}

export interface MasteryView {
  name: string;
  integerId: number;
  rank: number;
  maxPoints: number;
  specs: MasterySpec[];
}

export interface SkillMasteryView {
  skillName: string;
  slot: number;
  rank: number;
  maxPoints: number;
  masteries: MasteryView[];
}

let commandCounter = 0;
export function newCommandId(): string {
  commandCounter += 1;
  return `${Date.now().toString(36)}-${commandCounter}-${Math.random().toString(36).slice(2, 8)}`;
}

async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
  const headers = new Headers(init.headers);
  if (init.method && init.method !== "GET") headers.set("X-Nord-Request", "1");
  const response = await fetch(BASE + path, {
    credentials: "same-origin",
    ...init,
    headers,
  });
  if (!response.ok) {
    let detail = "";
    try {
      detail = JSON.stringify(await response.json());
    } catch {
      detail = await response.text();
    }
    throw new Error(`${response.status} ${detail}`);
  }
  const text = await response.text();
  return text ? (JSON.parse(text) as T) : (undefined as T);
}

export const api = {
  health: () => request<{ status: string; contentVersion: string; dev: boolean }>("/health"),

  devSession: (name: string) =>
    request<{ userId: string; displayName: string }>("/dev/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    }),

  session: (identity: string, password: string, createAccount = false) =>
    request<{ userId: string; displayName: string }>("/session", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ identity, password, createAccount }),
    }),

  logout: () => request<{ status: string }>("/session/logout", { method: "POST" }),

  account: () =>
    request<{ userId: string; displayName: string; characters: CharacterSummary[] }>("/account"),

  createCharacter: (displayName: string, cls: number, race: number, gameMode: number) =>
    request<CharacterSummary>("/characters", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ displayName, class: cls, race, gameMode }),
    }),

  snapshot: (characterId: string) => request<Snapshot>(`/characters/${characterId}/snapshot`),

  state: (characterId: string) => request<CombatEnvelope>(`/characters/${characterId}/state`),

  inventory: (characterId: string) => request<WebInventory>(`/characters/${characterId}/inventory`),

  command: (
    characterId: string,
    commandId: string,
    expectedVersion: number,
    type: "move" | "skill" | "equip" | "unequip" | "mastery",
    options: { x?: number; z?: number; itemId?: string; skillId?: number; masteryId?: number } = {},
  ) =>
    request<{ applied: boolean; reason: string; state: CombatEnvelope }>(`/characters/${characterId}/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({
        commandId,
        expectedVersion,
        type,
        x: options.x ?? 0,
        z: options.z ?? 0,
        itemId: options.itemId ?? "00000000-0000-0000-0000-000000000000",
        skillId: options.skillId ?? 0,
        masteryId: options.masteryId ?? 0,
      }),
    }),

  powers: (characterId: string) => request<SkillMasteryView[]>(`/characters/${characterId}/powers`),

  merchantCatalog: () => request<MerchantProduct[]>("/merchant/catalog"),

  smelt: (characterId: string, itemIds: string[]) => npc(characterId, "smelt", { itemIds }),
  disassemble: (characterId: string, itemIds: string[]) => npc(characterId, "disassemble", { itemIds }),
  craftEssence: (characterId: string, itemIds: string[], targetItemId: string, overheat: number) =>
    npc(characterId, "essence", { itemIds, targetItemId, overheat }),
  craftRelic: (characterId: string, itemIds: string[], targetItemId: string) =>
    npc(characterId, "relic", { itemIds, targetItemId }),
  socket: (characterId: string, itemIds: string[], targetItemId: string) =>
    npc(characterId, "socket", { itemIds, targetItemId }),
  addSocket: (characterId: string, targetItemId: string) =>
    npc(characterId, "add-socket", { itemIds: [], targetItemId }),
  trade: (characterId: string, offerItemIds: string[], catalogItemId: string, stacks: number) =>
    request<NpcResult>(`/characters/${characterId}/npc/trade`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ offerItemIds, catalogItemId, stacks }),
    }),

  buy: (characterId: string, catalogItemId: string, useOpals: boolean) =>
    request<{ purchased: boolean; newBalance: number; inventory: WebInventory }>(`/characters/${characterId}/npc/buy`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ catalogItemId, useOpals }),
    }),

  attributes: (characterId: string) => request<WebAttributes>(`/characters/${characterId}/attributes`),

  allocateAttributes: (characterId: string, deltas: Partial<Record<AttributeKey, number>>) =>
    request<{ available: number; attributes: WebAttributes }>(`/characters/${characterId}/attributes`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(deltas),
    }),
};

export type AttributeKey = "strength" | "dexterity" | "intelligence" | "vitality" | "constitution" | "agility" | "mindpower";

export interface WebAttributes {
  available: number;
  strengthAllocated: number; dexterityAllocated: number; intelligenceAllocated: number; vitalityAllocated: number;
  constitutionAllocated: number; agilityAllocated: number; mindpowerAllocated: number;
  strength: number; dexterity: number; intelligence: number; vitality: number; constitution: number; agility: number; mindpower: number;
}

export interface MerchantProduct {
  itemId: string;
  name: string;
  definitionIntegerId: number;
  silverPrice: number;
  opalPrice: number;
}

export interface NpcResult {
  successful: boolean;
  result?: unknown;
  inventory: WebInventory;
}

function npc(characterId: string, action: string, body: Record<string, unknown>): Promise<NpcResult> {
  return request<NpcResult>(`/characters/${characterId}/npc/${action}`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
}
