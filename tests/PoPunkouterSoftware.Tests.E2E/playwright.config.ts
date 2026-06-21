import { defineConfig, devices } from '@playwright/test';
import * as path from 'path';
import * as fs from 'fs';

const isDev = process.env['ASPNETCORE_ENVIRONMENT'] !== 'Production';
const baseURL = process.env['BASE_URL'] ?? 'http://127.0.0.1:8000';
const dotnetCommand = process.env['DOTNET_PATH']
  ?? (process.platform === 'win32'
    ? '"C:\\Program Files\\dotnet\\x64\\dotnet.exe"'
    : 'dotnet');

const localAppData = process.env.LOCALAPPDATA
  || process.env.localappdata
  || (process.env.USERPROFILE ? path.join(process.env.USERPROFILE, 'AppData', 'Local') : '');

const fallbackChromium = path.join(
  localAppData,
  'ms-playwright',
  'chromium-1217',
  'chrome-win64',
  'chrome.exe'
);
const executablePath = fs.existsSync(fallbackChromium) ? fallbackChromium : undefined;
console.log('--- PLAYWRIGHT_CONFIG_TILT_PATH:', executablePath);

// If BASE_URL is already set (server managed externally), skip the webServer block.
// Otherwise Playwright starts (and stops) the Blazor API server automatically.
const externalServer = !!process.env['BASE_URL'];

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  retries: 1,
  reporter: [['list'], ['json', { outputFile: '../../TESTRESULTS/playwright-results.json' }]],
  use: {
    baseURL,
    // Headed in Dev so failures are visually debuggable; headless in CI/Production
    headless: !isDev,
    screenshot: 'only-on-failure',
    video: 'off',
    trace: 'off',
    javaScriptEnabled: true,
    executablePath,
  },
  // Auto-start the Blazor server when no external BASE_URL is provided.
  // Set reuseExistingServer:true so a manually-started server is reused.
  webServer: externalServer ? undefined : {
    command: `${dotnetCommand} run --project ${path.resolve(__dirname, '../../PoPunkouterSoftware/PoPunkouterSoftware.csproj')} --no-build`,
    url: baseURL,
    reuseExistingServer: true,
    timeout: 60_000,
    env: { ASPNETCORE_URLS: baseURL },
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'], channel: 'chrome' },
    },
    {
      name: 'mobile-chrome',
      use: { ...devices['Pixel 5'], channel: 'chrome' },
    },
  ],
  timeout: 30_000,
  expect: { timeout: 10_000 },
});
