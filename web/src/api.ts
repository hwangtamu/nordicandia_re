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

export interface MonsterState {
  index: number;
  name: string;
  level: number;
  x: number;
  z: number;
  hp: number;
  maxHp: number;
  alive: boolean;
}

export interface CombatState {
  version: number;
  playerX: number;
  playerZ: number;
  playerHp: number;
  playerMaxHp: number;
  playerLevel: number;
  experience: number;
  silver: number;
  opals: number;
  kills: number;
  skillCooldown: number;
  monsters: MonsterState[];
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

  state: (characterId: string) => request<CombatState>(`/characters/${characterId}/state`),

  command: (
    characterId: string,
    commandId: string,
    expectedVersion: number,
    type: "move" | "skill",
    x = 0,
    z = 0,
  ) =>
    request<{ applied: boolean; reason: string; snapshot: CombatState }>(`/characters/${characterId}/commands`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ commandId, expectedVersion, type, x, z }),
    }),
};
