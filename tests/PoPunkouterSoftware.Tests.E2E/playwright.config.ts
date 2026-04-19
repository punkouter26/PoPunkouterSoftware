import { defineConfig, devices } from '@playwright/test';

const isDev = process.env['ASPNETCORE_ENVIRONMENT'] !== 'Production';

export default defineConfig({
  testDir: './tests',
  fullyParallel: false,
  retries: 1,
  reporter: [['list'], ['json', { outputFile: '../../TESTRESULTS/playwright-results.json' }]],
  use: {
    baseURL: 'http://localhost:5200',
    // Headed in Dev so failures are visually debuggable; headless in CI/Production
    headless: !isDev,
    screenshot: 'only-on-failure',
    video: 'off',
    trace: 'off',
    javaScriptEnabled: true,
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
    {
      name: 'mobile-chrome',
      use: { ...devices['Pixel 5'] },
    },
  ],
  timeout: 30_000,
  expect: { timeout: 10_000 },
});
