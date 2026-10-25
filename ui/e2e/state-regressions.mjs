import { chromium } from 'playwright-core';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { openApp, reloadApp } from './support.mjs';

const [sessionFile, reportFile] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionFile, 'utf8'));
const dictionaries = {};
for (const file of await fs.readdir(new URL('../src/locales/', import.meta.url)))
  if (file.endsWith('.json'))
    Object.assign(dictionaries, JSON.parse(await fs.readFile(new URL('../src/locales/' + file, import.meta.url), 'utf8')));
// Catalog keys are English; a Russian or Ukrainian label used in a test still finds its entry.
const entry = key => dictionaries[key] || Object.values(dictionaries).find(item => item.ru === key || item.uk === key);
const L = key => entry(key)?.en || key;
const checks = [],
  errors = [];
async function api(url, method = 'GET', body) {
  const response = await fetch(session.origin + url, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': session.token, Origin: session.origin },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  const text = await response.text();
  assert.ok(response.ok, text);
  return text ? JSON.parse(text) : null;
}
async function scan(workspace) {
  const job = await api('/api/workspaces/' + workspace.id + '/scan', 'POST', { mode: 'full' });
  const deadline = Date.now() + 30000;
  while (Date.now() < deadline) {
    const result = await api('/api/jobs/' + job.id);
    if (['completed', 'partial'].includes(result.status)) return;
    assert.notEqual(result.status, 'failed', result.error);
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error('Synthetic scan timed out');
}
const a = await api('/api/tutorial', 'POST');
const b = await api('/api/tutorial', 'POST');
for (const [tutorial, marker] of [
  [a, 'AlphaMarker'],
  [b, 'BetaMarker'],
]) {
  await fs.appendFile(
    path.join(tutorial.workspace.roots[0], 'Schema.sql'),
    `\nGO\nCREATE TABLE dbo.${marker}(Id int NOT NULL PRIMARY KEY);\nGO\n`,
  );
  await scan(tutorial.workspace);
}
const catalog = await api('/api/workspaces/' + a.workspace.id + '/schema-state');
assert.deepEqual(Object.keys(catalog), ['databases']);
assert.equal(catalog.databases[0].workspaceId, a.workspace.id);
const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
let page;
try {
  page = await browser.newPage({ viewport: { width: 1500, height: 1100 } });
  page.on('pageerror', error => errors.push(error.message));
  await openApp(page, session.launchUrl);
  await page.locator('[data-testid=language-selector]').first().selectOption('en');
  const button = key => page.getByRole('button', { name: L(key), exact: true });
  const workspace = page.getByRole('combobox', { name: L('Workspace'), exact: true });
  const shown = id => page.locator('.schema-browser[data-database-id="' + id + '"]');
  async function openRecipe(name) {
    await page.locator('body').click({ position: { x: 5, y: 5 } });
    await page.keyboard.press('Control+K');
    const palette = page.getByRole('dialog', { name: L('Search the workspace'), exact: true });
    await palette.getByRole('combobox').fill(name);
    await palette.getByRole('option').filter({ hasText: name }).first().click();
    await palette.waitFor({ state: 'detached' });
    await page.locator('.recipe-editor-heading').getByRole('heading', { name, exact: true }).waitFor();
  }
  await workspace.selectOption(a.workspace.id);
  await button('Schema catalog').click();
  await shown(a.database.id).waitFor();
  await page.locator('.table-item').filter({ hasText: 'AlphaMarker' }).click();
  const schemaAddress = page.url();
  await button('SQL recipes').click();
  await openRecipe('Period: new customers');
  await button('Versions and lineage').click();
  await page.waitForFunction(() => location.hash.includes('tab=history'));
  await button('Workspace').click();
  await workspace.selectOption(b.workspace.id);
  await button('SQL recipes').click();
  await openRecipe('Orders for customers');
  await button('Workspace').click();
  await page.goBack();
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Orders for customers', exact: true }).waitFor();
  assert.equal(await workspace.inputValue(), b.workspace.id);
  await page.goBack();
  await page.getByRole('heading', { name: L('Projects, schemas, and changes'), exact: true }).waitFor();
  await page.goBack();
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Period: new customers', exact: true }).waitFor();
  assert.equal(await workspace.inputValue(), a.workspace.id);
  assert.equal(await button('Versions and lineage').getAttribute('class'), 'selected');
  await page.goBack();
  await shown(a.database.id).waitFor();
  await page.locator('.table-item.selected').filter({ hasText: 'AlphaMarker' }).waitFor();
  assert.equal(page.url(), schemaAddress);
  await page.goForward();
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Period: new customers', exact: true }).waitFor();
  assert.equal(await workspace.inputValue(), a.workspace.id);
  await page.goForward();
  await page.goForward();
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Orders for customers', exact: true }).waitFor();
  assert.equal(await workspace.inputValue(), b.workspace.id);
  checks.push('Back and Forward restore workspace, database, table, recipe and tab');

  await button('Definition').click();
  await page.getByRole('textbox', { name: L('Recipe name'), exact: true }).fill('Unsaved route draft');
  const editingAddress = page.url();
  let dismissed = false;
  page.once('dialog', async dialog => {
    dismissed = true;
    await dialog.dismiss();
  });
  await page.goBack();
  await page.waitForFunction(address => location.href === address, editingAddress);
  assert.equal(dismissed, true);
  assert.equal(await page.getByRole('textbox', { name: L('Recipe name'), exact: true }).inputValue(), 'Unsaved route draft');
  page.once('dialog', dialog => dialog.accept());
  await page.goBack();
  await page.getByRole('heading', { name: L('Projects, schemas, and changes'), exact: true }).waitFor();
  checks.push('Dismissing Back keeps the address and unsaved draft; accepting it traverses history');

  await workspace.selectOption(a.workspace.id);
  const stateA = await api('/api/workspaces/' + a.workspace.id + '/schema-state');
  let release, received;
  const blocked = new Promise(resolve => {
    release = resolve;
  });
  const arrived = new Promise(resolve => {
    received = resolve;
  });
  const delayed = '**/api/schema/snapshots/' + stateA.databases[0].currentSnapshotId;
  await page.route(delayed, async route => {
    const response = await route.fetch();
    received();
    await blocked;
    await route.fulfill({ response }).catch(() => {});
  });
  await button('Schema catalog').click();
  await arrived;
  await workspace.selectOption(b.workspace.id);
  await shown(b.database.id).waitFor();
  release();
  await page.unroute(delayed);
  await page.locator('.table-item').filter({ hasText: 'BetaMarker' }).waitFor();
  assert.equal(await shown(a.database.id).count(), 0);
  checks.push('A delayed snapshot response cannot replace the newly selected workspace');

  await page.locator('.table-item').filter({ hasText: 'BetaMarker' }).click();
  await fs.appendFile(
    path.join(b.workspace.roots[0], 'Schema.sql'),
    '\nGO\nCREATE TABLE dbo.LiveMarker(Id int NOT NULL PRIMARY KEY);\nGO\n',
  );
  await scan(b.workspace);
  await page.locator('.table-item').filter({ hasText: 'LiveMarker' }).waitFor({ timeout: 15000 });
  await page.locator('.table-item.selected').filter({ hasText: 'BetaMarker' }).waitFor();
  checks.push('The open catalog follows a new background snapshot while preserving the selected table');

  await page.locator('body').click({ position: { x: 5, y: 5 } });
  await page.keyboard.press('Control+K');
  const palette = page.getByRole('dialog', { name: L('Search the workspace'), exact: true });
  await palette.getByRole('combobox').fill('Orders');
  const filters = palette.locator('.palette-kinds button');
  await filters.nth(1).waitFor();
  await page.keyboard.press('Tab');
  assert.equal(await filters.first().evaluate(element => element === document.activeElement), true);
  await page.keyboard.press('Tab');
  await page.keyboard.press('Enter');
  assert.equal(await filters.nth(1).getAttribute('aria-pressed'), 'true');
  await filters.first().evaluate(element => element.click());
  assert.equal(await filters.first().getAttribute('aria-pressed'), 'true');
  await page.keyboard.press('Shift+Tab');
  assert.equal(await palette.evaluate(element => element.contains(document.activeElement)), true);
  await page.keyboard.press('Escape');
  await palette.waitFor({ state: 'detached' });
  checks.push('Palette filters support Tab, Enter and click activation, retain focus and close with Escape');

  let hub = '\nGO\nCREATE TABLE dbo.ZZZHub(Id int NOT NULL PRIMARY KEY);\nGO\n';
  for (let i = 0; i < 80; i++)
    hub += `CREATE TABLE dbo.Neighbor${i}(Id int NOT NULL PRIMARY KEY, HubId int REFERENCES dbo.ZZZHub(Id));\nGO\n`;
  await fs.appendFile(path.join(b.workspace.roots[0], 'Schema.sql'), hub);
  await scan(b.workspace);
  await page.locator('.table-item').filter({ hasText: 'ZZZHub' }).waitFor({ timeout: 15000 });
  await page.locator('.table-item').filter({ hasText: 'ZZZHub' }).click();
  await page.locator('.view-tabs button').nth(1).click();
  await page.locator('.graph-node').filter({ hasText: 'ZZZHub' }).waitFor();
  assert.equal(await page.locator('.graph-node').count(), 60);
  await reloadApp(page);
  await page.locator('.graph-node').filter({ hasText: 'ZZZHub' }).waitFor();
  checks.push('A high-degree focus remains in the sixty-table overview and its view survives reload');

  await workspace.selectOption(a.workspace.id);
  await button('SQL recipes').click();
  await openRecipe('Orders for customers');
  await button('Definition').click();
  await page.getByRole('textbox', { name: L('Recipe name'), exact: true }).fill('Delayed save in A');
  let releaseSave, saveArrived;
  const saveBlocked = new Promise(resolve => {
    releaseSave = resolve;
  });
  const saveReceived = new Promise(resolve => {
    saveArrived = resolve;
  });
  const saveUrl = '**/api/recipes/' + a.recipeId;
  await page.route(saveUrl, async route => {
    if (route.request().method() !== 'PUT') return route.continue();
    const response = await route.fetch();
    saveArrived();
    await saveBlocked;
    await route.fulfill({ response }).catch(() => {});
  });
  await button('Save new version').click();
  await saveReceived;
  page.once('dialog', dialog => dialog.accept());
  await workspace.selectOption(b.workspace.id);
  await page.locator('.recipe-editor-heading').getByRole('heading', { name: 'Orders for customers', exact: true }).waitFor();
  releaseSave();
  await page.unroute(saveUrl);
  await page.waitForTimeout(800);
  assert.equal(await workspace.inputValue(), b.workspace.id);
  assert.equal(await page.getByText('Delayed save in A').count(), 0);
  assert.ok(!page.url().includes(a.recipeId), 'the address names a recipe of the other workspace');
  assert.equal((await api('/api/recipes/' + a.recipeId)).definition.name, 'Delayed save in A');
  checks.push('A save that answers after a workspace switch is stored but does not replace the shown recipe');

  const history = await api('/api/databases/' + a.database.id + '/snapshots');
  const oldest = [...history].sort((x, y) => String(x.createdAt).localeCompare(String(y.createdAt)))[0];
  page.once('dialog', dialog => dialog.accept());
  await workspace.selectOption(a.workspace.id);
  await page.evaluate(
    ([ws, db, snap]) => {
      location.hash = '#/schema?' + new URLSearchParams({ ws, db, snap, view: 'objects' });
    },
    [a.workspace.id, a.database.id, oldest.id],
  );
  await reloadApp(page);
  const objectList = page.locator('.object-list button');
  await objectList.first().waitFor();
  assert.equal(await objectList.filter({ hasText: 'AlphaMarker' }).count(), 0, 'a historical snapshot lists a newer table');
  await objectList.filter({ hasText: 'dbo.Customers' }).first().click();
  await page.locator('.object-detail h3, h3').filter({ hasText: 'dbo.Customers' }).first().waitFor();
  await page.waitForFunction(() => location.hash.includes('object=') && location.hash.includes('snap='));
  const chosenAddress = page.url();
  await reloadApp(page);
  await page.locator('h3').filter({ hasText: 'dbo.Customers' }).first().waitFor();
  assert.equal(await page.locator('.object-list button').filter({ hasText: 'AlphaMarker' }).count(), 0);
  assert.equal(page.url(), chosenAddress);
  checks.push('A historical snapshot stays pinned in Objects and after reload, and a chosen object is kept in the address');
  assert.deepEqual(errors, []);
  await fs.writeFile(reportFile, JSON.stringify({ success: true, checks, pageErrors: errors }, null, 2));
} catch (error) {
  if (page) await page.screenshot({ path: reportFile + '.png', fullPage: true }).catch(() => {});
  await fs.writeFile(reportFile, JSON.stringify({ success: false, checks, error: String(error), pageErrors: errors }, null, 2));
  throw error;
} finally {
  await browser.close();
}
