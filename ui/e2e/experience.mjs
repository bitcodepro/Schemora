import { chromium } from 'playwright-core';
import fs from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import assert from 'node:assert/strict';
import { openApp, reloadApp } from './support.mjs';
const [sessionFile, reportFile] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionFile, 'utf8'));
const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
const checks = [];
const errors = [];
try {
  const page = await browser.newPage({ viewport: { width: 1400, height: 1000 }, acceptDownloads: true });
  page.on('pageerror', e => errors.push(e.message));
  await openApp(page, session.launchUrl);
  await page.getByRole('button', { name: 'Load tutorial project', exact: true }).click();
  await page
    .getByRole('combobox', { name: 'Workspace', exact: true })
    .locator('option')
    .filter({ hasText: 'Schemora tutorial' })
    .waitFor({ state: 'attached' });
  checks.push('Tutorial creates scanned workspace');
  await page.getByRole('searchbox', { name: 'Saved navigation filter', exact: true }).fill('Orders');
  await page.getByRole('button', { name: 'Orders · Table', exact: true }).click();
  await page.getByRole('heading', { name: 'Recipe dependency graph', exact: true }).waitFor();
  await page.getByRole('button', { name: 'Orders for customers', exact: true }).waitFor();
  checks.push('Hierarchical object navigation and clickable recipe dependency graph');
  const downloadPromise = page.waitForEvent('download');
  await page.getByRole('button', { name: 'Export schema documentation', exact: true }).click();
  const download = await downloadPromise;
  const html = await fs.readFile(await download.path(), 'utf8');
  assert.ok(html.includes('Customers') && html.includes('Relationship evidence'));
  checks.push('Standalone HTML documentation download');
  await reloadApp(page);
  await page.waitForFunction(() => document.querySelector('input[type=search]')?.value === 'Orders');
  assert.equal(await page.getByRole('searchbox', { name: 'Saved navigation filter', exact: true }).inputValue(), 'Orders');
  checks.push('Navigation filter survives reload');
  await page.evaluate(() => {
    document.documentElement.style.zoom = '2';
  });
  await page.getByRole('button', { name: 'Export schema documentation', exact: true }).focus();
  assert.equal(
    await page.getByRole('button', { name: 'Export schema documentation', exact: true }).evaluate(e => document.activeElement === e),
    true,
  );
  await page.keyboard.press('Tab');
  assert.ok(await page.evaluate(() => document.activeElement.tagName !== 'BODY'));
  checks.push('Keyboard focus and 200 percent zoom');
  await page.screenshot({ path: reportFile.replace('.json', '.png'), fullPage: false });
  await page.evaluate(() => {
    document.documentElement.style.zoom = '1';
  });
  await page.getByRole('button', { name: 'SQL recipes', exact: true }).click();
  await page.locator('.recipe-catalog-list button').filter({ hasText: 'Period: new customers' }).click();
  await page.getByRole('textbox', { name: 'Value: From', exact: true }).fill('2026-10-01');
  await page.getByRole('textbox', { name: 'Value: To', exact: true }).fill('2026-11-01');
  assert.equal(await page.getByRole('textbox', { name: 'ID list: From', exact: true }).count(), 0);
  await page.getByRole('button', { name: 'Validate values', exact: true }).click();
  const periodRender = page.waitForResponse(response => response.url().endsWith('/render'));
  await page.getByRole('button', { name: 'Generate SQL', exact: true }).click();
  const periodSql = (await (await periodRender).json()).sql;
  assert.match(periodSql, /c\.CreatedAt >= CONVERT\(date, N'2026-10-01', 23\)/);
  assert.match(periodSql, /c\.CreatedAt < CONVERT\(date, N'2026-11-01', 23\)/);
  checks.push('Single-value date inputs render typed literals');
  await page.getByRole('button', { name: 'Last month', exact: true }).click();
  const periodFrom = await page.getByRole('textbox', { name: 'Value: From', exact: true }).inputValue();
  const periodTo = await page.getByRole('textbox', { name: 'Value: To', exact: true }).inputValue();
  assert.match(periodFrom, /^\d{4}-\d{2}-01$/);
  assert.match(periodTo, /^\d{4}-\d{2}-01$/);
  assert.ok(periodFrom < periodTo, 'Last month must end after it starts');
  checks.push('One click fills a whole period into From and To');
  await page.getByRole('button', { name: 'Workspace', exact: true }).click();
  for (const [language, title] of [
    ['ru', 'Навигатор рабочей области'],
    ['uk', 'Навігатор робочої області'],
    ['en', 'Workspace navigator'],
  ]) {
    await page.locator('[data-testid=language-selector]').first().selectOption(language);
    await page.getByRole('heading', { name: title, exact: true }).waitFor();
  }
  checks.push('New navigation labels switch across EN RU UK');
  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+K');
  const palette = page.getByRole('dialog', { name: 'Search the workspace', exact: true });
  await palette.getByRole('combobox', { name: 'Search the workspace', exact: true }).fill('Orders');
  await palette.getByRole('option').filter({ hasText: 'dbo.Orders' }).first().waitFor();
  await palette.getByRole('option').filter({ hasText: 'dbo.Orders' }).first().click();
  await palette.waitFor({ state: 'detached' });
  await page.getByRole('heading', { name: 'Explore your data structure', exact: true }).waitFor();
  checks.push('Ctrl+K search opens a schema object in the catalog');
  await page.getByRole('button', { name: 'SQL recipes', exact: true }).click();
  for (const recipe of ['Period: new customers', 'Orders for customers']) {
    await page.locator('body').click({ position: { x: 5, y: 5 } });
    await page.keyboard.press('Control+K');
    await palette.getByRole('combobox', { name: 'Search the workspace', exact: true }).fill(recipe.split(':')[0]);
    await palette.getByRole('option').filter({ hasText: recipe }).first().click();
    await palette.waitFor({ state: 'detached' });
    await page.locator('.recipe-editor-heading').getByRole('heading', { name: recipe, exact: true }).waitFor();
    assert.equal(await page.getByRole('button', { name: 'Inputs and SQL', exact: true }).getAttribute('class'), 'selected');
  }
  checks.push('Ctrl+K opens another recipe on an already open recipes page, on its run tab');
  assert.match(new URL(page.url()).hash, /^#\/recipes\?.*recipe=/);
  await reloadApp(page);
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Orders for customers', exact: true }).waitFor();
  assert.equal(await page.getByRole('button', { name: 'Inputs and SQL', exact: true }).getAttribute('class'), 'selected');
  await page.goBack();
  await page.getByRole('heading', { name: 'Explore your data structure', exact: true }).waitFor();
  checks.push('The address keeps the page, recipe and tab across a reload, and Back returns to the previous page');
  const emptyRoot = await fs.mkdtemp(path.join(os.tmpdir(), 'schemora-empty-'));
  try {
    const created = await fetch(session.origin + '/api/workspaces', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': session.token, Origin: session.origin },
      body: JSON.stringify({ name: 'Empty workspace', roots: [emptyRoot], excludes: [] }),
    }).then(response => response.json());
    const workspaceSelect = page.getByRole('combobox', { name: 'Workspace', exact: true });
    const tutorialId = await workspaceSelect.inputValue();
    await reloadApp(page);
    await page.locator('.table-item').first().waitFor();
    await workspaceSelect.selectOption(created.id);
    await page.locator('.empty-state').waitFor();
    assert.equal(await page.locator('.table-item').count(), 0);
    await workspaceSelect.selectOption(tutorialId);
    await page.locator('.table-item').first().waitFor();
    checks.push('Switching to a workspace without a snapshot clears the catalog instead of showing the previous model');
  } finally {
    await fs.rm(emptyRoot, { recursive: true, force: true });
  }
  assert.deepEqual(errors, []);
  await fs.writeFile(reportFile, JSON.stringify({ success: true, checks, pageErrors: errors }, null, 2));
} finally {
  await browser.close();
}
