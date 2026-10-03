import "./style.css";
import { api, AttributeKey, BlessingsView, CharacterSummary, CombatEnvelope, Loadout, LootDrop, MerchantProduct, newCommandId, NpcResult, OfflineView, PowerPoolOption, SkillMasteryView, Snapshot, WebAttributes, WebInventory, WebPortalState } from "./api";
import { loadContent, loadPowers, ContentManifest, ClassPowers, className } from "./content";
import { HudState, World } from "./game";

const RARITY_NAMES = ["F", "E", "D", "C", "B", "A", "AA", "AAA", "AAAA", "AAAAA", "S", "SS"];
const RARITY_COLORS = ["#b9b9b9", "#8fd18a", "#7fc6e8", "#c9a24a", "#e0894a", "#e05a5a", "#c05ae0", "#5a7fe0", "#e0c05a", "#e05ac0", "#ff5a3c", "#fff3b0"];

const app = document.getElementById("app")!;
app.innerHTML = `
  <canvas id="game"></canvas>
  <div id="overlay"></div>
  <div id="boot" class="panel">
    <h1>Nordicandia <span>web M2</span></h1>
    <p class="sub">3D dungeon kit + avatar tokens, server-authoritative combat, loot &amp; equipment.</p>
    <label>Account name <input id="boot-name" value="m2" autocomplete="off" /></label>
    <div class="row">
      <label>Class <select id="boot-class"></select></label>
      <label>Race <select id="boot-race"></select></label>
    </div>
    <button id="boot-enter">Enter world</button>
    <div id="boot-status" class="status"></div>
  </div>
  <div id="hud" hidden>
    <div class="hud-top-left">
      <div class="name-row"><span id="hud-name">—</span><span id="hud-class"></span></div>
      <div class="bar hp"><div id="hud-hp-fill"></div><span id="hud-hp-text"></span></div>
      <div class="bar mana"><div id="hud-mana-fill"></div><span id="hud-mana-text"></span></div>
      <div class="bar xp"><div id="hud-xp-fill"></div><span id="hud-xp-text"></span></div>
      <div class="passive" id="hud-passive"></div>
      <div class="stats"><span id="hud-stats">OFF 0 · DEF 0 · REC 0</span></div>
    </div>
    <div class="hud-top-right">
      <div>Lv <b id="hud-level">1</b> · <span id="hud-dungeons">0</span> cleared</div>
      <div><span id="hud-silver">0</span> silver · <span id="hud-opals">0</span> opals</div>
      <div><span id="hud-kills">0</span> kills · <span id="hud-alive">0</span> monsters</div>
      <div id="hud-boss" class="boss hidden">Boss in <span id="hud-boss-count">0</span> kills</div>
      <div class="source">content <span id="hud-content">…</span></div>
    </div>
    <div id="loot-feed" class="loot-feed"></div>
    <div id="inventory" class="inventory hidden">
      <div class="inv-head">
        <span>Bag</span>
        <button id="inv-close" class="icon-btn">×</button>
      </div>
      <div id="inv-items" class="inv-items"></div>
    </div>
    <div id="powers" class="inventory hidden">
      <div class="inv-head">
        <span>Skills <small id="powers-points"></small></span>
        <button id="powers-close" class="icon-btn">×</button>
      </div>
      <div id="powers-items" class="inv-items"></div>
    </div>
    <div id="loadout" class="inventory hidden">
      <div class="inv-head">
        <span>Loadout <small id="loadout-points"></small></span>
        <button id="loadout-close" class="icon-btn">×</button>
      </div>
      <div class="npc-hint">Equip up to 6 active skills and 3 masteries from your class pool.</div>
      <div id="loadout-items" class="inv-items"></div>
      <div class="npc-foot">
        <button id="loadout-save" class="npc-action">Save loadout</button>
        <span id="loadout-status" class="npc-status"></span>
      </div>
    </div>
    <div id="attrs" class="inventory hidden">
      <div class="inv-head">
        <span>Attributes <small id="attrs-points"></small></span>
        <button id="attrs-close" class="icon-btn">×</button>
      </div>
      <div id="attrs-items" class="inv-items"></div>
    </div>
    <div id="offline" class="offline-banner hidden">
      <span id="offline-text"></span>
      <button id="offline-claim" class="npc-action">Claim</button>
    </div>
    <div id="blessings" class="inventory hidden">
      <div class="inv-head">
        <span>Offerings <small id="blessings-opals"></small></span>
        <button id="blessings-close" class="icon-btn">×</button>
      </div>
      <div class="npc-hint">Buy an Aesir blessing with opals; the effect is active until it expires.</div>
      <div id="blessings-items" class="inv-items"></div>
    </div>
    <div id="portal" class="inventory hidden">
      <div class="inv-head">
        <span>Niflheim Portal</span>
        <button id="portal-close" class="icon-btn">×</button>
      </div>
      <div id="portal-body" class="inv-items"></div>
      <div class="npc-foot">
        <button id="portal-action" class="npc-action">Enter Niflheim</button>
        <span id="portal-status" class="npc-status"></span>
      </div>
    </div>
    <div id="npc" class="npc-panel hidden">
      <div class="npc-head">
        <span id="npc-title">Blacksmith</span>
        <button id="npc-close" class="icon-btn">×</button>
      </div>
      <div id="npc-tabs" class="npc-tabs"></div>
      <div id="npc-hint" class="npc-hint"></div>
      <div id="npc-body" class="npc-body"></div>
      <div class="npc-foot">
        <button id="npc-action" class="npc-action">Craft</button>
        <span id="npc-status" class="npc-status"></span>
      </div>
    </div>
    <div class="hud-bottom">
      <button id="hud-skill" class="skill">Skill 1 <small>1</small></button>
      <button id="hud-skill-2" class="skill">Skill 2 <small>2</small></button>
      <button id="hud-skill-3" class="skill">Skill 3 <small>3</small></button>
      <button id="hud-skill-4" class="skill hidden">Skill 4 <small>4</small></button>
      <button id="hud-skill-5" class="skill hidden">Skill 5 <small>5</small></button>
      <button id="hud-skill-6" class="skill hidden">Skill 6 <small>6</small></button>
      <button id="hud-loadout" class="skill alt">Loadout <small>L</small></button>
      <button id="hud-auto" class="skill alt">Auto-move: ON <small>Tab</small></button>
      <button id="hud-bag" class="skill alt">Bag <small>B</small></button>
      <button id="hud-powers" class="skill alt">Skills <small>P</small></button>
      <button id="hud-smith" class="skill alt">Blacksmith <small>K</small></button>
      <button id="hud-merchant" class="skill alt">Merchant <small>M</small></button>
      <button id="hud-attrs" class="skill alt">Attrs <small>C</small></button>
      <button id="hud-blessings" class="skill alt">Offerings <small>O</small></button>
      <button id="hud-portal" class="skill alt">Portal <small>N</small></button>
    </div>
    <div id="hud-message" class="toast"></div>
  </div>
`;

const canvas = document.getElementById("game") as HTMLCanvasElement;
const overlay = document.getElementById("overlay") as HTMLDivElement;
const bootPanel = document.getElementById("boot") as HTMLDivElement;
const hud = document.getElementById("hud") as HTMLDivElement;
const statusEl = document.getElementById("boot-status") as HTMLDivElement;
const contentEl = document.getElementById("hud-content") as HTMLElement;
const inventoryEl = document.getElementById("inventory") as HTMLDivElement;
const invItemsEl = document.getElementById("inv-items") as HTMLDivElement;
const powersEl = document.getElementById("powers") as HTMLDivElement;
const powersItemsEl = document.getElementById("powers-items") as HTMLDivElement;
const powersPointsEl = document.getElementById("powers-points") as HTMLElement;
const loadoutEl = document.getElementById("loadout") as HTMLDivElement;
const loadoutItemsEl = document.getElementById("loadout-items") as HTMLDivElement;
const loadoutPointsEl = document.getElementById("loadout-points") as HTMLElement;
const loadoutStatusEl = document.getElementById("loadout-status") as HTMLElement;
const lootFeedEl = document.getElementById("loot-feed") as HTMLDivElement;

let content: ContentManifest;
let powers: Record<string, ClassPowers> = {};
let classPowers: ClassPowers | null = null;
let world: World | null = null;
let character: CharacterSummary | null = null;

function setStatus(text: string, error = false): void {
  statusEl.textContent = text;
  statusEl.classList.toggle("error", error);
}

async function boot(): Promise<void> {
  content = await loadContent();
  try {
    powers = (await loadPowers()).kits;
  } catch (error) {
    console.warn("powers.json unavailable", error);
  }
  contentEl.textContent = content.contentVersion;
  try {
    const health = await api.health();
    if (health.contentVersion !== content.contentVersion) {
      contentEl.textContent = `${content.contentVersion} (server ${health.contentVersion})`;
      contentEl.style.color = "#ffb36b";
    }
  } catch {
    contentEl.style.color = "#ff8a7a";
    contentEl.textContent = `${content.contentVersion} (server unreachable)`;
  }
  const classSelect = document.getElementById("boot-class") as HTMLSelectElement;
  const raceSelect = document.getElementById("boot-race") as HTMLSelectElement;
  classSelect.innerHTML = content.classes
    .filter((c) => !c.hidden)
    .map((c) => `<option value="${c.integerId}">${c.name}</option>`)
    .join("");
  raceSelect.innerHTML = content.races
    .map((r) => `<option value="${r.integerId}">${r.name}</option>`)
    .join("");
  const mage = content.classes.find((c) => c.name === "Mage");
  if (mage) classSelect.value = String(mage.integerId);

  document.getElementById("boot-enter")!.addEventListener("click", () => void enterWorld());
}

async function enterWorld(): Promise<void> {
  const name = (document.getElementById("boot-name") as HTMLInputElement).value.trim() || "m2";
  const classId = Number((document.getElementById("boot-class") as HTMLSelectElement).value);
  const raceId = Number((document.getElementById("boot-race") as HTMLSelectElement).value);
  const button = document.getElementById("boot-enter") as HTMLButtonElement;
  button.disabled = true;
  try {
    setStatus("Signing in…");
    await api.devSession(name);
    let account = await api.account();
    setStatus(`Signed in as ${account.displayName}`);
    if (account.characters.length === 0) {
      setStatus("Creating character…");
      await api.createCharacter(capitalize(name), classId, raceId, 1);
      account = await api.account();
    }
    character = account.characters[0];
    setStatus("Reading snapshot…");
    const snapshot: Snapshot = await api.snapshot(character.characterId);
    await startGame(snapshot, character);
  } catch (error) {
    console.error(error);
    setStatus(`Failed: ${error instanceof Error ? error.message : String(error)}`, true);
    button.disabled = false;
  }
}

async function startGame(snapshot: Snapshot, selected: CharacterSummary): Promise<void> {
  bootPanel.hidden = true;
  hud.hidden = false;
  (document.getElementById("hud-name") as HTMLElement).textContent = selected.displayName;
  (document.getElementById("hud-class") as HTMLElement).textContent =
    `${className(content, selected.class)} · Lv ${snapshot.level}`;

  const characterId = selected.characterId;
  classPowers = powers[String(selected.class)] ?? null;
  (document.getElementById("hud-passive") as HTMLElement).textContent = classPowers
    ? `Passive · ${classPowers.passive.name}`
    : "";
  const sendCommand = async (
    type: "move" | "skill" | "equip" | "unequip" | "mastery",
    options: { x?: number; z?: number; itemId?: string; skillId?: number; masteryId?: number } = {},
  ): Promise<void> => {
    const expectedVersion = world?.currentVersion() ?? 0;
    try {
      const result = await api.command(characterId, newCommandId(), expectedVersion, type, options);
      world?.pushState(result.state);
      if (!result.applied && result.reason === "cooldown") showHudMessage("Skill on cooldown");
      if (!result.applied && result.reason === "no_target") showHudMessage("No target in range");
      if (!result.applied && result.reason === "no_mana") showHudMessage("Not enough mana");
      if (!result.applied && result.reason === "stale_command") showHudMessage("Resyncing…");
      if (type === "equip" || type === "unequip") await refreshInventory();
    } catch (error) {
      console.warn("command failed", error);
    }
  };

  const initial: CombatEnvelope = await api.state(characterId);
  npcCharacterId = characterId;
  const refreshInventory = async (): Promise<void> => {
    try {
      const inventory = await api.inventory(characterId);
      inventoryCache = inventory;
      renderInventory(inventory, (itemId, equipped) => void sendCommand(equipped ? "unequip" : "equip", { itemId }));
      if (npcMode) renderNpc();
    } catch (error) {
      console.warn("inventory failed", error);
    }
  };
  npcActionHandler = async (): Promise<void> => {
    const picked = Array.from(npcPicked);
    const target = npcTarget ?? "";
    npcStatusEl.textContent = "Working…";
    try {
      let result: NpcResult;
      switch (npcTab) {
        case "smelt": result = await api.smelt(characterId, picked); break;
        case "disassemble": result = await api.disassemble(characterId, picked); break;
        case "essence": result = await api.craftEssence(characterId, picked, target, 10); break;
        case "relic": result = await api.craftRelic(characterId, picked, target); break;
        case "socket": result = await api.socket(characterId, picked, target); break;
        case "add-socket": result = await api.addSocket(characterId, target); break;
        case "trade": result = await api.trade(characterId, picked, target, 1); break;
        default: return;
      }
      npcStatusEl.textContent = result.successful ? "Done" : "No valid items for that operation";
      if (result.successful) { npcPicked.clear(); npcTarget = null; }
      await refreshInventory();
    } catch (error) {
      npcStatusEl.textContent = `Failed: ${error instanceof Error ? error.message : String(error)}`;
    }
    renderNpc();
  };
  const refreshPowers = async (): Promise<void> => {
    try {
      const rows = await api.powers(characterId);
      renderPowers(rows, (masteryId) => void sendCommand("mastery", { masteryId }).then(() => refreshPowers()));
    } catch (error) {
      console.warn("powers failed", error);
    }
  };
  world = new World(canvas, overlay, content, snapshot.race, {
    onHud: renderHud,
    onLoot: (loot) => {
      showLoot(loot);
      void refreshInventory();
    },
    pollState: async () => {
      try {
        return await api.state(characterId);
      } catch {
        return null;
      }
    },
    moveTo: (x, z) => void sendCommand("move", { x, z }),
    castSkill: (skillId) => void sendCommand("skill", { skillId }),
  });
  await world.start(initial);
  await refreshInventory();
  await refreshPowers();

  const skillButtons: [string, number][] = [
    ["hud-skill", 0],
    ["hud-skill-2", 1],
    ["hud-skill-3", 2],
    ["hud-skill-4", 3],
    ["hud-skill-5", 4],
    ["hud-skill-6", 5],
  ];
  for (const [id, skillId] of skillButtons)
    document.getElementById(id)!.addEventListener("click", () => void sendCommand("skill", { skillId }));
  loadoutRefreshHandler = async (): Promise<void> => {
    try {
      const data = await api.loadout(characterId);
      loadoutCache = data;
      loadoutSelection = { active: new Set(data.active), passive: new Set(data.passive) };
      poolByName.clear();
      for (const option of [...data.poolActive, ...data.poolPassive]) poolByName.set(option.name, option);
      renderLoadout();
    } catch (error) {
      console.warn("loadout failed", error);
    }
  };
  loadoutSaveHandler = async (): Promise<void> => {
    loadoutStatusEl.textContent = "Saving…";
    try {
      const result = await api.setLoadout(characterId, [...loadoutSelection.active], [...loadoutSelection.passive]);
      loadoutStatusEl.textContent = result.applied ? "Saved" : `Rejected: ${result.reason}`;
      if (result.applied) {
        await refreshPowers();
        await loadoutRefreshHandler?.();
      }
    } catch (error) {
      loadoutStatusEl.textContent = `Failed: ${error instanceof Error ? error.message : String(error)}`;
    }
  };
  document.getElementById("hud-loadout")!.addEventListener("click", () => toggleLoadout());
  document.getElementById("loadout-close")!.addEventListener("click", () => toggleLoadout(false));
  document.getElementById("loadout-save")!.addEventListener("click", () => void loadoutSaveHandler?.());
  const autoButton = document.getElementById("hud-auto") as HTMLButtonElement;
  autoButton.addEventListener("click", () => {
    const on = world?.toggleAutoMove() ?? false;
    autoButton.firstChild!.textContent = `Auto-move: ${on ? "ON" : "OFF"} `;
  });
  document.getElementById("hud-bag")!.addEventListener("click", () => toggleBag());
  document.getElementById("inv-close")!.addEventListener("click", () => toggleBag(false));
  document.getElementById("hud-powers")!.addEventListener("click", () => togglePowers());
  document.getElementById("powers-close")!.addEventListener("click", () => togglePowers(false));
  document.getElementById("hud-smith")!.addEventListener("click", () => openNpc("smith"));
  document.getElementById("hud-merchant")!.addEventListener("click", () => openNpc("merchant"));
  document.getElementById("npc-close")!.addEventListener("click", () => closeNpc());
  npcActionEl.addEventListener("click", () => void npcActionHandler?.());
  document.getElementById("hud-attrs")!.addEventListener("click", () => toggleAttrs());
  document.getElementById("attrs-close")!.addEventListener("click", () => toggleAttrs(false));
  attrsRefreshHandler = async (): Promise<void> => {
    try { attributesCache = await api.attributes(characterId); renderAttrs(); }
    catch (error) { console.warn("attributes failed", error); }
  };
  allocateHandler = async (key: AttributeKey): Promise<void> => {
    try {
      const result = await api.allocateAttributes(characterId, { [key]: 1 });
      attributesCache = result.attributes;
      renderAttrs();
      await refreshInventory();
    } catch (error) {
      console.warn("allocate failed", error);
    }
  };

  // ----- M3: offline rewards, offerings, Niflheim portal -----
  document.getElementById("hud-blessings")!.addEventListener("click", () => toggleBlessings());
  document.getElementById("blessings-close")!.addEventListener("click", () => toggleBlessings(false));
  blessingsRefreshHandler = async (): Promise<void> => {
    try { blessingsCache = await api.blessings(characterId); renderBlessings(); }
    catch (error) { console.warn("blessings failed", error); }
  };
  offeringHandler = async (type: number, size: number): Promise<void> => {
    try {
      const result = await api.offering(characterId, type, size);
      blessingsCache = result.view;
      renderBlessings();
      await refreshInventory();
    } catch (error) { console.warn("offering failed", error); }
  };

  document.getElementById("hud-portal")!.addEventListener("click", () => togglePortal());
  document.getElementById("portal-close")!.addEventListener("click", () => togglePortal(false));
  portalActionEl.addEventListener("click", () => void portalActionHandler?.());
  portalRefreshHandler = async (): Promise<void> => {
    try { portalCache = await api.portal(characterId); renderPortal(); }
    catch (error) { console.warn("portal failed", error); }
  };
  portalActionHandler = async (): Promise<void> => {
    try {
      if (portalCache?.inNiflheim) {
        world?.pushState((await api.returnPortal(characterId)).state);
      } else if (portalCache?.hasPortal) {
        world?.pushState((await api.enterPortal(characterId, portalCache.portalItemId)).state);
      }
      portalCache = await api.portal(characterId);
      renderPortal();
      await refreshInventory();
    } catch (error) { console.warn("portal action failed", error); }
  };

  document.getElementById("offline-claim")!.addEventListener("click", () => void offlineClaimHandler?.());
  offlineClaimHandler = async (): Promise<void> => {
    try {
      offlineCache = await api.claimOffline(characterId);
      offlineEl.classList.add("hidden");
      world?.pushState(await api.state(characterId));
      await refreshInventory();
    } catch (error) { console.warn("offline claim failed", error); }
  };
  try {
    offlineCache = await api.offline(characterId);
    renderOffline();
  } catch (error) { console.warn("offline failed", error); }
  const keyToSkill: Record<string, number> = {
    Digit1: 0, Digit2: 1, Digit3: 2, Digit4: 3, Digit5: 4, Digit6: 5,
    Numpad1: 0, Numpad2: 1, Numpad3: 2, Numpad4: 3, Numpad5: 4, Numpad6: 5,
  };
  window.addEventListener("keydown", (event) => {
    if (event.code === "Space") {
      event.preventDefault();
      void sendCommand("skill", { skillId: 0 });
    } else if (event.code in keyToSkill) {
      event.preventDefault();
      void sendCommand("skill", { skillId: keyToSkill[event.code] });
    } else if (event.code === "Tab") {
      event.preventDefault();
      const on = world?.toggleAutoMove() ?? false;
      autoButton.firstChild!.textContent = `Auto-move: ${on ? "ON" : "OFF"} `;
    } else if (event.code === "KeyB") {
      event.preventDefault();
      toggleBag();
    } else if (event.code === "KeyP") {
      event.preventDefault();
      togglePowers();
    } else if (event.code === "KeyK") {
      event.preventDefault();
      openNpc("smith");
    } else if (event.code === "KeyM") {
      event.preventDefault();
      openNpc("merchant");
    } else if (event.code === "KeyC") {
      event.preventDefault();
      toggleAttrs();
    } else if (event.code === "KeyO") {
      event.preventDefault();
      toggleBlessings();
    } else if (event.code === "KeyN") {
      event.preventDefault();
      togglePortal();
    } else if (event.code === "KeyL") {
      event.preventDefault();
      toggleLoadout();
    } else if (event.code === "Escape") {
      closeNpc();
    }
  });
}

function toggleBag(force?: boolean): void {
  const show = force ?? inventoryEl.classList.contains("hidden");
  if (show) { powersEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); }
  inventoryEl.classList.toggle("hidden", !show);
}

function togglePowers(force?: boolean): void {
  const show = force ?? powersEl.classList.contains("hidden");
  if (show) { inventoryEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); }
  powersEl.classList.toggle("hidden", !show);
}

function renderPowers(rows: SkillMasteryView[], onAllocate: (masteryId: number) => void): void {
  const spent = rows.reduce((sum, r) => sum + r.rank, 0);
  const total = rows.reduce((sum, r) => sum + r.maxPoints, 0);
  powersPointsEl.textContent = `${spent} / ${total} points`;
  powersItemsEl.innerHTML = "";
  for (const skill of rows) {
    const header = document.createElement("div");
    header.className = "power-skill";
    header.textContent = `${skill.skillName}  (${skill.rank}/${skill.maxPoints})`;
    powersItemsEl.appendChild(header);
    for (const mastery of skill.masteries) {
      const row = document.createElement("div");
      row.className = "inv-row power-row";
      const specs = mastery.specs.map((s) => `${s.attributeName} ${s.value >= 0 ? "+" : ""}${s.value}`).join(", ");
      row.innerHTML = `
        <span class="power-rank">${mastery.rank}/${mastery.maxPoints}</span>
        <span class="inv-name">${escapeHtml(prettyMastery(mastery.name))}<span class="inv-affixes">${escapeHtml(specs)}</span></span>
      `;
      const button = document.createElement("button");
      button.className = "inv-btn";
      button.textContent = "+";
      button.disabled = mastery.rank >= mastery.maxPoints;
      button.addEventListener("click", () => onAllocate(mastery.integerId));
      row.appendChild(button);
      powersItemsEl.appendChild(row);
    }
  }
}

function prettyMastery(name: string): string {
  return name.replace(/^Mastery/, "").replace(/([a-z])([A-Z])/g, "$1 $2");
}

function renderInventory(inventory: WebInventory, onToggle: (itemId: string, equipped: boolean) => void): void {
  (document.getElementById("hud-stats") as HTMLElement).textContent =
    `OFF ${Math.round(inventory.offense)} · DEF ${Math.round(inventory.defense)} · REC ${Math.round(inventory.recovery)}`;
  if (inventory.items.length === 0) {
    invItemsEl.innerHTML = `<div class="inv-empty">No items yet — kill monsters to find loot.</div>`;
    return;
  }
  invItemsEl.innerHTML = "";
  const sorted = [...inventory.items].sort((a, b) => Number(b.equipped) - Number(a.equipped) || b.rarity - a.rarity);
  for (const item of sorted) {
    const row = document.createElement("div");
    row.className = `inv-row${item.equipped ? " equipped" : ""}${itemGlow(item.name)}`;
    const stats = `+${Math.round(item.offense)} off · +${Math.round(item.defense)} def · +${Math.round(item.recovery)} rec`;
    const affixes = item.affixes && item.affixes.length ? item.affixes.map((a) => `<span class="affix">${escapeHtml(a)}</span>`).join(" ") : "";
    row.innerHTML = `
      <span class="rarity" style="color:${RARITY_COLORS[item.rarity] ?? "#ccc"}">${RARITY_NAMES[item.rarity] ?? "?"}</span>
      <span class="inv-name">${escapeHtml(item.name)}${affixes ? `<span class="inv-affixes">${affixes}</span>` : ""}</span>
      <span class="inv-stats">${stats}</span>
    `;
    const button = document.createElement("button");
    button.className = "inv-btn";
    button.textContent = item.equipped ? "Unequip" : "Equip";
    button.addEventListener("click", () => onToggle(item.id, item.equipped));
    row.appendChild(button);
    invItemsEl.appendChild(row);
  }
}

type NpcTab = "smelt" | "disassemble" | "essence" | "relic" | "socket" | "add-socket" | "trade" | "buy" | "set";

const npcEl = document.getElementById("npc") as HTMLDivElement;
const npcTitleEl = document.getElementById("npc-title") as HTMLElement;
const npcTabsEl = document.getElementById("npc-tabs") as HTMLDivElement;
const npcHintEl = document.getElementById("npc-hint") as HTMLDivElement;
const npcBodyEl = document.getElementById("npc-body") as HTMLDivElement;
const npcActionEl = document.getElementById("npc-action") as HTMLButtonElement;
const npcStatusEl = document.getElementById("npc-status") as HTMLElement;

let npcMode: "smith" | "merchant" | null = null;
let npcTab: NpcTab = "smelt";
let npcPicked = new Set<string>();
let npcTarget: string | null = null;
let npcCatalog: MerchantProduct[] = [];
let inventoryCache: WebInventory | null = null;
let npcCharacterId = "";
let npcActionHandler: (() => Promise<void>) | null = null;

const attrsEl = document.getElementById("attrs") as HTMLDivElement;
const attrsItemsEl = document.getElementById("attrs-items") as HTMLDivElement;
const attrsPointsEl = document.getElementById("attrs-points") as HTMLElement;
let attributesCache: WebAttributes | null = null;
let attrsRefreshHandler: (() => Promise<void>) | null = null;
let allocateHandler: ((key: AttributeKey) => Promise<void>) | null = null;

const ATTR_LABELS: [AttributeKey, string][] = [
  ["strength", "Strength"], ["dexterity", "Dexterity"], ["intelligence", "Intelligence"],
  ["vitality", "Vitality"], ["constitution", "Constitution"], ["agility", "Agility"], ["mindpower", "Mindpower"],
];

function toggleAttrs(force?: boolean): void {
  const show = force ?? attrsEl.classList.contains("hidden");
  if (show) { inventoryEl.classList.add("hidden"); powersEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); closeNpc(); void attrsRefreshHandler?.(); }
  attrsEl.classList.toggle("hidden", !show);
}

function renderAttrs(): void {
  const a = attributesCache;
  if (!a) { attrsItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  attrsPointsEl.textContent = `${a.available} points`;
  attrsItemsEl.innerHTML = "";
  const record = a as unknown as Record<string, number>;
  for (const [key, label] of ATTR_LABELS) {
    const allocated = record[`${key}Allocated`] ?? 0;
    const total = record[key] ?? 0;
    const row = document.createElement("div");
    row.className = "inv-row";
    row.innerHTML = `<span class="inv-name">${label}</span><span class="inv-stats">alloc ${allocated} · total ${Math.round(total)}</span>`;
    const button = document.createElement("button");
    button.className = "inv-btn";
    button.textContent = "+";
    button.disabled = a.available <= 0;
    button.addEventListener("click", () => void allocateHandler?.(key));
    row.appendChild(button);
    attrsItemsEl.appendChild(row);
  }
}

// ----- offline rewards / offerings / Niflheim portal (M3) -----
const offlineEl = document.getElementById("offline") as HTMLDivElement;
const offlineTextEl = document.getElementById("offline-text") as HTMLElement;
const blessingsEl = document.getElementById("blessings") as HTMLDivElement;
const blessingsItemsEl = document.getElementById("blessings-items") as HTMLDivElement;
const blessingsOpalsEl = document.getElementById("blessings-opals") as HTMLElement;
const portalEl = document.getElementById("portal") as HTMLDivElement;
const portalBodyEl = document.getElementById("portal-body") as HTMLDivElement;
const portalActionEl = document.getElementById("portal-action") as HTMLButtonElement;
const portalStatusEl = document.getElementById("portal-status") as HTMLElement;

let offlineCache: OfflineView | null = null;
let blessingsCache: BlessingsView | null = null;
let portalCache: WebPortalState | null = null;
let offlineClaimHandler: (() => Promise<void>) | null = null;
let blessingsRefreshHandler: (() => Promise<void>) | null = null;
let offeringHandler: ((type: number, size: number) => Promise<void>) | null = null;
let portalRefreshHandler: (() => Promise<void>) | null = null;
let portalActionHandler: (() => Promise<void>) | null = null;

function renderOffline(): void {
  const o = offlineCache;
  if (!o || !o.claimable || o.experience <= 0) { offlineEl.classList.add("hidden"); return; }
  offlineTextEl.textContent = `Welcome back — ${(o.eligibleSeconds / 3600).toFixed(1)}h away. Claim ${Math.round(o.experience)} XP.`;
  offlineEl.classList.remove("hidden");
}

function toggleBlessings(force?: boolean): void {
  const show = force ?? blessingsEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    loadoutEl.classList.add("hidden");
    portalEl.classList.add("hidden");
    closeNpc();
    void blessingsRefreshHandler?.();
  }
  blessingsEl.classList.toggle("hidden", !show);
}

function renderBlessings(): void {
  const data = blessingsCache;
  if (!data) { blessingsItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  blessingsOpalsEl.textContent = `${data.opals} opals`;
  blessingsItemsEl.innerHTML = "";
  for (const blessing of data.active) {
    const row = document.createElement("div");
    row.className = `inv-row bless-row${blessing.active ? " equipped" : ""}`;
    const remaining = blessing.active ? ` · ${Math.ceil(blessing.secondsRemaining / 60)}m left` : "";
    row.innerHTML = `<span class="inv-name">${escapeHtml(blessing.name)}<span class="inv-affixes">${escapeHtml(blessing.effect)}${remaining}</span></span>`;
    for (const size of data.sizes) {
      const button = document.createElement("button");
      button.className = "inv-btn";
      button.textContent = `${size.name} · ${size.opalCost}`;
      button.disabled = data.opals < size.opalCost;
      button.addEventListener("click", () => void offeringHandler?.(blessing.type, size.size));
      row.appendChild(button);
    }
    blessingsItemsEl.appendChild(row);
  }
}

function togglePortal(force?: boolean): void {
  const show = force ?? portalEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    loadoutEl.classList.add("hidden");
    blessingsEl.classList.add("hidden");
    closeNpc();
    void portalRefreshHandler?.();
  }
  portalEl.classList.toggle("hidden", !show);
}

function renderPortal(): void {
  const state = portalCache;
  if (!state) { portalBodyEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  if (state.inNiflheim) {
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">In Niflheim<span class="inv-affixes">The portal world is active; clear the boss or return to town.</span></span></div>`;
    portalActionEl.textContent = "Return to town";
  } else if (state.hasPortal) {
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">Niflheim portal ready<span class="inv-affixes">Consumes one portal and starts a run (${state.runsCleared} cleared).</span></span></div>`;
    portalActionEl.textContent = "Enter Niflheim";
  } else {
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">No portal<span class="inv-affixes">Buy a NiflheimPortal from the merchant first.</span></span></div>`;
    portalActionEl.textContent = "Enter Niflheim";
  }
  portalActionEl.disabled = !state.inNiflheim && !state.hasPortal;
}

let loadoutCache: Loadout | null = null;
let loadoutSelection: { active: Set<string>; passive: Set<string> } = { active: new Set(), passive: new Set() };
let loadoutRefreshHandler: (() => Promise<void>) | null = null;
let loadoutSaveHandler: (() => Promise<void>) | null = null;
const poolByName = new Map<string, PowerPoolOption>();

function toggleLoadout(force?: boolean): void {
  const show = force ?? loadoutEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    closeNpc();
    void loadoutRefreshHandler?.();
  }
  loadoutEl.classList.toggle("hidden", !show);
}

function renderLoadout(): void {
  const data = loadoutCache;
  if (!data) { loadoutItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  loadoutPointsEl.textContent =
    `${loadoutSelection.active.size}/${data.maxActive} skills · ${loadoutSelection.passive.size}/${data.maxPassive} masteries`;
  loadoutItemsEl.innerHTML = "";
  const section = (title: string, options: PowerPoolOption[], selected: Set<string>, cap: number): void => {
    const header = document.createElement("div");
    header.className = "power-skill";
    header.textContent = `${title} (${selected.size}/${cap})`;
    loadoutItemsEl.appendChild(header);
    for (const option of options) {
      const chosen = selected.has(option.name);
      const row = document.createElement("div");
      row.className = `inv-row loadout-row${chosen ? " equipped" : ""}`;
      row.innerHTML = `<span class="inv-name">${escapeHtml(prettyPower(option.name))}<span class="inv-affixes">${escapeHtml((option.description ?? "").replace(/\{[0-9]+\}/g, "…"))}</span></span>`;
      const button = document.createElement("button");
      button.className = "inv-btn";
      button.textContent = chosen ? "Remove" : "Equip";
      button.disabled = !chosen && selected.size >= cap;
      button.addEventListener("click", () => {
        if (selected.has(option.name)) selected.delete(option.name);
        else if (selected.size < cap) selected.add(option.name);
        renderLoadout();
      });
      row.appendChild(button);
      loadoutItemsEl.appendChild(row);
    }
  };
  section("Active skills", data.poolActive, loadoutSelection.active, data.maxActive);
  section("Masteries", data.poolPassive, loadoutSelection.passive, data.maxPassive);
}

function prettyPower(name: string): string {
  return name.replace(/([a-z])([A-Z])/g, "$1 $2");
}

const NPc_TABS: Record<string, { label: string; hint: string }> = {
  smelt: { label: "Smelting", hint: "Smelt selected materials into Steel (essence → steel value)." },
  disassemble: { label: "Disassemble", hint: "Disassemble selected equipment into Iron." },
  essence: { label: "Reforge", hint: "Pick a target and source items; consumes Iron and merges affixes." },
  relic: { label: "Bless", hint: "Pick a target and a source relic; blesses the target's affixes." },
  socket: { label: "Socket", hint: "Pick a gem and a target; inserts the gem into a free socket." },
  "add-socket": { label: "Add Socket", hint: "Pick a target; consumes Titansteel to add a socket." },
  trade: { label: "Trade", hint: "Offer items for the selected merchant product." },
  buy: { label: "Buy", hint: "Purchase a product with silver or opals." },
  set: { label: "Set", hint: "Trade an item for a set item of the same slot (set merchant)." },
};

function openNpc(mode: "smith" | "merchant"): void {
  npcMode = mode;
  npcTab = mode === "smith" ? "smelt" : "buy";
  attrsEl.classList.add("hidden");
  npcPicked.clear();
  npcTarget = null;
  npcStatusEl.textContent = "";
  inventoryEl.classList.add("hidden");
  powersEl.classList.add("hidden");
  npcEl.classList.remove("hidden");
  renderNpc();
  if (mode === "merchant" && npcCatalog.length === 0) {
    void api.merchantCatalog().then((rows) => { npcCatalog = rows; renderNpc(); }).catch(() => undefined);
  }
}

function closeNpc(): void {
  npcMode = null;
  npcEl.classList.add("hidden");
}

function renderNpc(): void {
  if (!npcMode) return;
  npcTitleEl.textContent = npcMode === "smith" ? "Blacksmith" : "Merchant";
  const tabs: NpcTab[] = npcMode === "smith"
    ? ["smelt", "disassemble", "essence", "relic", "socket", "add-socket"]
    : ["buy", "trade", "set"];
  if (!tabs.includes(npcTab)) npcTab = tabs[0];
  npcTabsEl.innerHTML = tabs.map((t) =>
    `<button class="npc-tab${t === npcTab ? " active" : ""}" data-tab="${t}">${NPc_TABS[t].label}</button>`).join("");
  for (const button of Array.from(npcTabsEl.querySelectorAll(".npc-tab")))
    button.addEventListener("click", () => { npcTab = (button as HTMLElement).dataset.tab as NpcTab; npcPicked.clear(); npcTarget = null; npcStatusEl.textContent = ""; renderNpc(); });
  npcHintEl.textContent = NPc_TABS[npcTab].hint;

  const items = inventoryCache?.items ?? [];
  npcActionEl.style.display = npcTab === "buy" ? "none" : "";
  if (npcTab === "buy") {
    npcBodyEl.innerHTML = npcCatalog.length === 0
      ? `<div class="inv-empty">Loading merchant stock…</div>`
      : npcCatalog.map((p) => `<div class="npc-row">
        <span class="npc-slot">${p.silverPrice}s · ${p.opalPrice}o</span>
        <span class="inv-name">${escapeHtml(p.name)}</span>
        <button class="inv-btn" data-buy="silver" data-id="${p.itemId}">Buy</button>
        <button class="inv-btn" data-buy="opal" data-id="${p.itemId}">Buy (o)</button></div>`).join("");
    for (const button of Array.from(npcBodyEl.querySelectorAll("[data-buy]"))) {
      const el = button as HTMLButtonElement;
      el.addEventListener("click", () => void buyProduct(el.dataset.id!, el.dataset.buy === "opal"));
    }
    return;
  }
  if (npcTab === "set") {
    npcBodyEl.innerHTML = items.length === 0
      ? `<div class="inv-empty">No items in the bag.</div>`
      : items.map((item) => `<div class="npc-row">
        <span class="inv-name">${escapeHtml(item.name)}</span>
        <span class="inv-stats">+${Math.round(item.offense)} off</span>
        <button class="inv-btn" data-set-trade="${item.id}">Set</button></div>`).join("");
    for (const button of Array.from(npcBodyEl.querySelectorAll("[data-set-trade]"))) {
      const el = button as HTMLButtonElement;
      el.addEventListener("click", () => void setTrade(el.dataset.setTrade!));
    }
    return;
  }
  if (npcTab === "trade") {
    npcBodyEl.innerHTML = npcCatalog.length === 0
      ? `<div class="inv-empty">Loading merchant stock…</div>`
      : npcCatalog.map((p) => `<div class="npc-row" data-trade="${p.itemId}"><span class="npc-slot">${p.silverPrice} silver</span><span class="inv-name">${escapeHtml(p.name)}</span></div>`).join("");
    for (const row of Array.from(npcBodyEl.querySelectorAll("[data-trade]")))
      row.addEventListener("click", () => { npcTarget = (row as HTMLElement).dataset.trade!; renderNpc(); });
    npcActionEl.textContent = "Trade";
    npcActionEl.disabled = npcPicked.size === 0 || !npcTarget;
    return;
  }

  const needTarget = npcTab === "essence" || npcTab === "relic" || npcTab === "socket" || npcTab === "add-socket";
  npcBodyEl.innerHTML = items.length === 0
    ? `<div class="inv-empty">No items in the bag.</div>`
    : items.map((item) => {
      const picking = needTarget && npcTarget === item.id;
      const picked = npcPicked.has(item.id);
      return `<div class="npc-row${picked || picking ? " picked" : ""}" data-item="${item.id}">
        <span class="npc-slot">${picked ? "source" : picking ? "target" : item.equipped ? "equipped" : ""}</span>
        <span class="inv-name">${escapeHtml(item.name)}</span>
        <span class="inv-stats">+${Math.round(item.offense)} off</span></div>`;
    }).join("");
  for (const row of Array.from(npcBodyEl.querySelectorAll("[data-item]")))
    row.addEventListener("click", () => onClickItem((row as HTMLElement).dataset.item!));
  npcActionEl.textContent = NPc_TABS[npcTab].label;
  const ready = needTarget ? Boolean(npcTarget) : npcPicked.size > 0;
  npcActionEl.disabled = !ready;
}

function onClickItem(itemId: string): void {
  if (npcTab === "add-socket") {
    npcTarget = npcTarget === itemId ? null : itemId;
  } else if (npcTab === "essence" || npcTab === "relic" || npcTab === "socket") {
    npcPicked.delete(itemId);
    npcTarget = npcTarget === itemId ? null : itemId;
  } else if (npcPicked.has(itemId)) {
    npcPicked.delete(itemId);
  } else {
    npcPicked.add(itemId);
  }
  renderNpc();
}

async function setTrade(offerItemId: string): Promise<void> {
  if (!npcCharacterId) return;
  npcStatusEl.textContent = "Trading…";
  try {
    const result = await api.setTrade(npcCharacterId, offerItemId);
    inventoryCache = result.inventory;
    npcStatusEl.textContent = result.applied ? "Set item received" : "Could not trade that item";
    renderNpc();
  } catch (error) {
    npcStatusEl.textContent = `Failed: ${error instanceof Error ? error.message : String(error)}`;
  }
}

function itemGlow(name: string): string {
  if (name.startsWith("Unique_")) return " glow-unique";
  if (name.startsWith("Set_")) return " glow-set";
  return "";
}

async function buyProduct(catalogItemId: string, useOpals: boolean): Promise<void> {
  if (!npcCharacterId) return;
  npcStatusEl.textContent = "Buying…";
  try {
    const result = await api.buy(npcCharacterId, catalogItemId, useOpals);
    inventoryCache = result.inventory;
    npcStatusEl.textContent = `Bought · balance ${result.newBalance}`;
    renderNpc();
  } catch (error) {
    npcStatusEl.textContent = `Failed: ${error instanceof Error ? error.message : String(error)}`;
  }
}

function showLoot(loot: LootDrop[]): void {
  for (const drop of loot) {
    const el = document.createElement("div");
    el.className = `loot-item${itemGlow(drop.name)}`;
    el.style.color = RARITY_COLORS[drop.rarity] ?? "#ccc";
    el.textContent = `${RARITY_NAMES[drop.rarity] ?? "?"} ${drop.name}`;
    lootFeedEl.appendChild(el);
    window.setTimeout(() => el.classList.add("fade"), 3500);
    window.setTimeout(() => el.remove(), 4500);
  }
  while (lootFeedEl.childElementCount > 6) lootFeedEl.firstElementChild?.remove();
}

function renderHud(hudState: HudState): void {
  const hpPercent = Math.max(0, Math.min(100, (hudState.health / hudState.maxHealth) * 100));
  (document.getElementById("hud-hp-fill") as HTMLElement).style.width = `${hpPercent}%`;
  (document.getElementById("hud-hp-text") as HTMLElement).textContent = `${hudState.health} / ${hudState.maxHealth}${hudState.shield > 0 ? ` (+${hudState.shield} shield)` : ""}`;
  const manaPercent = Math.max(0, Math.min(100, (hudState.mana / Math.max(1, hudState.maxMana)) * 100));
  (document.getElementById("hud-mana-fill") as HTMLElement).style.width = `${manaPercent}%`;
  (document.getElementById("hud-mana-text") as HTMLElement).textContent = `${hudState.mana} / ${hudState.maxMana}`;
  const span = Math.max(1, hudState.experienceForNextLevel - hudState.experienceForLevel);
  const xpPercent = Math.max(0, Math.min(100, ((hudState.experience - hudState.experienceForLevel) / span) * 100));
  (document.getElementById("hud-xp-fill") as HTMLElement).style.width = `${xpPercent}%`;
  (document.getElementById("hud-xp-text") as HTMLElement).textContent =
    `XP ${Math.round(hudState.experience)} / ${Math.round(hudState.experienceForNextLevel)}`;
  (document.getElementById("hud-level") as HTMLElement).textContent = String(hudState.level);
  (document.getElementById("hud-dungeons") as HTMLElement).textContent = String(hudState.dungeonsCleared);
  (document.getElementById("hud-silver") as HTMLElement).textContent = String(hudState.silver);
  (document.getElementById("hud-opals") as HTMLElement).textContent = String(hudState.opals);
  (document.getElementById("hud-kills") as HTMLElement).textContent = String(hudState.monsterKills);
  (document.getElementById("hud-alive") as HTMLElement).textContent = String(hudState.monstersAlive);
  const boss = document.getElementById("hud-boss") as HTMLElement;
  boss.classList.toggle("hidden", hudState.bossAlive || hudState.bossKillsRemaining <= 0);
  (document.getElementById("hud-boss-count") as HTMLElement).textContent = String(hudState.bossKillsRemaining);
  const skillButtons = ["hud-skill", "hud-skill-2", "hud-skill-3", "hud-skill-4", "hud-skill-5", "hud-skill-6"] as const;
  skillButtons.forEach((id, index) => {
    const button = document.getElementById(id) as HTMLButtonElement;
    const skill = hudState.skills[index];
    button.classList.toggle("hidden", !skill);
    if (!skill) return;
    const ready = skill.ready ?? true;
    const name = skill.name ?? `Skill ${index + 1}`;
    const power = classPowers?.active[index];
    const pooled = poolByName.get(name);
    const description = [power?.descriptionFilled ?? power?.description?.replace(/\{[0-9]+\}/g, "…") ?? pooled?.description?.replace(/\{[0-9]+\}/g, "…") ?? "",
      power?.special ? `Special: ${power.special}` : "",
      power?.chains ? `Chains: ${power.chains}` : "",
      power?.baseChain?.length ? `Class: ${power.baseChain.slice(0, 3).join(" < ")}` : "",
      power?.masteries?.length ? `Masteries: ${power.masteries.length}` : "",
      power?.confidence ? `[${power.confidence}]` : ""].filter(Boolean).join("\n");
    const mana = skill.manaCost ? `  ${Math.round(skill.manaCost)}m` : "";
    if (description) button.title = description;
    button.classList.toggle("disabled", !ready);
    button.textContent = ready ? `${name}${mana}` : `${name} ${Math.ceil(skill.cooldown)}s`;
  });
  const message = document.getElementById("hud-message") as HTMLElement;
  message.textContent = hudState.message;
  message.classList.toggle("show", Boolean(hudState.message));
}

function showHudMessage(text: string): void {
  const message = document.getElementById("hud-message") as HTMLElement;
  message.textContent = text;
  message.classList.add("show");
  window.setTimeout(() => message.classList.remove("show"), 1500);
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (ch) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" })[ch]!);
}

function capitalize(value: string): string {
  return value.length ? value[0].toUpperCase() + value.slice(1) : value;
}

boot().catch((error) => {
  console.error(error);
  setStatus(`Boot failed: ${error instanceof Error ? error.message : String(error)}`, true);
});
