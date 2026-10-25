import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import path from 'node:path';
import { openApp } from './support.mjs';
const [sessionPath, reportPath] = process.argv.slice(2),
  session = JSON.parse(await fs.readFile(sessionPath, 'utf8')),
  checks = [],
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
async function scan(id, mode = 'full') {
  const job = await api('/api/workspaces/' + id + '/scan', 'POST', { mode, trustProjects: mode === 'rebuild' });
  for (let i = 0; i < 1800; i++) {
    const state = await api('/api/jobs/' + job.id);
    if (['completed', 'partial'].includes(state.status)) {
      if (mode === 'rebuild') assert.ok(state.result?.databasesUpdated > 0, JSON.stringify(state.result));
      return state;
    }
    if (state.status === 'failed') throw Error('Scan failed');
    await new Promise(r => setTimeout(r, 100));
  }
  throw Error('Scan timed out');
}
const tutorial = await api('/api/tutorial', 'POST'),
  base = '/api/workspaces/' + tutorial.workspace.id;
await fs.appendFile(
  path.join(tutorial.workspace.roots[0], 'Schema.sql'),
  "\nGO\nCREATE TABLE dbo.OptionalChild(Id int NOT NULL PRIMARY KEY,CustomerId int NULL,CONSTRAINT FK_Optional FOREIGN KEY(CustomerId) REFERENCES dbo.Customers(CustomerId));\nGO\nCREATE PROCEDURE dbo.SearchCode AS\nSELECT N'SearchMarkerКиїв' AS City;\nGO\n",
);
await scan(tutorial.workspace.id);
const data = path.dirname(path.dirname(tutorial.workspace.roots[0])),
  backupDirectory = path.join(data, 'backups');
await fs.mkdir(backupDirectory, { recursive: true });
for (let i = 0; i < 4; i++) {
  const file = path.join(backupDirectory, 'before-cleanup-' + crypto.randomUUID().replaceAll('-', '') + '.sqlite');
  await fs.writeFile(file, 'Synthetic browser cleanup fixture');
  const old = new Date(Date.now() - (45 + i) * 86400000);
  await fs.utimes(file, old, old);
}
const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
let page;
try {
  page = await browser.newPage({ viewport: { width: 1450, height: 1000 } });
  page.on('pageerror', e => errors.push(e.message));
  const button = name => page.getByRole('button', { name, exact: true });
  await openApp(page, session.launchUrl);
  await page.keyboard.press('Tab');
  assert.equal(
    await page.getByRole('link', { name: 'Skip to main content', exact: true }).evaluate(e => e === document.activeElement),
    true,
  );
  await page.keyboard.press('Enter');
  assert.equal(await page.locator('#main-content').evaluate(e => e === document.activeElement), true);
  checks.push('Keyboard skip link reaches the main landmark');
  await page.keyboard.press('?');
  const shortcuts = page.getByRole('dialog', { name: 'Keyboard shortcuts', exact: true });
  await shortcuts.getByText('Search the workspace and jump anywhere', { exact: true }).waitFor();
  assert.equal(await shortcuts.getByRole('button', { name: 'Close', exact: true }).evaluate(e => e === document.activeElement), true);
  await page.keyboard.press('Escape');
  await shortcuts.waitFor({ state: 'detached' });
  assert.equal(await page.locator('#main-content').evaluate(e => e === document.activeElement), true);
  checks.push('? opens the keyboard shortcut list, Escape closes it and focus returns');
  await button('Workspace quality').click();
  assert.equal(await page.locator('#page-title').evaluate(e => e === document.activeElement), true);
  assert.equal(await button('Workspace quality').getAttribute('aria-current'), 'page');
  await page.getByRole('combobox', { name: 'Workspace', exact: true }).selectOption(tutorial.workspace.id);
  checks.push('Navigation announces the current page and moves focus to its heading');
  await page.getByText('Quality rule settings', { exact: true }).click();
  await button('Load quality rules').click();
  await page.getByRole('combobox', { name: 'NULLABLE_FOREIGN_KEY', exact: true }).selectOption('off');
  await button('Save quality rules').click();
  await page.getByRole('status').filter({ hasText: 'Quality rules saved' }).waitFor();
  assert.equal((await api(base + '/quality/rules')).policy.rules.NULLABLE_FOREIGN_KEY, 'off');
  checks.push('Workspace quality rules save from labelled controls');
  await button('Analysis, search and recovery').click();
  await page.getByText('Unified workspace search', { exact: true }).click();
  await page.getByRole('searchbox', { name: 'Search all objects and recipes', exact: true }).fill(' searchmarkerкиїв ');
  await page.getByRole('combobox', { name: 'Search kind', exact: true }).selectOption('sqlCode');
  await button('Search workspace').click();
  await page.getByText('SearchMarkerКиїв', { exact: false }).first().waitFor();
  await page.getByRole('searchbox', { name: 'Search all objects and recipes', exact: true }).fill('unsubmitted query');
  await button('dbo.SearchCode').click();
  await page.getByRole('heading', { name: 'dbo.SearchCode', exact: true }).waitFor();
  await page.locator('.monaco-editor .view-overlays .selected-text').first().waitFor();
  checks.push('SQL code search opens the routine definition and selects the matching text');
  const editor = page.locator('.monaco-editor').first();
  await editor.locator('textarea').first().focus();
  await page.keyboard.press('Tab');
  assert.equal(await editor.evaluate(e => e.contains(document.activeElement)), false);
  checks.push('Tab leaves the SQL editor without a keyboard trap');
  await button('Workspace quality').click();
  await button('Analysis, search and recovery').click();
  await page.getByText('Managed files and disk space', { exact: true }).click();
  await button('Inspect managed files').click();
  await page.getByRole('spinbutton', { name: 'Keep files for days', exact: true }).waitFor();
  assert.equal(await button('Delete eligible files').isDisabled(), true);
  await page
    .getByRole('checkbox', { name: 'I reviewed the file cleanup preview and approve deleting eligible files.', exact: true })
    .check();
  assert.equal(await button('Delete eligible files').isDisabled(), false);
  await button('Delete eligible files').click();
  await page.getByRole('status').filter({ hasText: 'Removed files: 1' }).waitFor();
  assert.equal((await fs.readdir(backupDirectory)).length, 3);
  checks.push('Disk cleanup requires consent and keeps three recent backups');
  await page.getByText('Docker failure diagnostics', { exact: true }).click();
  await button('Load Docker diagnostics').click();
  await page.getByRole('status').filter({ hasText: 'Recorded failures: 0' }).waitFor();
  checks.push('Docker diagnostics distinguish an empty history');
  await scan(tutorial.workspace.id, 'rebuild');
  await page.getByText('Analysis provenance', { exact: true }).click();
  await button('Check analysis freshness').click();
  await page.getByText('Recorded build toolchain', { exact: true }).click();
  await page.getByText('SDK-style', { exact: true }).waitFor();
  const receipt = (await api(base + '/provenance')).latest.builds[0].toolchain;
  await page.getByText(receipt.sdkVersion, { exact: true }).waitFor();
  await page.getByText(receipt.msBuildVersion, { exact: true }).waitFor();
  checks.push('Build passport displays versions from a real SDK build');
  for (const [language, label] of [
    ['ru', 'Служебные файлы и место на диске'],
    ['uk', 'Службові файли та місце на диску'],
    ['en', 'Managed files and disk space'],
  ]) {
    await page.locator('[data-testid=language-selector]').first().selectOption(language);
    await page.getByText(label, { exact: true }).waitFor();
  }
  checks.push('New panels switch between English Russian and Ukrainian');
  async function contrast() {
    return page.locator('.professional-tools').evaluate(element => {
      const rgb = text =>
        text
          .match(/[\d.]+/g)
          .slice(0, 3)
          .map(Number);
      const light = values =>
        values
          .map(v => {
            v /= 255;
            return v <= 0.04045 ? v / 12.92 : ((v + 0.055) / 1.055) ** 2.4;
          })
          .reduce((s, v, i) => s + v * [0.2126, 0.7152, 0.0722][i], 0);
      const ratios = [];
      for (const target of [element, ...element.querySelectorAll('input:not([type=checkbox]),select,button:not(:disabled)')]) {
        if (!target.getClientRects().length) continue;
        let parent = target,
          bg = '';
        while (parent) {
          bg = getComputedStyle(parent).backgroundColor;
          if (bg !== 'rgba(0, 0, 0, 0)' && bg !== 'transparent') break;
          parent = parent.parentElement;
        }
        if (!parent) continue;
        const a = light(rgb(getComputedStyle(target).color)),
          b = light(rgb(bg));
        ratios.push((Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05));
      }
      return Math.min(...ratios);
    });
  }
  assert.ok((await contrast()) >= 4.5);
  checks.push('Dark theme new panel text and enabled controls meet 4.5 contrast');
  await button('Settings and tools').click();
  await button('Light').click();
  await button('Workspace quality').click();
  await button('Analysis, search and recovery').click();
  await page.getByText('Managed files and disk space', { exact: true }).click();
  await button('Inspect managed files').click();
  await page.getByRole('spinbutton', { name: 'Keep files for days', exact: true }).waitFor();
  assert.ok((await contrast()) >= 4.5);
  checks.push('Light theme new panel text and enabled controls meet 4.5 contrast');
  await page.evaluate(() => (document.documentElement.style.zoom = '2'));
  await button('Analysis, search and recovery').focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  assert.notEqual(await button('Analysis, search and recovery').evaluate(e => getComputedStyle(e).outlineStyle), 'none');
  await page.screenshot({ path: reportPath.replace('.json', '.png'), fullPage: false });
  const accessibility = await page.locator('main').ariaSnapshot();
  assert.match(accessibility, /Workspace quality and tools/);
  assert.match(accessibility, /Managed files and disk space/);
  checks.push('Visible keyboard focus at 200 percent and accessible landmark names');
  await page.emulateMedia({ forcedColors: 'active', reducedMotion: 'reduce' });
  assert.notEqual(await button('Analysis, search and recovery').evaluate(e => getComputedStyle(e).outlineStyle), 'none');
  checks.push('Focus remains visible with forced colors and reduced motion');
  assert.deepEqual(errors, []);
  await fs.writeFile(reportPath, JSON.stringify({ success: true, checks, pageErrors: errors, manualScreenReaderTest: false }, null, 2));
  console.log('PASS ' + checks.length + ' hardening browser workflows');
} catch (error) {
  if (page) await page.screenshot({ path: reportPath.replace('.json', '-failure.png'), fullPage: false }).catch(() => {});
  await fs.writeFile(reportPath, JSON.stringify({ success: false, checks, pageErrors: errors, error: String(error) }, null, 2));
  throw error;
} finally {
  await browser.close();
}
