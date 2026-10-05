import "./style.css";
import { api, AttributeKey, BlessingsView, CharacterSummary, CombatEnvelope, Loadout, LootDrop, MerchantProduct, newCommandId, NpcResult, OfflineView, PowerPoolOption, SkillMasteryView, Snapshot, WebAttributes, WebInventory, WebItemDetail, WebPetRoster, WebPortalState, WorldProgress } from "./api";
import { loadContent, loadPowers, ContentManifest, ClassPowers, className, raceIcon } from "./content";
import { HudState, World } from "./game";

const RARITY_NAMES = ["F", "E", "D", "C", "B", "A", "AA", "AAA", "AAAA", "AAAAA", "S", "SS"];
const RARITY_COLORS = ["#b9b9b9", "#8fd18a", "#7fc6e8", "#c9a24a", "#e0894a", "#e05a5a", "#c05ae0", "#5a7fe0", "#e0c05a", "#e05ac0", "#ff5a3c", "#fff3b0"];

type CharacterView = "inventory" | "attrs" | "skills";
function characterTabs(active: CharacterView): string {
  const tab = (view: CharacterView, hud: string, label: string, drawing: string): string =>
    `<button class="character-tab${active === view ? " active" : ""}" data-panel="${view}" data-hud="${hud}" aria-label="${label}" title="${label}"${active === view ? ' aria-current="page"' : ""}><svg viewBox="0 0 32 32" aria-hidden="true">${drawing}</svg></button>`;
  return `<nav class="inv-side character-tabs" aria-label="Character screens">
    ${tab("inventory", "hud-bag", "Equipment and inventory", '<path d="m4 6 7-3 5 3 5-3 7 3-3 7-4-2v17H11V11l-4 2z"/><path d="M12 7c0 5 8 5 8 0"/>')}
    ${tab("attrs", "hud-attrs", "Character attributes", '<path d="M5 22h6l3-4 4 1 4-5-2-4 3-2 4 5-1 9-6 6H8z"/><path d="m11 22 3 3h7"/>')}
    ${tab("skills", "hud-powers", "Skills and masteries", '<path d="m16 2 4.3 9.2 10.1 1.2-7.4 7 2 10-9-4.9-9 4.9 2-10-7.4-7 10.1-1.2z"/>')}
    <button data-hud="hud-merchant" aria-label="Merchant" title="Merchant">🛒</button>
    <button data-hud="hud-portal" aria-label="Portal" title="Portal">⬡</button>
    <button disabled aria-label="Leaderboard unavailable" title="Leaderboard is not implemented yet">▥</button>
    <button disabled aria-label="Achievements unavailable" title="Achievements are not implemented yet">✹</button>
    <button disabled aria-label="Friends unavailable" title="Friends are not implemented yet">♟</button>
  </nav>`;
}

let inventoryItemArtPaths: Record<string, string> = {};
const inventoryItemArtReady = fetch("/assets/items/manifest.json")
  .then((response) => response.ok ? response.json() as Promise<{ items?: Record<string, string> }> : null)
  .then((manifest) => { inventoryItemArtPaths = manifest?.items ?? {}; })
  .catch(() => { inventoryItemArtPaths = {}; });

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
      <div id="hud-packs" class="boss hidden">Packs <span id="hud-packs-count">0</span></div>
      <div class="source">content <span id="hud-content">…</span></div>
    </div>
    <div id="loot-feed" class="loot-feed"></div>
    <div id="inventory" class="inventory inventory-gear hidden">
      <div class="inventory-banner">
        <span class="inventory-title">Inventory</span>
        <small id="inv-count"></small>
        <button id="inv-close" class="inventory-close" aria-label="Close inventory">×</button>
      </div>
      <div class="inv-layout">
        <section class="inv-equipment" aria-label="Equipment">
          <div class="inv-character-head"><strong id="inv-character-name">Character</strong><small id="inv-character-level">Level 1</small></div>
          <div id="equipment-slots" class="equipment-slots"></div>
          <div class="inv-avatar-medallion"><img id="inv-character-avatar" alt="Character race portrait" /></div>
          <div class="inv-character-class"><span id="inv-race-name"></span><span id="inv-class-name"></span></div>
          <div class="inv-character-badges"><img id="inv-race-badge" alt="Race emblem" /><img id="inv-class-badge" alt="Class emblem" /></div>
          <button class="inv-avatar-change" disabled title="Avatar selection is not implemented yet">Change Avatar</button>
        </section>
        <section class="inv-ratings" aria-label="Character ratings">
          <div class="inv-rating offense"><span>Offense</span><strong id="inv-offense">0</strong></div>
          <div class="inv-rating defense"><span>Defense</span><strong id="inv-defense">0</strong></div>
          <div class="inv-rating recovery"><span>Recovery</span><strong id="inv-recovery">0</strong></div>
          <div class="inv-rating-help">?</div>
          <div class="inv-potions"><span>Potions</span><div class="inv-potion-slots"><div></div><div></div><button id="inv-potion-add" title="Visit the merchant for potions">+</button></div></div>
          <button class="inv-trash" disabled title="Discarding items is not implemented yet">▣</button>
        </section>
        <section class="inv-bag" aria-label="Bag">
          <div class="inv-opals"><span id="inv-opals">0</span> Opals</div>
          <div class="inv-toolbar">
            <label>Find <input id="inv-search" type="search" placeholder="Item or affix" /></label>
            <label>Show <select id="inv-filter"><option value="all">All</option><option value="gear">Gear</option><option value="other">Other items</option></select></label>
            <label>Sort <select id="inv-sort"><option value="rarity">Rarity</option><option value="name">Name</option><option value="slot">Slot</option></select></label>
          </div>
          <div id="inv-items" class="inv-items inv-grid" aria-label="Bag items"></div>
          <div class="inv-footer"><button disabled title="Discarding items is not implemented yet">Discard low rarity</button><button disabled title="Item locking is not implemented yet">Lock items</button><button id="inv-sort-button">Sort</button></div>
        </section>
        ${characterTabs("inventory")}
      </div>
      <div id="inv-detail" class="inv-detail" aria-live="polite">Select an item to see its details.</div>
    </div>
    <div id="powers" class="inventory character-panel skills-panel hidden">
      <div class="inventory-banner"><span class="inventory-title">Skills</span><button id="powers-close" class="inventory-close" aria-label="Close skills">×</button></div>
      <div class="character-panel-body skills-body">
        <section class="skills-library" aria-label="Skill allocation">
          <div class="skills-tabs"><button id="skills-active-tab" class="active">Active skills</button><button id="skills-passive-tab">Passive skills</button></div>
          <div class="skills-section-title">Assigned Skills <small id="skills-assigned-count"></small></div>
          <div id="skills-assigned" class="skills-assigned"></div>
          <div class="skills-section-title">Skillbook</div>
          <div id="skills-loadout-items" class="skills-loadout-items"></div>
          <div class="skills-foot"><button id="skills-save" class="npc-action">Save skills</button><span id="skills-status" class="npc-status"></span></div>
        </section>
        <section class="skills-mastery" aria-label="Skill advancement">
          <div class="skills-section-title"><span id="skills-detail-title">Skill Mastery</span> <small id="powers-points"></small></div>
          <div id="powers-items" class="inv-items"></div>
        </section>
        ${characterTabs("skills")}
      </div>
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
    <div id="attrs" class="inventory character-panel attrs-panel hidden">
      <div class="inventory-banner"><span class="inventory-title">Attributes</span><button id="attrs-close" class="inventory-close" aria-label="Close attributes">×</button></div>
      <div class="character-panel-body attrs-body">
        <section class="attrs-allocation" aria-label="Allocate attributes">
          <div class="attrs-available">Available attribute points: <strong id="attrs-points">0</strong></div>
          <div id="attrs-items" class="inv-items"></div>
        </section>
        <section class="attrs-details" aria-label="Character ratings">
          <div class="attrs-tabs"><button data-attrs-tab="offense" class="active">Offense</button><button data-attrs-tab="defense">Defense</button><button data-attrs-tab="attributes">Attributes</button><button data-attrs-tab="misc">Misc</button></div>
          <div id="attrs-detail-items" class="attrs-detail-items"></div>
        </section>
        ${characterTabs("attrs")}
      </div>
    </div>
    <div id="pets" class="inventory hidden">
      <div class="inv-head">
        <span id="pets-title">Pets</span>
        <button id="pets-close" class="icon-btn">×</button>
      </div>
      <div id="pets-tabs" class="npc-tabs"></div>
      <div id="pets-hint" class="npc-hint"></div>
      <div id="pets-items" class="inv-items"></div>
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
        <button id="portal-chest" class="npc-action" hidden>Open chest</button>
        <button id="portal-action" class="npc-action">Enter Niflheim</button>
        <span id="portal-status" class="npc-status"></span>
      </div>
    </div>
    <div id="worlds" class="inventory hidden">
      <div class="inv-head">
        <span>Worlds <small id="worlds-tier"></small></span>
        <button id="worlds-close" class="icon-btn">×</button>
      </div>
      <div id="worlds-items" class="inv-items"></div>
      <div class="npc-foot"><span id="worlds-status" class="npc-status"></span></div>
    </div>
    <div id="npc" class="npc-panel hidden">
      <div class="inventory-banner">
        <span id="npc-title" class="inventory-title">Blacksmith</span>
        <button id="npc-close" class="inventory-close" aria-label="Close NPC window">×</button>
      </div>
      <div class="npc-layout">
        <section class="npc-workbench" aria-label="NPC workbench">
          <div id="npc-tabs" class="npc-tabs"></div>
          <div id="npc-hint" class="npc-hint"></div>
          <div id="npc-stage" class="npc-stage"></div>
          <div class="npc-foot">
            <button id="npc-action" class="npc-action">Craft</button>
            <span id="npc-status" class="npc-status" role="status"></span>
          </div>
        </section>
        <section class="npc-stock" aria-label="Inventory and stock">
          <div class="npc-stock-head"><span id="npc-stock-title">Inventory</span><span class="npc-currencies"><span id="npc-silver">0 Silver</span> · <span id="npc-opals">0 Opals</span></span></div>
          <div id="npc-body" class="npc-body"></div>
          <div class="npc-stock-foot"><span id="npc-stock-count"></span><button id="npc-sort" type="button">Sort</button></div>
        </section>
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
      <button id="hud-worlds" class="skill alt">Worlds <small>W</small></button>
      <button id="hud-town" class="skill alt">Town <small>T</small></button>
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
const equipmentSlotsEl = document.getElementById("equipment-slots") as HTMLDivElement;
const invDetailEl = document.getElementById("inv-detail") as HTMLDivElement;
const invCountEl = document.getElementById("inv-count") as HTMLElement;
const invSearchEl = document.getElementById("inv-search") as HTMLInputElement;
const invFilterEl = document.getElementById("inv-filter") as HTMLSelectElement;
const invSortEl = document.getElementById("inv-sort") as HTMLSelectElement;
const invCharacterNameEl = document.getElementById("inv-character-name") as HTMLElement;
const invCharacterLevelEl = document.getElementById("inv-character-level") as HTMLElement;
const invCharacterAvatarEl = document.getElementById("inv-character-avatar") as HTMLImageElement;
const invRaceNameEl = document.getElementById("inv-race-name") as HTMLElement;
const invClassNameEl = document.getElementById("inv-class-name") as HTMLElement;
const invRaceBadgeEl = document.getElementById("inv-race-badge") as HTMLImageElement;
const invClassBadgeEl = document.getElementById("inv-class-badge") as HTMLImageElement;
const invOpalsEl = document.getElementById("inv-opals") as HTMLElement;
const powersEl = document.getElementById("powers") as HTMLDivElement;
const powersItemsEl = document.getElementById("powers-items") as HTMLDivElement;
const powersPointsEl = document.getElementById("powers-points") as HTMLElement;
const skillsAssignedEl = document.getElementById("skills-assigned") as HTMLDivElement;
const skillsAssignedCountEl = document.getElementById("skills-assigned-count") as HTMLElement;
const skillsLoadoutItemsEl = document.getElementById("skills-loadout-items") as HTMLDivElement;
const skillsStatusEl = document.getElementById("skills-status") as HTMLElement;
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
  invCharacterNameEl.textContent = selected.displayName;
  invCharacterAvatarEl.src = raceIcon(content, selected.race);
  invRaceNameEl.textContent = content.races.find((race) => race.integerId === selected.race)?.name ?? "Adventurer";
  invClassNameEl.textContent = className(content, selected.class);
  invRaceBadgeEl.src = raceIcon(content, selected.race);
  const classBadgeName: Record<number, string> = {
    0: "warrior", 1: "paladin", 2: "assassin", 3: "barbarian",
    4: "hunter", 5: "mage", 6: "necro", 7: "priest",
  };
  invClassBadgeEl.src = `/assets/inventory-ui/Badge_${classBadgeName[selected.class] ?? "warrior"}.png`;

  const characterId = selected.characterId;
  petRefreshHandler = async (): Promise<void> => {
    try { petRoster = await api.pets(characterId); renderPets(); }
    catch (error) { petsItemsEl.innerHTML = `<div class="inv-empty">Pet roster unavailable: ${escapeHtml(error instanceof Error ? error.message : String(error))}</div>`; }
  };
  petSelectHandler = async (definitionIntegerId: number, combat: boolean): Promise<void> => {
    try {
      petRoster = combat
        ? await api.selectCombatPet(characterId, definitionIntegerId)
        : await api.selectPet(characterId, definitionIntegerId);
      renderPets();
    } catch (error) {
      petsHintEl.textContent = `Could not select pet: ${error instanceof Error ? error.message : String(error)}`;
    }
  };
  petUnlockHandler = async (definitionIntegerId: number, combat: boolean): Promise<void> => {
    try {
      petRoster = combat
        ? await api.unlockCombatPet(characterId, definitionIntegerId)
        : await api.unlockPet(characterId, definitionIntegerId);
      renderPets();
    } catch (error) {
      petsHintEl.textContent = `Could not unlock pet: ${error instanceof Error ? error.message : String(error)}`;
    }
  };
  classPowers = powers[String(selected.class)] ?? null;
  (document.getElementById("hud-passive") as HTMLElement).textContent = classPowers
    ? `Passive · ${classPowers.passive.name}`
    : "";
  let equipmentCommandPending = false;
  const sendCommand = async (
    type: "move" | "skill" | "equip" | "unequip" | "mastery",
    options: { x?: number; z?: number; itemId?: string; skillId?: number; masteryId?: number } = {},
  ): Promise<void> => {
    if (travelInProgress && (type === "move" || type === "skill")) return;
    const equipmentCommand = type === "equip" || type === "unequip";
    if (equipmentCommand && equipmentCommandPending) return;
    if (equipmentCommand) equipmentCommandPending = true;
    const expectedVersion = world?.currentVersion() ?? 0;
    try {
      const result = await api.command(characterId, newCommandId(), expectedVersion, type, options);
      world?.pushState(result.state);
      if (!result.applied && result.reason === "cooldown") showHudMessage("Skill on cooldown");
      if (!result.applied && result.reason === "no_target") showHudMessage("No target in range");
      if (!result.applied && result.reason === "no_mana") showHudMessage("Not enough mana");
      if (!result.applied && result.reason === "stale_command") showHudMessage("Resyncing…");
      if (equipmentCommand && !result.applied && result.reason !== "stale_command") {
        const message: Record<string, string> = {
          no_item: "Item no longer in your bag", not_equippable: "This item is not equipment",
          not_in_inventory: "Move this item to the bag first", not_equipped: "Item is not equipped",
          level_requirement: "Level requirement not met",
        };
        showHudMessage(message[result.reason] ?? `Equipment action failed: ${result.reason}`);
      }
      if (equipmentCommand) await refreshInventory();
    } catch (error) {
      console.warn("command failed", error);
      if (equipmentCommand) showHudMessage("Equipment change failed; please retry");
    } finally {
      if (equipmentCommand) equipmentCommandPending = false;
    }
  };

  const initial: CombatEnvelope = await api.state(characterId);
  npcCharacterId = characterId;
  const refreshInventory = async (): Promise<void> => {
    try {
      await inventoryItemArtReady;
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
    onTownNpc: (npc) => {
      switch (npc) {
        case "blacksmith": openNpc("smith"); break;
        case "disassembler": openNpc("smith", "disassemble"); break;
        case "merchant": openNpc("merchant"); break;
        case "set-merchant": openNpc("merchant", "set"); break;
        case "offering": toggleBlessings(); break;
        case "petkeeper": togglePets(false); break;
        case "combat-petkeeper": togglePets(true); break;
        case "portal": togglePortal(); break;
        case "town-portal": void townActionHandler?.(); break;
        case "worlds": toggleWorlds(); break;
        case "bag": toggleBag(); break;
      }
    },
    onNiflheimExit: () => { void (async () => {
      await portalRefreshHandler?.();
      if (portalCache?.exitReady) await portalActionHandler?.();
    })(); },
    onNiflheimChest: () => { void (async () => {
      await portalRefreshHandler?.();
      if (portalCache?.exitReady) portalChestEl.click();
    })(); },
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
    skillsStatusEl.textContent = "Saving…";
    try {
      const result = await api.setLoadout(characterId, [...loadoutSelection.active], [...loadoutSelection.passive]);
      loadoutStatusEl.textContent = result.applied ? "Saved" : `Rejected: ${result.reason}`;
      skillsStatusEl.textContent = loadoutStatusEl.textContent;
      if (result.applied) {
        await refreshPowers();
        await loadoutRefreshHandler?.();
      }
    } catch (error) {
      loadoutStatusEl.textContent = `Failed: ${error instanceof Error ? error.message : String(error)}`;
      skillsStatusEl.textContent = loadoutStatusEl.textContent;
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
  document.getElementById("inv-sort-button")!.addEventListener("click", () => {
    const next = invSortEl.value === "rarity" ? "name" : invSortEl.value === "name" ? "slot" : "rarity";
    invSortEl.value = next;
    if (inventoryCache && inventoryToggleHandler) renderInventory(inventoryCache, inventoryToggleHandler);
  });
  document.getElementById("inv-potion-add")!.addEventListener("click", () => {
    toggleBag(false);
    openNpc("merchant");
  });
  document.querySelectorAll<HTMLButtonElement>(".character-tabs [data-panel]").forEach((shortcut) => {
    shortcut.addEventListener("click", () => {
      const panel = shortcut.dataset.panel;
      if (panel === "inventory") toggleBag(true);
      else if (panel === "attrs") toggleAttrs(true);
      else if (panel === "skills") togglePowers(true);
    });
  });
  document.querySelectorAll<HTMLButtonElement>(".character-tabs [data-hud]:not([data-panel])").forEach((shortcut) => {
    shortcut.addEventListener("click", () => {
      toggleBag(false); toggleAttrs(false); togglePowers(false);
      document.getElementById(shortcut.dataset.hud ?? "")?.click();
    });
  });
  document.getElementById("skills-save")!.addEventListener("click", () => void loadoutSaveHandler?.());
  document.getElementById("skills-active-tab")!.addEventListener("click", () => { skillsTab = "active"; renderLoadout(); });
  document.getElementById("skills-passive-tab")!.addEventListener("click", () => { skillsTab = "passive"; renderLoadout(); });
  document.querySelectorAll<HTMLButtonElement>(".attrs-tabs [data-attrs-tab]").forEach((tab) =>
    tab.addEventListener("click", () => { attrsTab = tab.dataset.attrsTab as AttrsTab; renderAttrs(); }));
  document.getElementById("hud-powers")!.addEventListener("click", () => togglePowers());
  document.getElementById("powers-close")!.addEventListener("click", () => togglePowers(false));
  document.getElementById("hud-smith")!.addEventListener("click", () => openNpc("smith"));
  document.getElementById("hud-merchant")!.addEventListener("click", () => openNpc("merchant"));
  document.getElementById("npc-close")!.addEventListener("click", () => closeNpc());
  npcActionEl.addEventListener("click", () => void npcActionHandler?.());
  document.getElementById("npc-sort")!.addEventListener("click", () => { npcSortBy = npcSortBy === "rarity" ? "name" : "rarity"; renderNpc(); });
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
  portalChestEl.addEventListener("click", async () => {
    if (travelInProgress || !portalCache?.exitReady || portalCache.chestSize === 0 || portalCache.chestOpened) return;
    travelInProgress = true;
    renderPortal();
    try {
      const result = await api.openNiflheimChest(characterId);
      if (result.applied) world?.pushState(result.state);
      portalCache = await api.portal(characterId);
      renderPortal();
      await refreshInventory();
    } catch (error) {
      showHudMessage(`Chest failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      travelInProgress = false;
      renderPortal();
    }
  });
  portalRefreshHandler = async (): Promise<void> => {
    try { portalCache = await api.portal(characterId); renderPortal(); }
    catch (error) { console.warn("portal failed", error); }
  };
  portalActionHandler = async (): Promise<void> => {
    if (travelInProgress) return;
    travelInProgress = true;
    portalActionEl.disabled = true;
    try {
      if (portalCache?.inNiflheim) {
        world?.pushState((await api.returnPortal(characterId)).state);
      } else if (portalCache?.hasPortal) {
        world?.pushState((await api.enterPortal(characterId, portalCache.portalItemId)).state);
      }
      portalCache = await api.portal(characterId);
      renderPortal();
      await refreshInventory();
    } catch (error) {
      showHudMessage(`Portal travel failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      travelInProgress = false;
      renderPortal();
    }
  };

  document.getElementById("hud-worlds")!.addEventListener("click", () => toggleWorlds());
  document.getElementById("worlds-close")!.addEventListener("click", () => toggleWorlds(false));
  document.getElementById("pets-close")!.addEventListener("click", () => togglePets(undefined, false));
  document.getElementById("hud-town")!.addEventListener("click", () => void townActionHandler?.());
  townActionHandler = async (): Promise<void> => {
    if (travelInProgress) return;
    travelInProgress = true;
    const townButton = document.getElementById("hud-town") as HTMLButtonElement;
    townButton.disabled = true;
    try {
      inventoryEl.classList.add("hidden");
      powersEl.classList.add("hidden");
      attrsEl.classList.add("hidden");
      loadoutEl.classList.add("hidden");
      blessingsEl.classList.add("hidden");
      portalEl.classList.add("hidden");
      worldsEl.classList.add("hidden");
      petsEl.classList.add("hidden");
      closeNpc();
      const current = await api.town(characterId);
      const result = current.inTown ? await api.leaveTown(characterId) : await api.enterTown(characterId);
      world?.pushState(result.state);
      portalCache = null;
      setWorldStatus(current.inTown ? "Returned to the selected world" : "Entered the town hub");
    } catch (error) {
      showHudMessage(`Town travel failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      travelInProgress = false;
      townButton.disabled = false;
    }
  };
  worldsRefreshHandler = async (): Promise<void> => {
    try { worldsCache = await api.worlds(characterId); renderWorlds(); }
    catch (error) { console.warn("world progress failed", error); }
  };
  worldSelectHandler = async (tier: number, waypoint: number): Promise<void> => {
    if (travelInProgress) return;
    travelInProgress = true;
    try {
      const result = await api.selectWorld(characterId, tier, waypoint);
      if (!result.applied) {
        setWorldStatus(`Cannot enter world: ${result.reason}`);
        return;
      }
      world?.pushState(result.state);
      worldsCache = await api.worlds(characterId);
      renderWorlds();
    } catch (error) {
      setWorldStatus(`World change failed: ${error instanceof Error ? error.message : String(error)}`);
    } finally {
      travelInProgress = false;
    }
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
    } else if (event.code === "KeyW") {
      event.preventDefault();
      toggleWorlds();
    } else if (event.code === "KeyT") {
      event.preventDefault();
      if (!event.repeat) void townActionHandler?.();
    } else if (event.code === "KeyL") {
      event.preventDefault();
      toggleLoadout();
    } else if (event.code === "Escape") {
      closeNpc();
      petsEl.classList.add("hidden");
    }
  });
}

function toggleBag(force?: boolean): void {
  const show = force ?? inventoryEl.classList.contains("hidden");
  if (show) { powersEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); portalEl.classList.add("hidden"); worldsEl.classList.add("hidden"); petsEl.classList.add("hidden"); }
  inventoryEl.classList.toggle("hidden", !show);
}

function togglePowers(force?: boolean): void {
  const show = force ?? powersEl.classList.contains("hidden");
  if (show) { inventoryEl.classList.add("hidden"); closeNpc(); attrsEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); portalEl.classList.add("hidden"); worldsEl.classList.add("hidden"); petsEl.classList.add("hidden"); void loadoutRefreshHandler?.(); }
  powersEl.classList.toggle("hidden", !show);
}

let masteryRows: SkillMasteryView[] = [];
let masteryAllocate: ((masteryId: number) => void) | null = null;
let selectedMasterySkill: string | null = null;

function renderPowers(rows: SkillMasteryView[], onAllocate: (masteryId: number) => void): void {
  masteryRows = rows;
  masteryAllocate = onAllocate;
  const spent = rows.reduce((sum, r) => sum + r.rank, 0);
  const total = rows.reduce((sum, r) => sum + r.maxPoints, 0);
  (document.getElementById("skills-detail-title") as HTMLElement).textContent = skillsTab === "passive" ? "Passive Skill" : "Skill Mastery";
  powersPointsEl.textContent = skillsTab === "passive" ? "" : `${spent} / ${total} ranks`;
  powersItemsEl.innerHTML = "";
  if (skillsTab === "passive") {
    const pool = loadoutCache?.poolPassive ?? [];
    const selected = pool.find((p) => p.name === selectedMasterySkill) ?? pool[0];
    if (selected) selectedMasterySkill = selected.name;
    const icon = selected ? powerIconPath(selected.icon) : null;
    powersItemsEl.innerHTML = `<div class="passive-detail">
      <div class="passive-detail-title">${icon ? `<img src="${icon}" alt="" />` : ""}<strong>${selected ? escapeHtml(prettyPower(selected.name)) : "Passive skills"}</strong></div>
      <p>${selected ? escapeHtml((selected.description ?? "").replace(/\{[0-9]+\}/g, "…")) : "Select a passive skill from the skillbook."}</p>
      <div class="passive-detail-status">${selected && loadoutSelection.passive.has(selected.name) ? "Assigned to a passive slot" : "Select Assign, then Save skills to equip this passive."}</div>
    </div>`;
    return;
  }
  const skill = rows.find((row) => row.skillName === selectedMasterySkill) ?? rows[0];
  if (!skill) { powersItemsEl.innerHTML = `<div class="inv-empty">No active skill masteries available.</div>`; return; }
  selectedMasterySkill = skill.skillName;
  const chooser = document.createElement("div");
  chooser.className = "mastery-skill-chooser";
  for (const row of rows) {
    const button = document.createElement("button");
    button.className = row.skillName === skill.skillName ? "active" : "";
    button.textContent = prettyPower(row.skillName);
    button.addEventListener("click", () => { selectedMasterySkill = row.skillName; renderPowers(masteryRows, masteryAllocate!); });
    chooser.appendChild(button);
  }
  powersItemsEl.appendChild(chooser);
  const heading = document.createElement("div");
  heading.className = "mastery-tree-heading";
  heading.textContent = `${prettyPower(skill.skillName)} Mastery · ${skill.rank}/${skill.maxPoints}`;
  powersItemsEl.appendChild(heading);
  if (!skill.masteries.length) { powersItemsEl.insertAdjacentHTML("beforeend", `<div class="inv-empty">No mastery nodes for this skill.</div>`); return; }
  const byId = new Map(skill.masteries.map((m) => [m.integerId, m]));
  const depths = new Map<number, number>();
  const visiting = new Set<number>();
  const depth = (id: number): number => {
    if (depths.has(id)) return depths.get(id)!;
    if (visiting.has(id)) return 0;
    visiting.add(id);
    const mastery = byId.get(id);
    const value = mastery ? Math.max(0, ...(mastery.dependencies ?? []).filter((dep) => byId.has(dep)).map((dep) => depth(dep) + 1)) : 0;
    visiting.delete(id);
    depths.set(id, value);
    return value;
  };
  for (const mastery of skill.masteries) depth(mastery.integerId);
  const columns = Math.max(...depths.values()) + 1;
  const layers = Array.from({ length: columns }, (_, col) => skill.masteries.filter((m) => depths.get(m.integerId) === col));
  const height = Math.max(290, ...layers.map((layer) => layer.length * 88 + 45));
  const canvas = document.createElement("div");
  canvas.className = "mastery-tree-canvas";
  canvas.style.height = `${height}px`;
  const position = new Map<number, { x: number; y: number }>();
  for (let col = 0; col < layers.length; col++) {
    layers[col].forEach((mastery, index) => position.set(mastery.integerId, {
      x: ((col + 0.5) / columns) * 100,
      y: ((index + 0.5) / layers[col].length) * height,
    }));
  }
  const wires = document.createElementNS("http://www.w3.org/2000/svg", "svg");
  wires.setAttribute("class", "mastery-tree-wires");
  wires.setAttribute("viewBox", `0 0 100 ${height}`);
  wires.setAttribute("preserveAspectRatio", "none");
  for (const mastery of skill.masteries) {
    const to = position.get(mastery.integerId)!;
    for (const depId of mastery.dependencies ?? []) {
      const from = position.get(depId);
      if (!from) continue;
      const line = document.createElementNS("http://www.w3.org/2000/svg", "line");
      line.setAttribute("x1", String(from.x)); line.setAttribute("y1", String(from.y));
      line.setAttribute("x2", String(to.x)); line.setAttribute("y2", String(to.y));
      line.setAttribute("class", byId.get(depId)!.rank > 0 ? "unlocked" : "locked");
      wires.appendChild(line);
    }
  }
  canvas.appendChild(wires);
  for (const mastery of skill.masteries) {
    const pos = position.get(mastery.integerId)!;
    const prereqMet = (mastery.dependencies ?? []).every((dep) => (byId.get(dep)?.rank ?? 0) > 0);
    const node = document.createElement("button");
    node.className = `mastery-node${mastery.rank ? " learned" : ""}${prereqMet ? "" : " locked"}`;
    node.style.left = `${pos.x}%`; node.style.top = `${pos.y}px`;
    node.disabled = mastery.rank >= mastery.maxPoints || !prereqMet;
    const fullLabel = prettyMastery(mastery.name);
    const skillLabel = prettyPower(skill.skillName);
    const shortLabel = fullLabel.startsWith(skillLabel) ? fullLabel.slice(skillLabel.length).trim() || fullLabel : fullLabel;
    node.setAttribute("aria-label", `${fullLabel}, rank ${mastery.rank} of ${mastery.maxPoints}${prereqMet ? "" : ", prerequisite required"}`);
    const specs = mastery.specs.map((s) => `${s.attributeName}: ${s.value >= 0 ? "+" : ""}${s.value}`).join("\n");
    const deps = (mastery.dependencies ?? []).map((id) => prettyMastery(byId.get(id)?.name ?? `#${id}`)).join(", ");
    node.title = `${prettyMastery(mastery.name)} (${mastery.rank}/${mastery.maxPoints})\n${specs}${deps ? `\nRequires: ${deps}` : ""}`;
    node.innerHTML = `<span class="mastery-node-gem">✦</span><span class="mastery-node-name">${escapeHtml(shortLabel)}</span><span class="mastery-node-rank">${mastery.rank}/${mastery.maxPoints}</span>`;
    node.addEventListener("click", () => onAllocate(mastery.integerId));
    canvas.appendChild(node);
  }
  powersItemsEl.appendChild(canvas);
}

function prettyMastery(name: string): string {
  return name.replace(/^Mastery/, "").replace(/([a-z])([A-Z])/g, "$1 $2");
}

const EQUIPMENT_SLOT_NAMES = [
  "Head", "Neck", "Shoulders", "Chest", "Back", "Wrists", "Gloves",
  "Waist", "Legs", "Boots", "Right ring", "Left ring", "Main hand", "Off hand",
];
const EQUIPMENT_SLOT_GRID: [number, number][] = [
  [1, 1], [1, 2], [1, 3], [1, 4], [1, 5], [1, 6],
  [4, 1], [4, 2], [4, 3], [4, 4], [4, 5], [4, 6], [2, 7], [3, 7],
];
let selectedInventoryItemId: string | null = null;
let inventoryToggleHandler: ((itemId: string, equipped: boolean) => void) | null = null;

function inventoryActionLabel(item: WebItemDetail): string {
  if (item.equipped) return "Unequip";
  if (item.canEquip) return "Equip";
  if (item.equipReason === "level_requirement") return `Requires Lv ${item.requiredLevel}`;
  if (item.equipReason === "not_in_inventory") return "Unavailable";
  return "Not equipment";
}

function inventoryItemStats(item: WebItemDetail): string {
  return `OFF ${Math.round(item.offense)} · DEF ${Math.round(item.defense)} · REC ${Math.round(item.recovery)}`;
}

function createInventoryItemIcon(item: WebItemDetail): HTMLSpanElement {
  const icon = document.createElement("span");
  icon.className = "item-art";
  icon.style.setProperty("--item-rarity", RARITY_COLORS[item.rarity] ?? "#ccc");
  icon.title = item.name;
  const imagePath = inventoryItemArtPaths[String(item.definitionIntegerId)];
  const image = document.createElement("img");
  image.alt = "";
  image.loading = "lazy";
  image.addEventListener("error", () => {
    icon.classList.add("missing");
    image.remove();
  }, { once: true });
  const fallback = document.createElement("span");
  fallback.className = "item-art-fallback";
  fallback.textContent = item.name.trim().charAt(0).toLocaleUpperCase() || "?";
  if (imagePath) image.src = imagePath;
  else icon.classList.add("missing");
  icon.append(image, fallback);
  return icon;
}

function renderInventory(inventory: WebInventory, onToggle: (itemId: string, equipped: boolean) => void): void {
  inventoryToggleHandler = onToggle;
  (document.getElementById("hud-stats") as HTMLElement).textContent =
    `OFF ${Math.round(inventory.offense)} · DEF ${Math.round(inventory.defense)} · REC ${Math.round(inventory.recovery)}`;
  const equipped = inventory.items.filter((item) => item.equipped);
  const bag = inventory.items.filter((item) => !item.equipped);
  invCountEl.textContent = `${bag.length} items`;
  invCharacterLevelEl.textContent = `Level ${inventory.characterLevel}`;
  invOpalsEl.textContent = (document.getElementById("hud-opals") as HTMLElement).textContent ?? "0";
  for (const [id, value] of [["inv-offense", inventory.offense], ["inv-defense", inventory.defense], ["inv-recovery", inventory.recovery]] as const)
    (document.getElementById(id) as HTMLElement).textContent = new Intl.NumberFormat("en", { notation: "compact", maximumFractionDigits: 2 }).format(value);
  if (selectedInventoryItemId && !inventory.items.some((item) => item.id === selectedInventoryItemId))
    selectedInventoryItemId = null;

  equipmentSlotsEl.innerHTML = "";
  EQUIPMENT_SLOT_NAMES.forEach((name, slot) => {
    const item = equipped.find((candidate) => candidate.slot === slot);
    const cell = document.createElement("div");
    cell.className = `equip-slot${item ? " filled" : ""}${item?.id === selectedInventoryItemId ? " selected" : ""}`;
    if (item) cell.style.setProperty("--item-rarity", RARITY_COLORS[item.rarity] ?? "#ad873b");
    cell.dataset.slot = String(slot);
    cell.style.gridColumn = String(EQUIPMENT_SLOT_GRID[slot][0]);
    cell.style.gridRow = String(EQUIPMENT_SLOT_GRID[slot][1]);
    cell.tabIndex = 0;
    cell.innerHTML = `<span class="equip-slot-name">${name}</span><span class="equip-slot-item">${item ? escapeHtml(item.name) : "Empty"}</span>`;
    if (item) {
      cell.prepend(createInventoryItemIcon(item));
      cell.dataset.itemId = item.id;
      cell.draggable = true;
      cell.addEventListener("dragstart", (event) => event.dataTransfer?.setData("text/plain", item.id));
      cell.addEventListener("click", () => selectInventoryItem(item.id));
      cell.addEventListener("keydown", (event) => { if (event.key === "Enter") selectInventoryItem(item.id); });
      const remove = document.createElement("button");
      remove.className = "equip-remove";
      remove.textContent = "×";
      remove.title = `Unequip ${item.name}`;
      remove.setAttribute("aria-label", remove.title);
      remove.addEventListener("click", (event) => { event.stopPropagation(); onToggle(item.id, true); });
      cell.appendChild(remove);
    }
    cell.addEventListener("dragover", (event) => event.preventDefault());
    cell.addEventListener("drop", (event) => {
      event.preventDefault();
      const dragged = inventory.items.find((candidate) => candidate.id === event.dataTransfer?.getData("text/plain"));
      if (!dragged || dragged.equipped) return;
      if (!dragged.canEquip || dragged.equipSlot !== slot) {
        showHudMessage(dragged.equipReason === "level_requirement"
          ? `Requires level ${dragged.requiredLevel}` : `Cannot equip in ${name}`);
        return;
      }
      onToggle(dragged.id, false);
    });
    equipmentSlotsEl.appendChild(cell);
  });

  const query = invSearchEl.value.trim().toLocaleLowerCase();
  const filtered = bag.filter((item) => {
    if (invFilterEl.value === "gear" && item.equipSlot < 0) return false;
    if (invFilterEl.value === "other" && item.equipSlot >= 0) return false;
    return !query || `${item.name} ${item.affixes.join(" ")}`.toLocaleLowerCase().includes(query);
  });
  filtered.sort((a, b) => {
    switch (invSortEl.value) {
      case "name": return a.name.localeCompare(b.name) || b.rarity - a.rarity;
      case "slot": return a.equipSlot - b.equipSlot || b.rarity - a.rarity;
      default: return b.rarity - a.rarity || a.name.localeCompare(b.name);
    }
  });
  invItemsEl.innerHTML = "";
  if (!filtered.length) invItemsEl.innerHTML = `<div class="inv-empty">${bag.length ? "No matching items." : "Your bag is empty."}</div>`;
  for (const item of filtered) {
    const card = document.createElement("div");
    card.className = `inv-row inv-card${item.id === selectedInventoryItemId ? " selected" : ""}${itemGlow(item.name)}`;
    card.style.setProperty("--item-rarity", RARITY_COLORS[item.rarity] ?? "#4e4641");
    card.dataset.itemId = item.id;
    card.title = `${item.name} · ${inventoryItemStats(item)}`;
    card.setAttribute("aria-label", `${item.name}, ${RARITY_NAMES[item.rarity] ?? "item"}`);
    card.tabIndex = 0;
    card.draggable = item.canEquip;
    card.innerHTML = `
      <span class="inv-card-rarity" style="color:${RARITY_COLORS[item.rarity] ?? "#ccc"}">${RARITY_NAMES[item.rarity] ?? "?"}</span>
      <span class="inv-card-name">${escapeHtml(item.name)}</span>
      <span class="inv-card-meta">${item.equipSlot >= 0 ? EQUIPMENT_SLOT_NAMES[item.equipSlot] ?? "Gear" : "Other"}${item.stack > 1 ? ` · ×${item.stack}` : ""}</span>
    `;
    card.prepend(createInventoryItemIcon(item));
    if (item.stack > 1) {
      const stack = document.createElement("span");
      stack.className = "inv-stack";
      stack.textContent = String(item.stack);
      card.appendChild(stack);
    }
    card.addEventListener("click", () => selectInventoryItem(item.id));
    card.addEventListener("keydown", (event) => { if (event.key === "Enter") selectInventoryItem(item.id); });
    card.addEventListener("dblclick", () => { if (item.canEquip) onToggle(item.id, false); });
    card.addEventListener("dragstart", (event) => event.dataTransfer?.setData("text/plain", item.id));
    const button = document.createElement("button");
    button.className = "inv-btn";
    button.textContent = inventoryActionLabel(item);
    button.disabled = !item.canEquip;
    button.title = item.equipReason === "level_requirement" ? `Current level ${inventory.characterLevel}; requires ${item.requiredLevel}` : "";
    button.addEventListener("click", (event) => { event.stopPropagation(); onToggle(item.id, false); });
    card.appendChild(button);
    invItemsEl.appendChild(card);
  }
  for (let index = filtered.length; index < Math.max(49, Math.ceil(filtered.length / 7) * 7); index++) {
    const emptyCell = document.createElement("span");
    emptyCell.className = "inv-grid-empty";
    emptyCell.setAttribute("aria-label", "Empty bag slot");
    emptyCell.addEventListener("click", () => selectInventoryItem(null));
    invItemsEl.appendChild(emptyCell);
  }
  renderInventoryDetail(inventory);
}

function selectInventoryItem(itemId: string | null): void {
  selectedInventoryItemId = itemId;
  if (inventoryCache && inventoryToggleHandler) renderInventory(inventoryCache, inventoryToggleHandler);
}

function renderInventoryDetail(inventory: WebInventory): void {
  const item = inventory.items.find((candidate) => candidate.id === selectedInventoryItemId);
  if (!item) { invDetailEl.classList.add("hidden"); invDetailEl.textContent = ""; return; }
  invDetailEl.classList.remove("hidden");
  const current = !item.equipped && item.equipSlot >= 0
    ? inventory.items.find((candidate) => candidate.equipped && candidate.slot === item.equipSlot) : undefined;
  const comparison = current ? `<div class="inv-compare">Compared with ${escapeHtml(current.name)}: ` +
    `OFF ${Math.round(item.offense - current.offense).toLocaleString()} · ` +
    `DEF ${Math.round(item.defense - current.defense).toLocaleString()} · ` +
    `REC ${Math.round(item.recovery - current.recovery).toLocaleString()} (item bonuses)</div>` : "";
  invDetailEl.innerHTML = `<strong>${escapeHtml(item.name)}</strong><span>${item.equipSlot >= 0 ? EQUIPMENT_SLOT_NAMES[item.equipSlot] ?? "Gear" : "Other item"} · ${RARITY_NAMES[item.rarity] ?? "?"}${item.stack > 1 ? ` · Stack ×${item.stack}` : ""}</span>
    <div>${inventoryItemStats(item)}${item.requiredLevel > 0 ? ` · Required Lv ${item.requiredLevel}` : ""}</div>
    ${item.affixes.length ? `<div class="inv-affixes">${item.affixes.map(escapeHtml).join(" · ")}</div>` : ""}${comparison}`;
  const close = document.createElement("button");
  close.className = "inv-detail-close";
  close.textContent = "×";
  close.setAttribute("aria-label", "Close item details");
  close.addEventListener("click", () => selectInventoryItem(null));
  invDetailEl.appendChild(close);
  const action = document.createElement("button");
  action.className = "inv-btn";
  action.textContent = inventoryActionLabel(item);
  action.disabled = !item.equipped && !item.canEquip;
  action.addEventListener("click", () => inventoryToggleHandler?.(item.id, item.equipped));
  invDetailEl.appendChild(action);
}

for (const control of [invSearchEl, invFilterEl, invSortEl]) {
  control.addEventListener(control === invSearchEl ? "input" : "change", () => {
    if (inventoryCache && inventoryToggleHandler) renderInventory(inventoryCache, inventoryToggleHandler);
  });
}
invItemsEl.addEventListener("dragover", (event) => event.preventDefault());
invItemsEl.addEventListener("drop", (event) => {
  event.preventDefault();
  const item = inventoryCache?.items.find((candidate) => candidate.id === event.dataTransfer?.getData("text/plain"));
  if (item?.equipped) inventoryToggleHandler?.(item.id, true);
});

type NpcTab = "smelt" | "disassemble" | "essence" | "relic" | "socket" | "add-socket" | "trade" | "buy" | "set";

const npcEl = document.getElementById("npc") as HTMLDivElement;
const npcTitleEl = document.getElementById("npc-title") as HTMLElement;
const npcTabsEl = document.getElementById("npc-tabs") as HTMLDivElement;
const npcHintEl = document.getElementById("npc-hint") as HTMLDivElement;
const npcStageEl = document.getElementById("npc-stage") as HTMLDivElement;
const npcBodyEl = document.getElementById("npc-body") as HTMLDivElement;
const npcActionEl = document.getElementById("npc-action") as HTMLButtonElement;
const npcStatusEl = document.getElementById("npc-status") as HTMLElement;
const npcStockTitleEl = document.getElementById("npc-stock-title") as HTMLElement;
const npcStockCountEl = document.getElementById("npc-stock-count") as HTMLElement;

let npcMode: "smith" | "merchant" | null = null;
let npcTab: NpcTab = "smelt";
let npcPicked = new Set<string>();
let npcTarget: string | null = null;
let npcPickRole: "target" | "source" = "target";
let npcSortBy: "rarity" | "name" = "rarity";
let npcCatalog: MerchantProduct[] = [];
let inventoryCache: WebInventory | null = null;
let npcCharacterId = "";
let npcActionHandler: (() => Promise<void>) | null = null;

const attrsEl = document.getElementById("attrs") as HTMLDivElement;
const attrsItemsEl = document.getElementById("attrs-items") as HTMLDivElement;
const attrsPointsEl = document.getElementById("attrs-points") as HTMLElement;
const attrsDetailItemsEl = document.getElementById("attrs-detail-items") as HTMLDivElement;
type AttrsTab = "offense" | "defense" | "attributes" | "misc";
let attrsTab: AttrsTab = "offense";
const petsEl = document.getElementById("pets") as HTMLDivElement;
const petsTitleEl = document.getElementById("pets-title") as HTMLElement;
const petsTabsEl = document.getElementById("pets-tabs") as HTMLDivElement;
const petsHintEl = document.getElementById("pets-hint") as HTMLDivElement;
const petsItemsEl = document.getElementById("pets-items") as HTMLDivElement;
let petRoster: WebPetRoster | null = null;
let petTabCombat = false;
let petRefreshHandler: (() => Promise<void>) | null = null;
let petSelectHandler: ((definitionIntegerId: number, combat: boolean) => Promise<void>) | null = null;
let petUnlockHandler: ((definitionIntegerId: number, combat: boolean) => Promise<void>) | null = null;
let attributesCache: WebAttributes | null = null;
let attrsRefreshHandler: (() => Promise<void>) | null = null;
let allocateHandler: ((key: AttributeKey) => Promise<void>) | null = null;

const ATTR_LABELS: [AttributeKey, string][] = [
  ["strength", "Strength"], ["dexterity", "Dexterity"], ["intelligence", "Intelligence"],
  ["vitality", "Vitality"], ["constitution", "Constitution"], ["agility", "Agility"], ["mindpower", "Mindpower"],
];

function toggleAttrs(force?: boolean): void {
  const show = force ?? attrsEl.classList.contains("hidden");
  if (show) { inventoryEl.classList.add("hidden"); powersEl.classList.add("hidden"); loadoutEl.classList.add("hidden"); portalEl.classList.add("hidden"); worldsEl.classList.add("hidden"); petsEl.classList.add("hidden"); closeNpc(); void attrsRefreshHandler?.(); }
  attrsEl.classList.toggle("hidden", !show);
}

function renderAttrs(): void {
  const a = attributesCache;
  if (!a) { attrsItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  attrsPointsEl.textContent = String(a.available);
  attrsItemsEl.innerHTML = "";
  const record = a as unknown as Record<string, number>;
  for (const [key, label] of ATTR_LABELS) {
    const allocated = record[`${key}Allocated`] ?? 0;
    const total = record[key] ?? 0;
    const row = document.createElement("div");
    row.className = "inv-row attr-row";
    row.innerHTML = `<span class="attr-label">${label} <span class="attr-help">?</span></span><span class="attr-value" title="${allocated} allocated points">${Math.round(total).toLocaleString()}</span>`;
    const button = document.createElement("button");
    button.className = "attr-add";
    button.textContent = "+";
    button.setAttribute("aria-label", `Add one point to ${label}`);
    button.disabled = a.available <= 0;
    button.addEventListener("click", () => void allocateHandler?.(key));
    row.appendChild(button);
    attrsItemsEl.appendChild(row);
  }
  document.querySelectorAll<HTMLButtonElement>(".attrs-tabs [data-attrs-tab]").forEach((tab) => {
    tab.classList.toggle("active", tab.dataset.attrsTab === attrsTab);
    tab.setAttribute("aria-pressed", String(tab.dataset.attrsTab === attrsTab));
  });
  const format = (value: number): string => new Intl.NumberFormat("en", { maximumFractionDigits: 2 }).format(value);
  const d = a.details ?? {};
  const number = (name: string): number => d[name] ?? 0;
  const percent = (name: string): string => `${format(number(name) * 100)}%`;
  const damage = (type: string): number => number(`Weapon_${type}_Damage_Min_MainHand_Total`) + number(`Weapon_${type}_Damage_Delta_MainHand_Total`) / 2;
  const damageTotal = ["Physical", "Fire", "Cold", "Lightning", "Poison"].reduce((sum, type) => sum + damage(type), 0);
  const detail: [string, string][] = attrsTab === "offense" ? [
    ["Total attack rating", format(number("AttackRating_Total"))],
    ["Main hand average damage", format(damageTotal)],
    ["Physical / elemental damage", `${format(damage("Physical"))} / ${format(damageTotal - damage("Physical"))}`],
    ["Attack speed multiplier", `${format(number("Attack_Speed_Percent_Total"))}×`],
    ["Critical strike chance", percent("Crit_Chance_MainHand_Total")],
    ["Critical strike damage", percent("Crit_Damage_Total")],
    ["Main hand attack range", format(number("Attack_Range_MainHand_Total"))],
  ] : attrsTab === "defense" ? [
    ["Armor", format(number("Armor_Total"))], ["Evasion", format(number("Evasion_Total"))],
    ["Dodge chance", percent("Dodge_Chance_Total")], ["Block chance", percent("Block_Chance_Total")],
    ["Fire resistance", percent("Resistance_Fire_Total_Capped")],
    ["Cold resistance", percent("Resistance_Cold_Total_Capped")],
    ["Lightning resistance", percent("Resistance_Lightning_Total_Capped")],
    ["Poison resistance", percent("Resistance_Poison_Total_Capped")],
  ] : attrsTab === "misc" ? [
    ["Maximum life", format(number("Life_Max_Total"))], ["Life regeneration", format(number("Life_Regen_Total"))],
    ["Maximum mana", format(number("Mana_Max_Total"))], ["Mana regeneration", format(number("Mana_Regen_Total"))],
    ["Movement speed", format(number("Movement_Speed_Total"))],
    ["Magic find", percent("Magic_Find_Percent_Total")], ["Item quantity", percent("Item_Quantity_Bonus_Percent_Total")],
  ] : ATTR_LABELS.map(([key, label]) => [label, format(record[key] ?? 0)]);
  attrsDetailItemsEl.innerHTML = detail.map(([label, value]) => `<div class="attrs-detail-row"><span class="attrs-detail-icon">✦</span><span>${escapeHtml(label)}</span><strong>${escapeHtml(value)}</strong></div>`).join("");
}

function togglePets(combat?: boolean, force?: boolean): void {
  const show = force ?? petsEl.classList.contains("hidden");
  if (!show) { petsEl.classList.add("hidden"); return; }
  if (combat !== undefined) petTabCombat = combat;
  inventoryEl.classList.add("hidden");
  powersEl.classList.add("hidden");
  attrsEl.classList.add("hidden");
  loadoutEl.classList.add("hidden");
  blessingsEl.classList.add("hidden");
  portalEl.classList.add("hidden");
  worldsEl.classList.add("hidden");
  closeNpc();
  petsEl.classList.remove("hidden");
  renderPets();
  void petRefreshHandler?.();
}

function renderPets(): void {
  const data = petRoster;
  petsTitleEl.textContent = petTabCombat ? "Combat Pets" : "Pets";
  petsHintEl.textContent = petTabCombat
    ? "Combat pet unlock prices and affix bonuses are recovered from the target Monster/Affix data. Purchase unlocks and selects the pet."
    : "Companion pet costs and passive affixes are recovered from the target Monster/Affix data. Purchase unlocks and selects the pet.";
  petsTabsEl.innerHTML = `<button class="npc-tab${petTabCombat ? "" : " active"}" data-pet-tab="pets">Pets</button><button class="npc-tab${petTabCombat ? " active" : ""}" data-pet-tab="combat">Combat Pets</button>`;
  for (const button of Array.from(petsTabsEl.querySelectorAll("[data-pet-tab]")))
    button.addEventListener("click", () => { petTabCombat = (button as HTMLElement).dataset.petTab === "combat"; renderPets(); });
  if (!data) { petsItemsEl.innerHTML = `<div class="inv-empty">Loading pet roster…</div>`; return; }
  const options = petTabCombat ? data.combatPets : data.pets;
  petsItemsEl.innerHTML = "";
  for (const pet of options) {
    const row = document.createElement("div");
    row.className = `inv-row${pet.selected ? " equipped" : ""}`;
    const effectText = pet.effects.map(effect => `${prettyPower(effect.name)} ${effect.value >= 0 ? "+" : ""}${effect.value}`).join(" · ");
    const status = !pet.unlocked ? (pet.unlockCost != null ? `Unlock ${pet.unlockCost} ${pet.unlockCurrency === "OP" ? "opals" : "silver"}` : "No purchase price")
      : petTabCombat ? `Lv ${pet.level} · ${pet.isAlive ? "alive" : "dead"}`
      : pet.selected ? "Selected" : "Unlocked";
    row.innerHTML = `<span class="inv-name">${escapeHtml(pet.name)}<span class="inv-affixes">${escapeHtml(status)}${effectText ? ` · ${escapeHtml(effectText)}` : ""}</span></span>`;
    const button = document.createElement("button");
    button.className = "inv-btn";
    if (pet.unlocked) {
      button.textContent = pet.selected ? "Selected" : "Select";
      button.disabled = pet.selected;
      button.addEventListener("click", () => void petSelectHandler?.(pet.definitionIntegerId, petTabCombat));
    } else {
      const balance = pet.unlockCurrency === "OP" ? data.opals : pet.unlockCurrency === "SL" ? data.silver : 0;
      button.textContent = pet.unlockCost != null ? "Unlock" : "Unavailable";
      button.disabled = pet.unlockCost == null || balance < pet.unlockCost;
      button.addEventListener("click", () => void petUnlockHandler?.(pet.definitionIntegerId, petTabCombat));
    }
    row.appendChild(button);
    petsItemsEl.appendChild(row);
  }
  if (options.length === 0) petsItemsEl.innerHTML = `<div class="inv-empty">No pet definitions recovered for this version.</div>`;
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
const portalChestEl = document.getElementById("portal-chest") as HTMLButtonElement;
const portalStatusEl = document.getElementById("portal-status") as HTMLElement;
const worldsEl = document.getElementById("worlds") as HTMLDivElement;
const worldsItemsEl = document.getElementById("worlds-items") as HTMLDivElement;
const worldsTierEl = document.getElementById("worlds-tier") as HTMLElement;
const worldsStatusEl = document.getElementById("worlds-status") as HTMLElement;

let offlineCache: OfflineView | null = null;
let blessingsCache: BlessingsView | null = null;
let portalCache: WebPortalState | null = null;
let worldsCache: WorldProgress | null = null;
let worldsRefreshHandler: (() => Promise<void>) | null = null;
let worldSelectHandler: ((tier: number, waypoint: number) => Promise<void>) | null = null;
let townActionHandler: (() => Promise<void>) | null = null;
let travelInProgress = false;
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
    worldsEl.classList.add("hidden");
    petsEl.classList.add("hidden");
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

function setWorldStatus(message: string): void {
  worldsStatusEl.textContent = message;
}

function toggleWorlds(force?: boolean): void {
  const show = force ?? worldsEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    loadoutEl.classList.add("hidden");
    blessingsEl.classList.add("hidden");
    portalEl.classList.add("hidden");
    petsEl.classList.add("hidden");
    closeNpc();
    setWorldStatus("");
    void worldsRefreshHandler?.();
  }
  worldsEl.classList.toggle("hidden", !show);
}

function renderWorlds(): void {
  const progress = worldsCache;
  if (!progress) { worldsItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  worldsTierEl.textContent = `Tier ${progress.currentTier} · unlocked ${progress.unlockedTier}`;
  worldsItemsEl.innerHTML = "";
  for (const option of progress.worlds) {
    const row = document.createElement("div");
    row.className = `inv-row${option.selected ? " equipped" : ""}`;
    const currentWaypoint = Math.max(1, option.currentWaypoint);
    const maxWaypoint = Math.max(currentWaypoint, option.maxWaypoint);
    row.innerHTML = `<span class="inv-name">Tier ${option.tier} · ${escapeHtml(option.name)}<span class="inv-affixes">Boss: ${escapeHtml(option.bossName || "Unknown")} · Checkpoint ${currentWaypoint} / ${maxWaypoint}</span></span>`;
    const waypointSelect = document.createElement("select");
    waypointSelect.className = "world-waypoint";
    waypointSelect.setAttribute("aria-label", `Tier ${option.tier} checkpoint`);
    for (let waypoint = 1; waypoint <= maxWaypoint; waypoint++) {
      const choice = document.createElement("option");
      choice.value = String(waypoint);
      choice.textContent = `WP ${waypoint}`;
      waypointSelect.appendChild(choice);
    }
    waypointSelect.value = String(Math.min(currentWaypoint, maxWaypoint));
    waypointSelect.disabled = !option.unlocked;
    const button = document.createElement("button");
    button.className = "inv-btn";
    button.textContent = option.unlocked ? (option.selected ? "Travel" : "Enter") : "Locked";
    button.disabled = !option.unlocked;
    button.addEventListener("click", () => void worldSelectHandler?.(option.tier, Number(waypointSelect.value)));
    row.appendChild(waypointSelect);
    row.appendChild(button);
    worldsItemsEl.appendChild(row);
  }
  if (progress.worlds.length === 0) worldsItemsEl.innerHTML = `<div class="inv-empty">No world data available.</div>`;
}

function togglePortal(force?: boolean): void {
  const show = force ?? portalEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    loadoutEl.classList.add("hidden");
    blessingsEl.classList.add("hidden");
    worldsEl.classList.add("hidden");
    petsEl.classList.add("hidden");
    closeNpc();
    void portalRefreshHandler?.();
  }
  portalEl.classList.toggle("hidden", !show);
}

function renderPortal(): void {
  const state = portalCache;
  if (!state) { portalBodyEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
  if (state.inNiflheim) {
    const chest = state.exitReady && state.chestSize > 0
      ? state.chestOpened ? "Chest opened." : `${state.chestSize === 2 ? "Large" : "Medium"} chest available.`
      : "";
    const detail = state.exitReady ? `Town portal ready. ${chest}` : `${Math.max(1, state.packs - 1)} combat packs; return early to abandon the run.`;
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">In Niflheim<span class="inv-affixes">${detail}</span></span></div>`;
    portalActionEl.textContent = state.exitReady ? "Use town portal" : "Abandon run";
  } else if (state.hasPortal) {
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">Niflheim portal ready<span class="inv-affixes">Consumes one portal and starts a run (${state.runsCleared} cleared).</span></span></div>`;
    portalActionEl.textContent = "Enter Niflheim";
  } else {
    portalBodyEl.innerHTML = `<div class="inv-row"><span class="inv-name">No portal<span class="inv-affixes">Buy a NiflheimPortal from the merchant first.</span></span></div>`;
    portalActionEl.textContent = "Enter Niflheim";
  }
  portalChestEl.hidden = !state.inNiflheim || !state.exitReady || state.chestSize === 0 || state.chestOpened;
  portalChestEl.disabled = travelInProgress;
  portalActionEl.disabled = travelInProgress || (!state.inNiflheim && !state.hasPortal);
}

let loadoutCache: Loadout | null = null;
let loadoutSelection: { active: Set<string>; passive: Set<string> } = { active: new Set(), passive: new Set() };
let skillsTab: "active" | "passive" = "active";
let loadoutRefreshHandler: (() => Promise<void>) | null = null;
let loadoutSaveHandler: (() => Promise<void>) | null = null;
const poolByName = new Map<string, PowerPoolOption>();

function toggleLoadout(force?: boolean): void {
  const show = force ?? loadoutEl.classList.contains("hidden");
  if (show) {
    inventoryEl.classList.add("hidden");
    powersEl.classList.add("hidden");
    attrsEl.classList.add("hidden");
    portalEl.classList.add("hidden");
    worldsEl.classList.add("hidden");
    petsEl.classList.add("hidden");
    closeNpc();
    void loadoutRefreshHandler?.();
  }
  loadoutEl.classList.toggle("hidden", !show);
}

function renderLoadout(): void {
  const data = loadoutCache;
  if (!data) { loadoutItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; skillsLoadoutItemsEl.innerHTML = `<div class="inv-empty">Loading…</div>`; return; }
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

  for (const [id, name] of [["skills-active-tab", "active"], ["skills-passive-tab", "passive"]] as const) {
    const button = document.getElementById(id) as HTMLButtonElement;
    button.classList.toggle("active", skillsTab === name);
    button.setAttribute("aria-pressed", String(skillsTab === name));
  }
  const options = skillsTab === "active" ? data.poolActive : data.poolPassive;
  const selected = skillsTab === "active" ? loadoutSelection.active : loadoutSelection.passive;
  const cap = skillsTab === "active" ? data.maxActive : data.maxPassive;
  skillsAssignedCountEl.textContent = `${selected.size}/${cap}`;
  skillsAssignedEl.innerHTML = [...selected].map((name) => {
    const icon = powerIconPath(poolByName.get(name)?.icon ?? "");
    return `<span class="skill-assigned-token" title="${escapeHtml(prettyPower(name))}">${icon ? `<img src="${icon}" alt="" />` : escapeHtml(prettyPower(name).slice(0, 2))}</span>`;
  }).join("") +
    Array.from({ length: Math.max(0, cap - selected.size) }, () => `<span class="skill-assigned-token empty">+</span>`).join("");
  skillsLoadoutItemsEl.innerHTML = "";
  for (const option of options) {
    const chosen = selected.has(option.name);
    const row = document.createElement("div");
    row.className = `skillbook-row${chosen ? " equipped" : ""}${selectedMasterySkill === option.name ? " selected" : ""}`;
    const icon = powerIconPath(option.icon);
    row.innerHTML = `<span class="skillbook-icon">${icon ? `<img src="${icon}" alt="" />` : escapeHtml(prettyPower(option.name).slice(0, 1))}</span><span class="skillbook-copy"><strong>${escapeHtml(prettyPower(option.name))}</strong><small>${escapeHtml((option.description ?? "").replace(/\{[0-9]+\}/g, "…"))}</small></span>`;
    const button = document.createElement("button");
    button.className = "inv-btn";
    button.textContent = chosen ? "Remove" : "Assign";
    button.disabled = !chosen && selected.size >= cap;
    button.addEventListener("click", () => {
      if (chosen) selected.delete(option.name);
      else selected.add(option.name);
      selectedMasterySkill = option.name;
      skillsStatusEl.textContent = "Unsaved changes";
      renderLoadout();
    });
    row.appendChild(button);
    row.addEventListener("click", (event) => {
      if ((event.target as HTMLElement).closest("button")) return;
      selectedMasterySkill = option.name;
      renderLoadout();
    });
    skillsLoadoutItemsEl.appendChild(row);
  }
  if (masteryAllocate) renderPowers(masteryRows, masteryAllocate);
}

function prettyPower(name: string): string {
  return name.replace(/([a-z])([A-Z])/g, "$1 $2");
}

function powerIconPath(name: string): string | null {
  return /^[A-Za-z0-9_]+$/.test(name) ? `/assets/power-icons/${name}.png` : null;
}

const NPc_TABS: Record<string, { label: string; hint: string }> = {
  smelt: { label: "Smelting", hint: "Steel: 300 Iron + essences per ingot. Titansteel: 10 Titanium Ore + 100 Steel. Unused materials are kept." },
  disassemble: { label: "Disassemble", hint: "Extract essences from eligible equipment affixes." },
  essence: { label: "Reforge", hint: "Pick a target and sources; consumes Iron. New affixes need a matching open prefix or suffix slot." },
  relic: { label: "Bless", hint: "Pick a target and a source relic; blesses the target's affixes." },
  socket: { label: "Socket", hint: "Pick a gem and a target; inserts the gem into a free socket." },
  "add-socket": { label: "Add Socket", hint: "Pick a target; consumes Titansteel to add a socket." },
  trade: { label: "Trade", hint: "Offer items for the selected merchant product." },
  buy: { label: "Buy", hint: "Purchase a product with silver or opals." },
  set: { label: "Set", hint: "Trade an item for a set item of the same slot (set merchant)." },
};

function openNpc(mode: "smith" | "merchant", initialTab?: NpcTab): void {
  npcMode = mode;
  npcTab = initialTab ?? (mode === "smith" ? "smelt" : "buy");
  npcPickRole = "target";
  attrsEl.classList.add("hidden");
  petsEl.classList.add("hidden");
  portalEl.classList.add("hidden");
  worldsEl.classList.add("hidden");
  npcPicked.clear();
  npcTarget = null;
  npcStatusEl.textContent = "";
  inventoryEl.classList.add("hidden");
  powersEl.classList.add("hidden");
  npcEl.classList.remove("hidden");
  renderNpc();
  if (npcCharacterId) void api.inventory(npcCharacterId).then((inventory) => { inventoryCache = inventory; if (npcMode) renderNpc(); }).catch(() => undefined);
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
  npcTitleEl.textContent = npcTab === "disassemble" ? "Disassemble" : npcTab === "set" ? "Set Merchant" : npcMode === "smith" ? "Blacksmith" : "Merchant";
  (document.getElementById("npc-silver") as HTMLElement).textContent = `${document.getElementById("hud-silver")?.textContent ?? "0"} Silver`;
  (document.getElementById("npc-opals") as HTMLElement).textContent = `${document.getElementById("hud-opals")?.textContent ?? "0"} Opals`;
  const tabs: NpcTab[] = npcMode === "smith"
    ? ["smelt", "disassemble", "essence", "relic", "socket", "add-socket"]
    : ["buy", "trade", "set"];
  if (!tabs.includes(npcTab)) npcTab = tabs[0];
  npcTabsEl.innerHTML = tabs.map((t) =>
    `<button class="npc-tab${t === npcTab ? " active" : ""}" data-tab="${t}">${NPc_TABS[t].label}</button>`).join("");
  for (const button of Array.from(npcTabsEl.querySelectorAll(".npc-tab")))
    button.addEventListener("click", () => { npcTab = (button as HTMLElement).dataset.tab as NpcTab; npcPicked.clear(); npcTarget = null; npcPickRole = "target"; npcStatusEl.textContent = ""; renderNpc(); });
  npcHintEl.textContent = NPc_TABS[npcTab].hint;
  const items = (inventoryCache?.items ?? []).filter((item) => !item.equipped);
  items.sort((a, b) => npcSortBy === "name" ? a.name.localeCompare(b.name) : b.rarity - a.rarity || a.name.localeCompare(b.name));
  const sortButton = document.getElementById("npc-sort") as HTMLButtonElement;
  sortButton.textContent = `Sort: ${npcSortBy}`;
  sortButton.hidden = npcTab === "buy";
  npcStockTitleEl.textContent = npcTab === "buy" ? "Merchant stock" : "Inventory";
  npcStockCountEl.textContent = npcTab === "buy" ? `${npcCatalog.length} products` : `${items.length} items`;
  npcActionEl.style.display = npcTab === "buy" || npcTab === "set" ? "none" : "";
  npcActionEl.textContent = NPc_TABS[npcTab].label;
  renderNpcStage(items);

  if (npcTab === "buy") {
    npcBodyEl.className = "npc-body npc-products";
    npcBodyEl.innerHTML = npcCatalog.length === 0 ? `<div class="inv-empty">Loading merchant stock…</div>`
      : npcCatalog.map((product) => `<div class="npc-row npc-product-card${npcTarget === product.itemId ? " selected" : ""}" data-preview="${product.itemId}">
        <span class="npc-product-art">${npcArt(product.definitionIntegerId, product.name, 0)}</span>
        <strong>${escapeHtml(prettyPower(product.name))}</strong>
        <span class="npc-product-prices">${product.silverPrice.toLocaleString()} Silver · ${product.opalPrice.toLocaleString()} Opals</span>
        <span class="npc-product-actions"><button class="inv-btn" data-buy="silver" data-id="${product.itemId}">Buy with silver</button><button class="inv-btn" data-buy="opal" data-id="${product.itemId}">Buy with opals</button></span>
      </div>`).join("");
    for (const button of Array.from(npcBodyEl.querySelectorAll<HTMLButtonElement>("[data-buy]")))
      button.addEventListener("click", () => void buyProduct(button.dataset.id!, button.dataset.buy === "opal"));
    for (const card of Array.from(npcBodyEl.querySelectorAll<HTMLElement>("[data-preview]")))
      card.addEventListener("click", (event) => { if (!(event.target as HTMLElement).closest("[data-buy]")) { npcTarget = card.dataset.preview!; renderNpc(); } });
    return;
  }

  npcBodyEl.className = "npc-body npc-item-grid";
  npcBodyEl.innerHTML = items.length === 0 ? `<div class="inv-empty">Your bag is empty.</div>`
    : items.map((item) => `<div class="npc-row npc-item-card${npcPicked.has(item.id) ? " picked" : ""}${npcTarget === item.id ? " target" : ""}" data-item="${item.id}" tabindex="0" role="button" aria-label="${escapeHtml(item.name)}">
      ${npcArt(item.definitionIntegerId, item.name, item.rarity)}${item.stack > 1 ? `<span class="inv-stack">${item.stack}</span>` : ""}
      ${npcTab === "set" ? `<button class="npc-set-button" data-set-trade="${item.id}">Set</button>` : ""}
    </div>`).join("");
  for (const row of Array.from(npcBodyEl.querySelectorAll<HTMLElement>("[data-item]"))) {
    const item = items.find((entry) => entry.id === row.dataset.item)!;
    row.title = `${item.name} · ${inventoryItemStats(item)}${item.affixes.length ? `\n${item.affixes.join("\n")}` : ""}`;
    row.addEventListener("click", (event) => { if (!(event.target as HTMLElement).closest("[data-set-trade]")) onClickItem(item.id); });
    row.addEventListener("keydown", (event) => { if (event.key === "Enter") onClickItem(item.id); });
  }
  for (const button of Array.from(npcBodyEl.querySelectorAll<HTMLButtonElement>("[data-set-trade]")))
    button.addEventListener("click", () => void setTrade(button.dataset.setTrade!));
  const needsSource = npcTab !== "add-socket";
  const needsTarget = ["essence", "relic", "socket", "add-socket", "trade"].includes(npcTab);
  npcActionEl.disabled = (needsTarget && !npcTarget) || (needsSource && npcPicked.size === 0);
}

function onClickItem(itemId: string): void {
  if (npcTab === "set" || npcTab === "buy") return;
  if (npcTab === "add-socket" || (["essence", "relic", "socket"].includes(npcTab) && npcPickRole === "target")) {
    npcTarget = npcTarget === itemId ? null : itemId;
    npcPicked.delete(itemId);
    if (npcTarget) npcPickRole = "source";
  } else if (npcPicked.has(itemId)) {
    npcPicked.delete(itemId);
  } else {
    if (npcTarget === itemId) npcTarget = null;
    npcPicked.add(itemId);
  }
  renderNpc();
}

function npcArt(definitionId: number, name: string, rarity: number): string {
  const imagePath = inventoryItemArtPaths[String(definitionId)];
  const color = RARITY_COLORS[rarity] ?? "#6f6155";
  return `<span class="item-art${imagePath ? "" : " missing"}" style="--item-rarity:${color}">${imagePath ? `<img src="${escapeHtml(imagePath)}" alt="" loading="lazy" />` : ""}<span class="item-art-fallback">${escapeHtml(name.charAt(0).toUpperCase())}</span></span>`;
}

function renderNpcStage(items: WebItemDetail[]): void {
  const selected = items.filter((item) => npcPicked.has(item.id));
  const target = items.find((item) => item.id === npcTarget);
  const pickedTiles = selected.map((item) => `<span class="npc-stage-tile" title="${escapeHtml(item.name)}">${npcArt(item.definitionIntegerId, item.name, item.rarity)}</span>`).join("");
  if (npcTab === "buy") {
    const featured = npcCatalog.find((product) => product.itemId === npcTarget) ?? npcCatalog[0];
    if (featured) npcTarget = featured.itemId;
    npcStageEl.innerHTML = featured ? `<div class="npc-stage-heading">Selected product</div><div class="npc-featured-art">${npcArt(featured.definitionIntegerId, featured.name, 0)}</div><h3 class="npc-featured-name">${escapeHtml(prettyPower(featured.name))}</h3><div class="npc-featured-price">${featured.silverPrice.toLocaleString()} Silver <span>or</span> ${featured.opalPrice.toLocaleString()} Opals</div><p class="npc-stage-muted">Choose a currency on the product card. Purchased items are delivered to your inventory.</p>`
      : `<div class="npc-stage-heading">Merchant</div><p>Loading merchant stock…</p>`;
    return;
  }
  if (npcTab === "trade") {
    npcStageEl.innerHTML = `<div class="npc-stage-heading">Choose an offer</div><div class="npc-offer-list">${npcCatalog.map((product) => `<button class="npc-offer${npcTarget === product.itemId ? " selected" : ""}" data-trade="${product.itemId}">${npcArt(product.definitionIntegerId, product.name, 0)}<span>${escapeHtml(prettyPower(product.name))}</span></button>`).join("") || "Loading offers…"}</div><div class="npc-stage-heading">Items to trade (${selected.length})</div><div class="npc-stage-grid">${pickedTiles || `<span class="npc-stage-empty">Select items from your inventory</span>`}</div>`;
    for (const button of Array.from(npcStageEl.querySelectorAll<HTMLButtonElement>("[data-trade]")))
      button.addEventListener("click", () => { npcTarget = button.dataset.trade!; renderNpc(); });
    return;
  }
  if (npcTab === "set") {
    npcStageEl.innerHTML = `<div class="npc-stage-heading">Set item exchange</div><p>Select an eligible item in your inventory and choose Set to exchange it for a set item of the same slot.</p><div class="npc-stage-crest">◆</div>`;
    return;
  }
  if (npcTab === "smelt" || npcTab === "disassemble") {
    const summary = npcTab === "disassemble" ? `${selected.length} item${selected.length === 1 ? "" : "s"} selected for disassembly` : `${selected.length} material${selected.length === 1 ? "" : "s"} selected for smelting`;
    const details = npcTab === "disassemble" && selected.length
      ? `<div class="npc-stage-breakdown"><strong>Selected item summary</strong>${selected.map((item) => `<div><span>${escapeHtml(item.name)}</span><span style="color:${RARITY_COLORS[item.rarity] ?? "#aaa"}">${RARITY_NAMES[item.rarity] ?? "?"} · ${item.affixes.length} affixes</span></div>`).join("")}</div>` : "";
    npcStageEl.innerHTML = `<div class="npc-stage-heading">${npcTab === "disassemble" ? "Items to disassemble" : "Source materials"}</div><div class="npc-stage-grid">${pickedTiles || `<span class="npc-stage-empty">Select items from your inventory</span>`}</div><div class="npc-stage-summary">${summary}</div>${details}`;
    return;
  }
  const targetTile = target ? npcArt(target.definitionIntegerId, target.name, target.rarity) : `<span class="npc-stage-placeholder">+</span>`;
  npcStageEl.innerHTML = `<div class="npc-stage-heading">${NPc_TABS[npcTab].label}</div><div class="npc-slot-picker">
    <button class="npc-stage-choice${npcPickRole === "target" ? " active" : ""}" data-role="target"><small>Target item</small><span class="npc-stage-tile">${targetTile}</span><span>${target ? escapeHtml(target.name) : "Select target"}</span></button>
    ${npcTab === "add-socket" ? "" : `<button class="npc-stage-choice${npcPickRole === "source" ? " active" : ""}" data-role="source"><small>Source item</small><span class="npc-stage-tile">${selected[0] ? npcArt(selected[0].definitionIntegerId, selected[0].name, selected[0].rarity) : `<span class="npc-stage-placeholder">+</span>`}</span><span>${selected.length ? `${selected.length} selected` : "Select source"}</span></button>`}
  </div><div class="npc-stage-summary">${target ? escapeHtml(target.name) : "Choose a target from your inventory"}${selected.length ? ` · ${selected.length} source item${selected.length === 1 ? "" : "s"}` : ""}</div>`;
  for (const button of Array.from(npcStageEl.querySelectorAll<HTMLButtonElement>("[data-role]")))
    button.addEventListener("click", () => { npcPickRole = button.dataset.role as "target" | "source"; renderNpc(); });
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
  boss.classList.toggle("hidden", hudState.bossAlive || hudState.bossKillsRemaining <= 0 || hudState.totalPacks > 0);
  (document.getElementById("hud-boss-count") as HTMLElement).textContent = String(hudState.bossKillsRemaining);
  const packs = document.getElementById("hud-packs") as HTMLElement;
  packs.classList.toggle("hidden", hudState.totalPacks <= 0);
  (document.getElementById("hud-packs-count") as HTMLElement).textContent =
    hudState.niflheimExitReady ? "Exit ready" : `${hudState.packsRemaining}/${hudState.totalPacks}`;
  const townButton = document.getElementById("hud-town") as HTMLButtonElement;
  townButton.firstChild!.textContent = hudState.inTown ? "Return to World " : "Town ";
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
    button.classList.toggle("disabled", !ready || hudState.inTown);
    button.disabled = hudState.inTown || !ready;
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
