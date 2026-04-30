const { chromium } = require('playwright');
const fs = require('fs');

const screenshotDir = 'C:\\\\Users\\\\punko\\\\Downloads\\\\PoPunkouterSoftware\\\\TESTRESULTS\\\\screenshots';

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
      await page.screenshot({ path: screenshotDir + '\\\\' + name + '.png', fullPage: true });
      const bodyText = await page.locator('body').textContent().catch(() => '');
      return { page: url, status: resp && resp.status(), bodyLen: bodyText && bodyText.trim().length, jsErrors };
    } catch(e) {
      return { page: url, error: e.message };
    } finally {
      await context.close();
    }
  }

  results.push(await testPage('http://localhost:5000/', 'home', 5000));
  results.push(await testPage('http://localhost:5000/azure', 'azure', 8000));
  results.push(await testPage('http://localhost:5000/status', 'status', 5000));
  results.push(await testPage('http://localhost:5000/diag', 'diag', 5000));

  // Mobile viewport
  const ctx4 = await browser.newContext({ viewport: { width: 393, height: 852 } });
  const pg4 = await ctx4.newPage();
  try {
    await pg4.goto('http://localhost:5000/', { waitUntil: 'domcontentloaded', timeout: 15000 });
    await pg4.waitForTimeout(4000);
    await pg4.screenshot({ path: screenshotDir + '\\\\home-mobile.png', fullPage: true });
    results.push({ page: '/ (mobile)', status: 200 });
  } catch(e) { results.push({ page: '/ (mobile)', error: e.message }); }
  await ctx4.close();

  await browser.close();
  process.stdout.write(JSON.stringify(results, null, 2) + '\n');
})().catch(e => { process.stderr.write(e.stack + '\n'); process.exit(1); });
