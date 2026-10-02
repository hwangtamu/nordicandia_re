import "./style.css";
import { api, AttributeKey, CharacterSummary, CombatEnvelope, LootDrop, MerchantProduct, newCommandId, NpcResult, SkillMasteryView, Snapshot, WebAttributes, WebInventory } from "./api";
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
    <div id="attrs" class="inventory hidden">
      <div class="inv-head">
        <span>Attributes <small id="attrs-points"></small></span>
        <button id="attrs-close" class="icon-btn">×</button>
      </div>
      <div id="attrs-items" class="inv-items"></div>
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
      <button id="hud-auto" class="skill alt">Auto-move: ON <small>Tab</small></button>
      <button id="hud-bag" class="skill alt">Bag <small>B</small></button>
      <button id="hud-powers" class="skill alt">Skills <small>P</small></button>
      <button id="hud-smith" class="skill alt">Blacksmith <small>K</small></button>
      <button id="hud-merchant" class="skill alt">Merchant <small>M</small></button>
      <button id="hud-attrs" class="skill alt">Attrs <small>C</small></button>
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
  ];
  for (const [id, skillId] of skillButtons)
    document.getElementById(id)!.addEventListener("click", () => void sendCommand("skill", { skillId }));
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
  const keyToSkill: Record<string, number> = { Digit1: 0, Digit2: 1, Digit3: 2, Numpad1: 0, Numpad2: 1, Numpad3: 2 };
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
    } else if (event.code === "Escape") {
      closeNpc();
    }
  });
}

function toggleBag(force?: boolean): void {
  const show = force ?? inventoryEl.classList.contains("hidden");
  if (show) { powersEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); }
  inventoryEl.classList.toggle("hidden", !show);
}

function togglePowers(force?: boolean): void {
  const show = force ?? powersEl.classList.contains("hidden");
  if (show) { inventoryEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); }
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
    row.className = `inv-row${item.equipped ? " equipped" : ""}`;
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

type NpcTab = "smelt" | "disassemble" | "essence" | "relic" | "socket" | "add-socket" | "trade" | "buy";

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
  if (show) { inventoryEl.classList.add("hidden"); powersEl.classList.add("hidden"); closeNpc(); void attrsRefreshHandler?.(); }
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

const NPc_TABS: Record<string, { label: string; hint: string }> = {
  smelt: { label: "Smelting", hint: "Smelt selected materials into Steel (essence → steel value)." },
  disassemble: { label: "Disassemble", hint: "Disassemble selected equipment into Iron." },
  essence: { label: "Reforge", hint: "Pick a target and source items; consumes Iron and merges affixes." },
  relic: { label: "Bless", hint: "Pick a target and a source relic; blesses the target's affixes." },
  socket: { label: "Socket", hint: "Pick a gem and a target; inserts the gem into a free socket." },
  "add-socket": { label: "Add Socket", hint: "Pick a target; consumes Titansteel to add a socket." },
  trade: { label: "Trade", hint: "Offer items for the selected merchant product." },
  buy: { label: "Buy", hint: "Purchase a product with silver or opals." },
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
    : ["buy", "trade"];
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
    el.className = "loot-item";
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
  const skillButtons = ["hud-skill", "hud-skill-2", "hud-skill-3"] as const;
  skillButtons.forEach((id, index) => {
    const button = document.getElementById(id) as HTMLButtonElement;
    const skill = hudState.skills[index];
    const ready = skill?.ready ?? true;
    const name = skill?.name ?? `Skill ${index + 1}`;
    const power = classPowers?.active[index];
    const description = [power?.descriptionFilled ?? power?.description?.replace(/\{[0-9]+\}/g, "…") ?? "",
      power?.special ? `Special: ${power.special}` : "",
      power?.chains ? `Chains: ${power.chains}` : "",
      power?.baseChain?.length ? `Class: ${power.baseChain.slice(0, 3).join(" < ")}` : "",
      power?.masteries?.length ? `Masteries: ${power.masteries.length}` : "",
      power?.confidence ? `[${power.confidence}]` : ""].filter(Boolean).join("\n");
    const mana = skill?.manaCost ? `  ${Math.round(skill.manaCost)}m` : "";
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
