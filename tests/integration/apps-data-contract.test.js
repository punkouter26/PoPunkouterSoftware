import { describe, it, expect } from 'vitest';
import fs from 'fs';
import path from 'path';
import { fileURLToPath } from 'url';
import { APP_STATUS, APP_CATEGORIES } from '../../PoPunkouterSoftware/wwwroot/js/constants/app-constants.js';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const appsJsonPath = path.resolve(__dirname, '../../PoPunkouterSoftware/wwwroot/data/apps.json');

function loadApps() {
  const raw = fs.readFileSync(appsJsonPath, 'utf8');
  const parsed = JSON.parse(raw);
  return parsed.apps;
}

describe('apps.json data contract', () => {
  it('has a non-empty apps array', () => {
    const apps = loadApps();
    expect(Array.isArray(apps)).toBe(true);
    expect(apps.length).toBeGreaterThan(0);
  });

  it('ensures each app has required fields and valid enums', () => {
    const apps = loadApps();
    const validStatuses = new Set(Object.values(APP_STATUS));
    const validCategories = new Set(Object.values(APP_CATEGORIES));

    for (const app of apps) {
      expect(typeof app.id).toBe('string');
      expect(app.id.length).toBeGreaterThan(0);
      expect(typeof app.name).toBe('string');
      expect(app.name.length).toBeGreaterThan(0);
      expect(typeof app.description).toBe('string');
      expect(app.description.length).toBeGreaterThan(0);
      expect(validCategories.has(app.category)).toBe(true);
      expect(validStatuses.has(app.status)).toBe(true);
      expect(Array.isArray(app.technologies)).toBe(true);
      expect(app.technologies.length).toBeGreaterThan(0);
      expect(typeof app.url).toBe('string');
      expect(/^https?:\/\//i.test(app.url)).toBe(true);
    }
  });

  it('has unique ids and names', () => {
    const apps = loadApps();
    const ids = new Set();
    const names = new Set();

    for (const app of apps) {
      expect(ids.has(app.id)).toBe(false);
      expect(names.has(app.name)).toBe(false);
      ids.add(app.id);
      names.add(app.name);
    }
  });
});
