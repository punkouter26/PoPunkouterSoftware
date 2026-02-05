import { test, expect } from '@playwright/test';

const BASE_URL = process.env.TEST_URL || 'https://witty-smoke-014bc370f.2.azurestaticapps.net';

test.describe('PoPunkouterSoftware Static Web App', () => {
  
  test('Homepage loads correctly', async ({ page }) => {
    await page.goto(BASE_URL);
    
    // Check page title
    await expect(page).toHaveTitle(/PoPunkouterSoftware/);
    
    // Check main heading
    await expect(page.locator('h1')).toContainText('Welcome to Punkouter Software');
    
    // Check navigation exists
    await expect(page.locator('nav')).toBeVisible();
    
    // Check footer exists
    await expect(page.locator('footer')).toBeVisible();
    await expect(page.locator('footer')).toContainText('Punkouter Software');
  });

  test('Navigation links work', async ({ page }) => {
    await page.goto(BASE_URL);
    
    // Check all nav links exist
    const navLinks = ['Home', 'Our Team', 'Web Apps', 'Phone Apps', 'Contact'];
    for (const link of navLinks) {
      await expect(page.getByRole('link', { name: link })).toBeVisible();
    }
  });

  test('Our Team page loads', async ({ page }) => {
    await page.goto(`${BASE_URL}/OurTeam.html`);
    
    await expect(page).toHaveTitle(/About Us/);
    await expect(page.locator('h2')).toContainText('Meet Our Team');
  });

  test('Web Apps page loads with app cards', async ({ page }) => {
    await page.goto(`${BASE_URL}/OurWebApps.html`);
    
    await expect(page).toHaveTitle(/Web Apps/);
    await expect(page.locator('h1')).toContainText('Web Apps');
    
    // Check sort dropdown exists
    await expect(page.locator('#sort-select')).toBeVisible();
    
    // Wait for apps to load and check count
    await expect(page.locator('.app-card')).toHaveCount(16, { timeout: 10000 });
  });

  test('Phone Apps page loads', async ({ page }) => {
    await page.goto(`${BASE_URL}/OurPhoneApps.html`);
    
    await expect(page).toHaveTitle(/Phone Apps/);
    await expect(page.locator('h1')).toContainText('Phone Apps');
  });

  test('Contact page loads', async ({ page }) => {
    await page.goto(`${BASE_URL}/Contact.html`);
    
    await expect(page).toHaveTitle(/Contact/);
    await expect(page.locator('h1')).toContainText('Contact Us');
    await expect(page.getByRole('link', { name: 'Email Us' })).toBeVisible();
  });

  test('Privacy Policy page loads', async ({ page }) => {
    await page.goto(`${BASE_URL}/PrivacyPolicy.html`);
    
    await expect(page).toHaveTitle(/Privacy Policy/);
    await expect(page.locator('h1').first()).toContainText('Privacy Policy');
  });

  test('Web Apps sorting works', async ({ page }) => {
    await page.goto(`${BASE_URL}/OurWebApps.html`);
    
    // Wait for apps to load
    await expect(page.locator('.app-card')).toHaveCount(16, { timeout: 10000 });
    
    // Change sort to Status
    await page.selectOption('#sort-select', 'status');
    
    // First card should be active
    const firstCard = page.locator('.app-card').first();
    await expect(firstCard).toHaveAttribute('data-status', 'active');
  });

  test('No JavaScript errors on homepage', async ({ page }) => {
    const errors: string[] = [];
    page.on('pageerror', (error) => {
      errors.push(error.message);
    });
    
    await page.goto(BASE_URL);
    await page.waitForLoadState('networkidle');
    
    // Filter out expected App Insights and analytics errors (non-critical telemetry)
    const criticalErrors = errors.filter(e => 
      !e.toLowerCase().includes('appinsights') &&
      !e.toLowerCase().includes('applicationinsights') &&
      !e.toLowerCase().includes('trackpageview')
    );
    expect(criticalErrors).toHaveLength(0);
  });
});
