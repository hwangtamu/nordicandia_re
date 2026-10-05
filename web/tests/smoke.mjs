// M0 smoke test: drive the web client against a running server + Vite dev server and
// assert that the world loads with a live snapshot, no console/network errors.
//
// Prerequisites:
//   server: NORD_WEB_DEV=1 NORD_WEB_PORT=5080 dotnet ... (see docs/web/M0_STATUS.md)
//   client: npm run dev
// Run:
//   npm run smoke
//
// CHROME_PATH may point at a Chromium/Chrome binary when the bundled Playwright browser
// for the installed version is unavailable.
import { fileURLToPath } from "node:url";
import path from "node:path";
import fs from "node:fs";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
const url = process.env.WEB_URL ?? "http://127.0.0.1:5173/";
const out = process.env.OUT ?? path.join(repoRoot, "tmp", "web-m2", "smoke.png");
const townOut = process.env.TOWN_OUT ?? path.join(repoRoot, "tmp", "web-town", "town.png");
const bagOut = process.env.BAG_OUT ?? path.join(repoRoot, "tmp", "web-m2", "bag.png");
const attrsOut = process.env.ATTRS_OUT ?? path.join(repoRoot, "tmp", "web-m2", "attributes.png");
const skillsOut = process.env.SKILLS_OUT ?? path.join(repoRoot, "tmp", "web-m2", "skills.png");
const passiveOut = process.env.PASSIVE_OUT ?? path.join(repoRoot, "tmp", "web-m2", "passive.png");

function resolveChrome() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
  try {
    const bundled = chromium.executablePath();
    if (bundled && fs.existsSync(bundled)) return bundled;
  } catch {
    /* fall through to cache scan */
  }
  const cache = path.join(process.env.HOME ?? "", "Library/Caches/ms-playwright");
  if (fs.existsSync(cache)) {
    for (const entry of fs.readdirSync(cache).filter((n) => n.startsWith("chromium-")).sort().reverse()) {
      const candidate = path.join(
        cache, entry,
        "chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing",
      );
      if (fs.existsSync(candidate)) return candidate;
    }
  }
  return undefined;
}

const browser = await chromium.launch({
  executablePath: resolveChrome(),
  args: ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
});
const page = await browser.newPage({ viewport: {
  width: Number(process.env.VIEWPORT_WIDTH ?? 1440),
  height: Number(process.env.VIEWPORT_HEIGHT ?? 900),
} });
const errors = [];
page.on("console", (msg) => {
  if (msg.type() === "error") errors.push("console: " + msg.text());
});
page.on("pageerror", (err) => errors.push("pageerror: " + err.message));
page.on("requestfailed", (req) => errors.push(`requestfailed: ${req.url()} ${req.failure()?.errorText}`));
page.on("response", (res) => {
  if (res.status() >= 400) errors.push(`http ${res.status()}: ${res.url()}`);
});

try {
  await page.goto(url, { waitUntil: "networkidle" });
  await page.waitForSelector("#boot-enter", { timeout: 15000 });
  await page.fill("#boot-name", "smoke");
  await page.click("#boot-enter");
  await page.waitForSelector("#hud:not([hidden])", { timeout: 20000 });
  await page.waitForTimeout(6000);
  await page.click("#hud-worlds");
  await page.waitForSelector("#worlds .inv-row", { timeout: 10000 });
  const worldRows = await page.$$eval("#worlds .inv-row", (rows) => rows.length);
  const checkpointSelectors = await page.$$eval("#worlds .world-waypoint", (items) => items.length);
  await page.click("#worlds-close");
  const townManifestLoaded = page.waitForResponse((res) => res.url().endsWith("/assets/kit/town/town_scene.json"));
  const townSceneLoaded = page.waitForResponse((res) => res.url().endsWith("/assets/kit/town/town_scene.glb"));
  let townEnterRequests = 0;
  page.on("request", (req) => {
    if (req.method() === "POST" && req.url().endsWith("/town/enter")) townEnterRequests++;
  });
  // Burst clicks must produce one transition, not toggle back once the first request lands.
  await page.locator("#hud-town").evaluate((button) => { button.click(); button.click(); button.click(); });
  await page.waitForFunction(() => document.querySelector("#hud-town")?.textContent?.includes("Return to World"));
  const townAssetResponses = [await townManifestLoaded, await townSceneLoaded];
  if (townAssetResponses.some((res) => !res.ok())) throw new Error("Town scene assets failed to load");
  const townMonsters = await page.textContent("#hud-alive");
  await page.waitForSelector(".town-npc[data-action='blacksmith']", { timeout: 15000 });
  await page.waitForTimeout(1000);
  if (townEnterRequests !== 1) throw new Error(`Town click burst sent ${townEnterRequests} entry requests`);
  fs.mkdirSync(path.dirname(townOut), { recursive: true });
  await page.screenshot({ path: townOut });
  await page.click(".town-npc[data-action='blacksmith']");
  await page.waitForSelector("#npc:not([hidden])", { timeout: 5000 });
  const townSmithTitle = await page.textContent("#npc-title");
  await page.click("#npc-close");
  await page.click("#hud-town");
  await page.waitForFunction(() => document.querySelector("#hud-town")?.textContent?.includes("Town"));
  await page.waitForTimeout(600);
  await page.click("#hud-skill");
  await page.waitForTimeout(1500);
  await page.click("#hud-skill-3");
  await page.waitForTimeout(6500);

  // M2: open the bag, equip the first unequipped item, and confirm the server marked it.
  await page.click("#hud-bag");
  await page.waitForTimeout(1000);
  await page.waitForSelector("#equipment-slots .equip-slot", { timeout: 10000 });
  await page.waitForSelector("#inv-items .inv-card", { timeout: 10000 });
  const initialBagCards = await page.$$eval("#inv-items .inv-card", (cards) => cards.length);
  const itemArtTiles = await page.$$eval("#inv-items .inv-card", (cards) => cards.filter((card) => {
    const art = card.querySelector(".item-art");
    const image = art?.querySelector("img");
    return !!art && (art.classList.contains("missing") || !!image?.getAttribute("src"));
  }).length);
  const equipButton = page.locator("#inv-items .inv-card .inv-btn:not([disabled])").first();
  if (await equipButton.count() > 0) {
    const itemId = await equipButton.locator("xpath=..").getAttribute("data-item-id");
    await equipButton.click({ timeout: 10000 });
    const slot = page.locator(`#equipment-slots .equip-slot[data-item-id='${itemId}']`);
    await slot.waitFor({ timeout: 10000 }).catch(async (error) => {
      fs.mkdirSync(path.dirname(bagOut), { recursive: true });
      await page.screenshot({ path: bagOut });
      console.log(`equip wait failed: ${itemId}; detail=${await page.textContent("#inv-detail")}`);
      throw error;
    });
    const slotNumber = await slot.getAttribute("data-slot");
    await slot.dragTo(page.locator("#inv-items"));
    await page.locator(`#inv-items .inv-card[data-item-id='${itemId}']`).waitFor({ timeout: 10000 });
    await page.locator(`#inv-items .inv-card[data-item-id='${itemId}']`)
      .dragTo(page.locator(`#equipment-slots .equip-slot[data-slot='${slotNumber}']`));
    await slot.waitFor({ timeout: 10000 });
  }
  const equippedRows = await page.$$eval("#equipment-slots .equip-slot.filled", (rows) => rows.length);
  await page.selectOption("#inv-filter", "gear");
  const filteredOther = await page.$$eval("#inv-items .inv-card .inv-card-meta", (rows) =>
    rows.some((row) => row.textContent?.startsWith("Other")));
  await page.selectOption("#inv-filter", "all");
  if (filteredOther) throw new Error("U01 gear filter included a non-equipment item");
  fs.mkdirSync(path.dirname(bagOut), { recursive: true });
  await page.screenshot({ path: bagOut });
  const visibleBagItem = page.locator("#inv-items .inv-card").first();
  if (await visibleBagItem.count() > 0) {
    await visibleBagItem.click();
    await page.locator("#inv-detail:not(.hidden)").waitFor({ timeout: 5000 });
    await page.locator(".inv-detail-close").click();
    await page.locator("#inv-detail").waitFor({ state: "hidden", timeout: 5000 });
  }
  const lootRows = await page.$$eval(".loot-item", (rows) => rows.length);

  // The three white character tabs keep the same window frame and switch views.
  await page.click("#inventory .character-tabs [data-panel='attrs']");
  await page.waitForSelector("#attrs:not(.hidden) .attr-row", { timeout: 5000 });
  await page.click("#attrs .attrs-tabs [data-attrs-tab='defense']");
  await page.waitForFunction(() => document.querySelector("#attrs .attrs-detail-items")?.textContent?.includes("Fire resistance"));
  if (await page.locator("#attrs .attrs-detail-row").count() < 8) throw new Error("Attribute detail rows are missing");
  fs.mkdirSync(path.dirname(attrsOut), { recursive: true });
  await page.screenshot({ path: attrsOut });

  // M3: the skills tab combines assignment and mastery allocation.
  await page.click("#attrs .character-tabs [data-panel='skills']");
  await page.waitForSelector("#powers:not(.hidden) .skillbook-row", { timeout: 5000 });
  await page.waitForSelector(".mastery-node", { timeout: 10000 });
  if (await page.locator(".mastery-tree-wires line").count() < 1) throw new Error("Mastery prerequisite links are missing");
  fs.mkdirSync(path.dirname(skillsOut), { recursive: true });
  await page.screenshot({ path: skillsOut });
  await page.click("#skills-passive-tab");
  await page.waitForSelector("#skills-loadout-items .skillbook-row", { timeout: 5000 });
  await page.waitForSelector("#powers .passive-detail");
  await page.locator("#skills-loadout-items .skillbook-row").first().click();
  if (!await page.locator("#powers .passive-detail-title").textContent()) throw new Error("Passive skill detail is missing");
  await page.screenshot({ path: passiveOut });
  const passiveToggle = page.locator("#skills-loadout-items .skillbook-row .inv-btn:not([disabled])").first();
  if (await passiveToggle.count() > 0) {
    await passiveToggle.click();
    await page.click("#skills-save");
    await page.waitForFunction(() => document.querySelector("#skills-status")?.textContent === "Saved", undefined, { timeout: 10000 });
  }
  await page.click("#skills-active-tab");
  const assignSkill = page.locator("#skills-loadout-items .skillbook-row .inv-btn:not([disabled])", { hasText: "Assign" }).first();
  if (await assignSkill.count() > 0) {
    await assignSkill.click();
    await page.click("#skills-save");
    await page.waitForFunction(() => document.querySelector("#skills-status")?.textContent === "Saved", undefined, { timeout: 10000 });
  }
  const beforeRank = await page.textContent("#powers-points");
  await page.click(".mastery-node:not([disabled])");
  await page.waitForTimeout(1200);
  const afterRank = await page.textContent("#powers-points");

  const status = await page.textContent("#boot-status").catch(() => "");
  const level = await page.textContent("#hud-level").catch(() => "");
  const monsters = await page.textContent("#hud-alive").catch(() => "");
  const hp = await page.textContent("#hud-hp-text").catch(() => "");
  const kills = await page.textContent("#hud-kills").catch(() => "");
  const xp = await page.textContent("#hud-xp-text").catch(() => "");
  const stats = await page.textContent("#hud-stats").catch(() => "");
  await page.screenshot({ path: out });

  const realErrors = errors.filter((e) => !e.includes("favicon"));
  console.log(`status: ${status}`);
  console.log(`level: ${level}  monsters: ${monsters}  hp: ${hp}  kills: ${kills}`);
  console.log(`xp: ${xp}`);
  console.log(`stats: ${stats}`);
  console.log(`world rows: ${worldRows}  checkpoint selectors: ${checkpointSelectors}  town monsters: ${townMonsters}  town NPC: ${townSmithTitle}`);
  console.log(`town screenshot: ${townOut}`);
  console.log(`equipped rows: ${equippedRows}  loot feed: ${lootRows}`);
  console.log(`bag screenshot: ${bagOut}`);
  console.log(`attributes screenshot: ${attrsOut}`);
  console.log(`skills screenshot: ${skillsOut}`);
  console.log(`passive screenshot: ${passiveOut}`);
  console.log(`mastery rank: ${beforeRank} -> ${afterRank}`);
  console.log(`screenshot: ${out}`);
  if (!level || monsters === "0") throw new Error("HUD did not report a populated world");
  if (worldRows < 1 || checkpointSelectors < 1) throw new Error("W05 world/checkpoint selector did not load");
  if (townMonsters !== "0") throw new Error("town transition did not suspend hostile monsters");
  if (townSmithTitle !== "Blacksmith") throw new Error("Town Blacksmith spawn did not open its NPC window");
  if (equippedRows < 1) throw new Error("M2 equip did not mark an item equipped");
  if (itemArtTiles !== initialBagCards)
    throw new Error("inventory item icon mapping is incomplete");
  if (beforeRank === afterRank) throw new Error("M3 mastery allocation did not change the rank");
  if (realErrors.length) throw new Error("browser errors:\n" + realErrors.join("\n"));
  console.log("PASS m2 smoke");
} finally {
  await browser.close();
}
