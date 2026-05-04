import { Page } from '@playwright/test';

/** Attaches pageerror + console.error listeners and returns the collected messages. */
export function collectJsErrors(page: Page): string[] {
  const errors: string[] = [];
  page.on('pageerror', (err) => errors.push(err.message));
  page.on('console', (msg) => {
    if (msg.type() === 'error') errors.push(`[console.error] ${msg.text()}`);
  });
  return errors;
}
