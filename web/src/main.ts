import "./style.css";
import { api, CharacterSummary, Snapshot } from "./api";
import { loadContent, ContentManifest, className } from "./content";
import { HudState, World } from "./game";

const app = document.getElementById("app")!;
app.innerHTML = `
  <canvas id="game"></canvas>
  <div id="overlay"></div>
  <div id="boot" class="panel">
    <h1>Nordicandia <span>web M0</span></h1>
    <p class="sub">3D dungeon kit + avatar tokens, online snapshot. Dev login is local-only.</p>
    <label>Account name <input id="boot-name" value="m0" autocomplete="off" /></label>
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
      <div class="bar xp"><div id="hud-xp-fill"></div><span id="hud-xp-text"></span></div>
    </div>
    <div class="hud-top-right">
      <div>Lv <b id="hud-level">1</b></div>
      <div><span id="hud-silver">0</span> silver · <span id="hud-opals">0</span> opals</div>
      <div><span id="hud-kills">0</span> kills · <span id="hud-alive">0</span> monsters</div>
      <div class="source">content <span id="hud-content">…</span></div>
    </div>
    <div class="hud-bottom">
      <button id="hud-skill" class="skill">Skill <small>Space</small></button>
      <button id="hud-auto" class="skill alt">Auto-move: ON <small>Tab</small></button>
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

let content: ContentManifest;
let world: World | null = null;

function setStatus(text: string, error = false): void {
  statusEl.textContent = text;
  statusEl.classList.toggle("error", error);
}

async function boot(): Promise<void> {
  content = await loadContent();
  contentEl.textContent = content.contentVersion;
  // Server/client content-version guard: both must agree before the world loads.
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
  // Mage (5) exists in the visible classes; pick it if present for the default slice.
  const mage = content.classes.find((c) => c.name === "Mage");
  if (mage) classSelect.value = String(mage.integerId);

  document.getElementById("boot-enter")!.addEventListener("click", () => {
    void enterWorld();
  });
}

async function enterWorld(): Promise<void> {
  const name = (document.getElementById("boot-name") as HTMLInputElement).value.trim() || "m0";
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
    const character = account.characters[0];
    setStatus("Reading snapshot…");
    const snapshot: Snapshot = await api.snapshot(character.characterId);
    await startGame(snapshot, character);
  } catch (error) {
    console.error(error);
    setStatus(`Failed: ${error instanceof Error ? error.message : String(error)}`, true);
    button.disabled = false;
  }
}

async function startGame(snapshot: Snapshot, character: CharacterSummary): Promise<void> {
  bootPanel.hidden = true;
  hud.hidden = false;
  (document.getElementById("hud-name") as HTMLElement).textContent = character.displayName;
  (document.getElementById("hud-class") as HTMLElement).textContent =
    `${className(content, character.class)} · Lv ${snapshot.level}`;

  world = new World(canvas, overlay, snapshot, content, renderHud);
  await world.start(snapshot);

  document.getElementById("hud-skill")!.addEventListener("click", () => world?.useSkill());
  const autoButton = document.getElementById("hud-auto") as HTMLButtonElement;
  autoButton.addEventListener("click", () => {
    const on = world?.toggleAutoMove() ?? false;
    autoButton.firstChild!.textContent = `Auto-move: ${on ? "ON" : "OFF"} `;
  });
  window.addEventListener("keydown", (event) => {
    if (event.code === "Space") {
      event.preventDefault();
      world?.useSkill();
    } else if (event.code === "Tab") {
      event.preventDefault();
      const on = world?.toggleAutoMove() ?? false;
      autoButton.firstChild!.textContent = `Auto-move: ${on ? "ON" : "OFF"} `;
    }
  });
}

function renderHud(hudState: HudState): void {
  const hpPercent = Math.max(0, Math.min(100, (hudState.health / hudState.maxHealth) * 100));
  (document.getElementById("hud-hp-fill") as HTMLElement).style.width = `${hpPercent}%`;
  (document.getElementById("hud-hp-text") as HTMLElement).textContent =
    `${hudState.health} / ${hudState.maxHealth}`;
  const span = Math.max(1, hudState.experienceForNextLevel - hudState.experienceForLevel);
  const xpPercent = Math.max(0, Math.min(100, ((hudState.experience - hudState.experienceForLevel) / span) * 100));
  (document.getElementById("hud-xp-fill") as HTMLElement).style.width = `${xpPercent}%`;
  (document.getElementById("hud-xp-text") as HTMLElement).textContent =
    `XP ${Math.round(hudState.experience)} / ${Math.round(hudState.experienceForNextLevel)}`;
  (document.getElementById("hud-level") as HTMLElement).textContent = String(hudState.level);
  (document.getElementById("hud-silver") as HTMLElement).textContent = String(hudState.silver);
  (document.getElementById("hud-opals") as HTMLElement).textContent = String(hudState.opals);
  (document.getElementById("hud-kills") as HTMLElement).textContent = String(hudState.monsterKills);
  (document.getElementById("hud-alive") as HTMLElement).textContent = String(hudState.monstersAlive);
  const message = document.getElementById("hud-message") as HTMLElement;
  message.textContent = hudState.message;
  message.classList.toggle("show", Boolean(hudState.message));
}

function capitalize(value: string): string {
  return value.length ? value[0].toUpperCase() + value.slice(1) : value;
}

boot().catch((error) => {
  console.error(error);
  setStatus(`Boot failed: ${error instanceof Error ? error.message : String(error)}`, true);
});
