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
const page = await browser.newPage({ viewport: { width: 1440, height: 900 } });
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
  await page.click("#boot-enter");
  await page.waitForSelector("#hud:not([hidden])", { timeout: 20000 });
  await page.waitForTimeout(6000);
  await page.click("#hud-skill");
  await page.waitForTimeout(1500);
  await page.click("#hud-skill-3");
  await page.waitForTimeout(6500);

  // M2: open the bag, equip the first unequipped item, and confirm the server marked it.
  await page.click("#hud-bag");
  await page.waitForTimeout(1000);
  await page.waitForSelector(".inv-row", { timeout: 10000 });
  const equipButtons = await page.$$(".inv-row:not(.equipped) .inv-btn");
  if (equipButtons.length > 0) {
    await equipButtons[0].click();
    await page.waitForTimeout(1500);
  }
  const equippedRows = await page.$$eval(".inv-row.equipped", (rows) => rows.length);
  const lootRows = await page.$$eval(".loot-item", (rows) => rows.length);

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
  console.log(`equipped rows: ${equippedRows}  loot feed: ${lootRows}`);
  console.log(`screenshot: ${out}`);
  if (!level || monsters === "0") throw new Error("HUD did not report a populated world");
  if (equippedRows < 1) throw new Error("M2 equip did not mark an item equipped");
  if (realErrors.length) throw new Error("browser errors:\n" + realErrors.join("\n"));
  console.log("PASS m2 smoke");
} finally {
  await browser.close();
}
