const { chromium } = require('playwright');
const fs = require('fs');
const path = require('path');

const screenshotDir = path.join(__dirname, '..', '..', '..', 'TESTRESULTS', 'screenshots');
fs.mkdirSync(screenshotDir, { recursive: true });

(async () => {
  const browser = await chromium.launch({ headless: true });
  const results = [];

  async function testPage(url, name, waitMs) {
    const context = await browser.newContext({ viewport: { width: 1280, height: 800 } });
    const page = await context.newPage();
    const jsErrors = [];
    page.on('pageerror', e => jsErrors.push(e.message));
    try {
      const resp = await page.goto(url, { waitUntil: 'domcontentloaded', timeout: 15000 });
      await page.waitForTimeout(waitMs || 3000);
      await page.screenshot({ path: path.join(screenshotDir, name + '.png'), fullPage: true });
      const bodyText = await page.locator('body').textContent().catch(() => '');
      return { page: url, status: resp?.status(), bodyLen: bodyText?.trim().length, jsErrors };
    } catch(e) {
      return { page: url, error: e.message };
    } finally {
      await context.close();
    }
  }

  results.push(await testPage('http://localhost:5000/', 'home', 4000));
  results.push(await testPage('http://localhost:5000/azure', 'azure', 6000));
  results.push(await testPage('http://localhost:5000/status', 'status', 4000));
  results.push(await testPage('http://localhost:5000/diag', 'diag', 5000));

  // Health API
  const ctx2 = await browser.newContext();
  const pg2 = await ctx2.newPage();
  try {
    const r = await pg2.goto('http://localhost:5000/api/health', { timeout: 10000 });
    const body = await pg2.locator('body').textContent().catch(() => '');
    results.push({ page: '/api/health', status: r?.status(), body: body?.substring(0, 300) });
  } catch(e) { results.push({ page: '/api/health', error: e.message }); }
  await ctx2.close();

  // Nav links check
  const ctx3 = await browser.newContext({ viewport: { width: 1280, height: 800 } });
  const pg3 = await ctx3.newPage();
  try {
    await pg3.goto('http://localhost:5000/', { waitUntil: 'domcontentloaded', timeout: 15000 });
    await pg3.waitForTimeout(3000);
    const links = await pg3.locator('header a').allTextContents().catch(() => []);
    results.push({ page: 'nav-links', links });
  } catch(e) { results.push({ page: 'nav-links', error: e.message }); }
  await ctx3.close();

  // Mobile viewport
  const ctx4 = await browser.newContext({ viewport: { width: 393, height: 852 } });
  const pg4 = await ctx4.newPage();
  const mobileErrors = [];
  pg4.on('pageerror', e => mobileErrors.push(e.message));
  try {
    await pg4.goto('http://localhost:5000/', { waitUntil: 'domcontentloaded', timeout: 15000 });
    await pg4.waitForTimeout(3000);
    await pg4.screenshot({ path: path.join(screenshotDir, 'home-mobile.png'), fullPage: true });
    results.push({ page: '/ (mobile 393px)', status: 200, jsErrors: mobileErrors });
  } catch(e) { results.push({ page: '/ (mobile)', error: e.message }); }
  await ctx4.close();

  await browser.close();
  process.stdout.write(JSON.stringify(results, null, 2) + '\n');
})().catch(e => { process.stderr.write(e.stack + '\n'); process.exit(1); });
