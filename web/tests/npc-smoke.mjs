// NPC window smoke test: drive the web client against a running server + Vite dev server,
// open the Blacksmith and Merchant windows, and assert they render without browser errors.
//
// Prerequisites (see docs/web/M0_STATUS.md):
//   server: NORD_WEB_DEV=1 NORD_WEB_PORT=5080 dotnet ...
//   client: npm run dev
// Run:  npm run npc-smoke
import { fileURLToPath } from "node:url";
import path from "node:path";
import fs from "node:fs";
import { chromium } from "playwright";

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "..", "..");
const url = process.env.WEB_URL ?? "http://127.0.0.1:5173/";
const out = process.env.OUT ?? path.join(repoRoot, "tmp", "web-npc", "npc.png");

function resolveChrome() {
  if (process.env.CHROME_PATH) return process.env.CHROME_PATH;
  try {
    const bundled = chromium.executablePath();
    if (bundled && fs.existsSync(bundled)) return bundled;
  } catch {
    /* fall through */
  }
  const cache = path.join(process.env.HOME ?? "", "Library/Caches/ms-playwright");
  if (fs.existsSync(cache)) {
    for (const entry of fs.readdirSync(cache).filter((n) => n.startsWith("chromium-")).sort().reverse()) {
      const candidate = path.join(cache, entry,
        "chrome-mac-arm64/Google Chrome for Testing.app/Contents/MacOS/Google Chrome for Testing");
      if (fs.existsSync(candidate)) return candidate;
    }
  }
  return undefined;
}

fs.mkdirSync(path.dirname(out), { recursive: true });
const browser = await chromium.launch({
  executablePath: resolveChrome(),
  args: ["--use-gl=angle", "--use-angle=swiftshader", "--enable-unsafe-swiftshader"],
});
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
const errors = [];
page.on("console", (msg) => { if (msg.type() === "error") errors.push("console: " + msg.text()); });
page.on("pageerror", (err) => errors.push("pageerror: " + err.message));
page.on("requestfailed", (req) => errors.push(`requestfailed: ${req.url()} ${req.failure()?.errorText}`));
page.on("response", (res) => { if (res.status() >= 400) errors.push(`http ${res.status()}: ${res.url()}`); });

try {
  await page.goto(url, { waitUntil: "networkidle" });
  await page.waitForSelector("#boot-enter", { timeout: 15000 });
  await page.fill("#boot-name", "npc-smoke");
  await page.click("#boot-enter");
  await page.waitForSelector("#hud:not([hidden])", { timeout: 20000 });
  await page.waitForTimeout(7000); // let a few kills land so there is loot to work with

  // Blacksmith window.
  await page.click("#hud-smith");
  await page.waitForSelector("#npc:not([hidden])", { timeout: 5000 });
  const smithTabs = await page.$$eval("#npc-tabs .npc-tab", (rows) => rows.map((r) => r.textContent));
  const smithTitle = await page.textContent("#npc-title");
  await page.click("#npc-tabs .npc-tab:last-child"); // Add Socket tab
  await page.waitForTimeout(300);
  const addSocketHint = await page.textContent("#npc-hint");

  // Merchant window.
  await page.click("#npc-close");
  await page.click("#hud-merchant");
  await page.waitForSelector("#npc:not([hidden])", { timeout: 5000 });
  await page.waitForTimeout(1200);
  const merchantTitle = await page.textContent("#npc-title");
  const merchantRows = await page.$$eval("#npc-body .npc-row", (rows) => rows.length);
  const buyButtons = await page.$$eval("#npc-body [data-buy]", (rows) => rows.length);
  const merchantTabs = await page.$$eval("#npc-tabs .npc-tab", (rows) => rows.map((r) => r.textContent));
  await page.click("#npc-close");

  // Attribute panel.
  await page.click("#hud-attrs");
  await page.waitForSelector("#attrs:not([hidden])", { timeout: 5000 });
  await page.waitForTimeout(1000);
  const attrRows = await page.$$eval("#attrs-items .inv-row", (rows) => rows.length);
  const attrPoints = await page.textContent("#attrs-points");
  await page.click("#attrs-close");

  // Offerings window (M3): four blessings, each with four offering sizes.
  await page.click("#hud-blessings");
  await page.waitForSelector("#blessings:not([hidden])", { timeout: 5000 });
  await page.waitForSelector("#blessings-items .bless-row", { timeout: 5000 });
  const blessingRows = await page.$$eval("#blessings-items .bless-row", (rows) => rows.length);
  const offeringButtons = await page.$$eval("#blessings-items .inv-btn", (rows) => rows.length);
  const blessingOpals = await page.textContent("#blessings-opals");
  await page.click("#blessings-close");

  // Niflheim portal window (M3).
  await page.click("#hud-portal");
  await page.waitForSelector("#portal:not([hidden])", { timeout: 5000 });
  await page.waitForTimeout(600);
  const portalText = await page.textContent("#portal-body");
  const portalAction = await page.textContent("#portal-action");
  await page.click("#portal-close");

  // Loadout window: choose up to 6 active / 3 passive from the class pool, then save.
  await page.click("#hud-loadout");
  await page.waitForSelector("#loadout:not([hidden])", { timeout: 5000 });
  await page.waitForSelector("#loadout-items .loadout-row", { timeout: 5000 });
  const loadoutRows = await page.$$eval("#loadout-items .loadout-row", (rows) => rows.length);
  const loadoutPoints = await page.textContent("#loadout-points");
  const equipButton = await page.$("#loadout-items .loadout-row .inv-btn:not([disabled])");
  if (equipButton) await equipButton.click();
  await page.click("#loadout-save");
  await page.waitForTimeout(1000);
  const loadoutStatus = await page.textContent("#loadout-status");

  await page.screenshot({ path: out });

  console.log(`smith: ${smithTitle} tabs=[${smithTabs.join(", ")}]`);
  console.log(`add-socket hint: ${addSocketHint}`);
  console.log(`merchant: ${merchantTitle} tabs=[${merchantTabs.join(", ")}] products=${merchantRows} buyButtons=${buyButtons}`);
  console.log(`attributes: rows=${attrRows} points=${attrPoints}`);
  console.log(`offerings: blessings=${blessingRows} buttons=${offeringButtons} opals=${blessingOpals}`);
  console.log(`portal: action=${portalAction} text=${(portalText ?? "").slice(0, 60)}`);
  console.log(`loadout: rows=${loadoutRows} points=${loadoutPoints} status=${loadoutStatus}`);
  console.log(`screenshot: ${out}`);
  const realErrors = errors.filter((e) => !e.includes("favicon"));
  if (smithTabs.length < 6) throw new Error("Blacksmith should expose its six tabs");
  if (merchantRows < 1) throw new Error("Merchant catalog did not render");
  if (buyButtons < 2) throw new Error("Merchant Buy tab should offer silver and opal purchases");
  if (merchantTabs.length < 3) throw new Error("Merchant should expose Buy/Trade/Set tabs");
  if (attrRows !== 7) throw new Error("Attribute panel should list the seven attributes");
  if (blessingRows !== 4) throw new Error("Offerings should list the four Aesir blessings");
  if (offeringButtons !== 16) throw new Error("Each blessing should offer four offering sizes");
  if (!portalText || !portalText.includes("portal")) throw new Error("Portal window did not render a status");
  if (loadoutRows < 6) throw new Error("Loadout should offer at least six active skills");
  if (loadoutStatus !== "Saved") throw new Error(`Loadout save did not apply (status: ${loadoutStatus})`);
  if (realErrors.length) throw new Error("browser errors:\n" + realErrors.join("\n"));
  console.log("PASS npc smoke");
} finally {
  await browser.close();
}
