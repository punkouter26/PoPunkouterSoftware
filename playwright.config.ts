import { defineConfig, devices } from '@playwright/test';

export default defineConfig({
  testDir: './e2e',
  fullyParallel: true,
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 2 : 0,
  workers: process.env.CI ? 1 : undefined,
  reporter: process.env.CI ? [['github'], ['html', { open: 'never' }]] : 'html',
  use: {
    baseURL: process.env.TEST_URL || 'http://localhost:5000',
    trace: 'on-first-retry',
    screenshot: 'only-on-failure',
    // Headed in Dev (non-CI), headless in CI
    headless: !!process.env.CI,
  },
  webServer: {
    command: `dotnet run --project ${process.env.PROJECT_PATH || 'PoPunkouterSoftware/PoPunkouterSoftware.csproj'} --launch-profile http`,
    url: 'http://localhost:5000',
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
    stdout: 'pipe',
    stderr: 'pipe',
  },
  projects: [
    {
      name: 'chromium',
      use: { ...devices['Desktop Chrome'] },
    },
  ],
  timeout: 30_000,
});
