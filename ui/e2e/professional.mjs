import fs from 'node:fs/promises';
import assert from 'node:assert/strict';
import { chromium } from 'playwright-core';
import path from 'node:path';
import { openApp, reloadApp } from './support.mjs';
const [sessionPath, reportPath] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionPath, 'utf8'));
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
const tutorial = await api('/api/tutorial', 'POST');
const source = path.join(tutorial.workspace.roots[0], 'Schema.sql');
await fs.appendFile(source, '\nGO\nCREATE TABLE dbo.Staging(Value int NULL);');
const job = await api('/api/workspaces/' + tutorial.workspace.id + '/scan', 'POST', { mode: 'full' });
for (let i = 0; i < 300; i++) {
  const state = await api('/api/jobs/' + job.id);
  if (['completed', 'partial'].includes(state.status)) break;
  if (state.status === 'failed') throw Error('Scan failed');
  await new Promise(r => setTimeout(r, 100));
}
const backupResponse = await fetch(session.origin + '/api/storage/backup', {
  method: 'POST',
  headers: { 'X-Schemora-Token': session.token, Origin: session.origin },
});
assert.ok(backupResponse.ok);
const backup = path.join(tutorial.workspace.roots[0], 'browser-backup.sqlite');
await fs.writeFile(backup, Buffer.from(await backupResponse.arrayBuffer()));
const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
let page;
try {
  page = await browser.newPage({ viewport: { width: 1450, height: 1000 } });
  page.on('pageerror', e => errors.push(e.message));
  const button = name => page.getByRole('button', { name, exact: true });
  await openApp(page, session.launchUrl);
  await button('Workspace quality').click();
  await page.getByRole('combobox', { name: 'Workspace', exact: true }).selectOption(tutorial.workspace.id);
  const finding = page.locator('article').filter({ has: page.getByText('TABLE_WITHOUT_PRIMARY_KEY', { exact: true }) });
  await finding.getByRole('heading', { name: 'dbo.Staging', exact: true }).waitFor();
  assert.ok(await finding.getByRole('button', { name: 'Mark as reviewed', exact: true }).isDisabled());
  await finding
    .getByRole('textbox', { name: 'Review reason', exact: true })
    .fill('Synthetic staging table intentionally has no primary key.');
  await finding.getByRole('button', { name: 'Mark as reviewed', exact: true }).click();
  await finding.waitFor({ state: 'detached' });
  await page.getByRole('checkbox', { name: 'Show reviewed findings', exact: true }).check();
  await finding.getByRole('button', { name: 'Reopen finding', exact: true }).click();
  await finding.getByRole('textbox', { name: 'Review reason', exact: true }).waitFor();
  checks.push('Quality review requires rationale and can be reopened');
  await button('Analysis, search and recovery').click();
  await page.getByText('Analysis provenance', { exact: true }).click();
  await button('Check analysis freshness').click();
  await page.getByText('Recorded inputs are current', { exact: false }).waitFor();
  await fs.appendFile(source, '\n-- browser freshness test');
  await button('Check analysis freshness').click();
  await page.getByText('Analysis needs refresh', { exact: false }).waitFor();
  checks.push('Provenance detects edited input before rescanning');
  await page.getByText('Unified workspace search', { exact: true }).click();
  await page.getByRole('searchbox', { name: 'Search all objects and recipes', exact: true }).fill('Orders');
  await page.getByRole('combobox', { name: 'Search kind', exact: true }).selectOption('table');
  await button('Search workspace').click();
  await button('dbo.Orders').click();
  await page.getByRole('heading', { name: 'dbo.Orders', exact: true }).waitFor();
  checks.push('Unified object search navigates to the selected table');
  await button('Workspace quality').click();
  await button('Analysis, search and recovery').click();
  await page.getByText('Recipe collections and batch validation', { exact: true }).click();
  await page.getByRole('textbox', { name: 'Collection name', exact: true }).fill('Browser collection');
  await page.getByRole('checkbox', { name: 'Orders for customers', exact: true }).check();
  await button('Save collection').click();
  await page
    .getByRole('combobox', { name: 'Saved collection', exact: true })
    .locator('option')
    .filter({ hasText: 'Browser collection' })
    .waitFor({ state: 'attached' });
  await button('Validate selected recipes').click();
  await page.getByText('Batch validation finished: 1', { exact: false }).waitFor();
  checks.push('Collection saves selected recipes and runs batch validation');
  const collectionSql = page.getByRole('region', { name: 'SQL for the whole collection', exact: true });
  await collectionSql.getByRole('textbox', { name: 'Collection input Ids', exact: true }).fill('7, 8');
  await collectionSql.getByRole('button', { name: 'Generate collection SQL', exact: true }).click();
  await collectionSql.getByText('Orders for customers · v1', { exact: true }).waitFor();
  await collectionSql.getByRole('button', { name: 'Download file', exact: true }).waitFor();
  const [saved] = (await api('/api/workspaces/' + tutorial.workspace.id + '/recipe-collections')).collections;
  const rendered = await api('/api/workspaces/' + tutorial.workspace.id + '/recipe-collections/' + saved.id + '/render', 'POST', {
    inputText: { Ids: '7, 8' },
  });
  assert.ok(rendered.sql.includes('-- 1/1: Orders for customers (v1)'));
  checks.push('A saved collection renders one SQL script from inputs entered once');
  await page.getByText('Catalog health and recovery', { exact: true }).click();
  await button('Check catalog integrity').click();
  await page.getByText('Catalog integrity verified', { exact: false }).waitFor();
  const automaticBackups = page.getByRole('region', { name: 'Automatic backups', exact: true });
  assert.equal(await automaticBackups.getByRole('checkbox', { name: 'Copy the catalog on a schedule', exact: true }).isChecked(), true);
  await automaticBackups.getByRole('button', { name: 'Back up now', exact: true }).click();
  await automaticBackups
    .locator('code')
    .filter({ hasText: /^auto-/ })
    .first()
    .waitFor();
  checks.push('Automatic catalog backups are on by default and can be taken on demand');
  await page.getByRole('textbox', { name: 'Backup SQLite path', exact: true }).fill(backup);
  await button('Preview restore').click();
  await page.getByText('No version migration required', { exact: false }).waitFor();
  assert.ok(await button('Restore and restart').isDisabled());
  await page
    .getByRole('checkbox', { name: 'I reviewed the backup and approve replacing the catalog and restarting Schemora.', exact: true })
    .check();
  assert.equal(await button('Restore and restart').isDisabled(), false);
  await page
    .getByRole('checkbox', { name: 'I reviewed the backup and approve replacing the catalog and restarting Schemora.', exact: true })
    .uncheck();
  checks.push('Recovery preview shows catalog contents and requires explicit restart consent');
  await button('SQL recipes').click();
  await page.getByRole('button', { name: 'Favorite: Orders for customers', exact: true }).click();
  await page.locator('.recipe-catalog-open').filter({ hasText: 'Orders for customers' }).click();
  await page.getByRole('button', { name: 'Recent 1', exact: true }).waitFor();
  await reloadApp(page);
  await button('SQL recipes').click();
  const favorite = page.getByRole('button', { name: 'Favorite: Orders for customers', exact: true });
  await page.getByRole('button', { name: 'Favorites 1', exact: true }).click();
  assert.equal(await favorite.getAttribute('aria-pressed'), 'true');
  await page.getByRole('button', { name: 'Recent 1', exact: true }).click();
  await page.locator('.recipe-catalog-open').filter({ hasText: 'Orders for customers' }).waitFor();
  await page.getByRole('button', { name: 'All', exact: false }).first().click();
  checks.push('Favourite and recent recipes survive a reload');
  await button('Workspace quality').click();
  for (const [language, title] of [
    ['ru', 'Качество, поиск и сопровождение'],
    ['uk', 'Якість, пошук і супровід'],
    ['en', 'Workspace quality and tools'],
  ]) {
    await page.locator('[data-testid=language-selector]').first().selectOption(language);
    await page.getByRole('heading', { level: 1, name: title, exact: true }).waitFor();
  }
  checks.push('Professional tools switch live across EN RU UK');
  await button('Quality findings').click();
  await page.evaluate(() => (document.documentElement.style.zoom = '2'));
  await button('Refresh quality findings').focus();
  await page.keyboard.press('Tab');
  await page.keyboard.press('Shift+Tab');
  assert.notEqual(await button('Refresh quality findings').evaluate(e => getComputedStyle(e).outlineStyle), 'none');
  assert.ok(await button('Refresh quality findings').evaluate(e => e === document.activeElement));
  await page.screenshot({ path: reportPath.replace('.json', '.png'), fullPage: false });
  checks.push('Keyboard focus remains visible at 200 percent zoom');
  assert.deepEqual(errors, []);
  await fs.writeFile(reportPath, JSON.stringify({ success: true, checks, pageErrors: errors }, null, 2));
  console.log('PASS ' + checks.length + ' professional browser workflows');
} catch (error) {
  await page.screenshot({ path: reportPath.replace('.json', '-failure.png'), fullPage: false }).catch(() => {});
  await fs.writeFile(reportPath, JSON.stringify({ success: false, checks, pageErrors: errors, error: String(error) }, null, 2));
  throw error;
} finally {
  await browser.close();
}
