import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { openApp } from './support.mjs';
const [sessionPath, fixtureRoot, reportPath, screenshotDirectory, executablePath] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionPath, 'utf8'));
const dictionaries = {};
const localeDir = fileURLToPath(new URL('../src/locales/', import.meta.url));
for (const file of await fs.readdir(localeDir))
  if (file.endsWith('.json')) Object.assign(dictionaries, JSON.parse(await fs.readFile(path.join(localeDir, file), 'utf8')));
let locale = 'en',
  browser,
  page;
const report = { success: false, checks: [], screenshots: [], pageErrors: [], externalRequests: [] };
// Catalog keys are English; a Russian or Ukrainian label used in a test still finds its entry.
const entry = key => dictionaries[key] || Object.values(dictionaries).find(item => item.ru === key || item.uk === key);
const L = (key, ...values) =>
  (entry(key)?.[locale] || key).replace(/\{(\d+)\}/g, (all, index) => (index < values.length ? String(values[index]) : all));
async function poll(action, message, timeout = 45000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await action()) return;
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  throw new Error(message);
}
async function check(name, action) {
  await action();
  report.checks.push(name);
  console.log('PASS ' + name);
}
async function api(url, method = 'GET', body) {
  const response = await fetch(session.origin + url, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': session.token, Origin: session.origin },
    ...(body === undefined ? {} : { body: JSON.stringify(body) }),
  });
  const text = await response.text();
  const result = text ? JSON.parse(text) : null;
  assert.ok(response.ok, `${method} ${url}: ${response.status} ${JSON.stringify(result)}`);
  return result;
}
async function shot(name) {
  await fs.mkdir(screenshotDirectory, { recursive: true });
  const target = path.join(screenshotDirectory, name + '.png');
  await page.screenshot({ path: target, fullPage: false });
  report.screenshots.push(target);
}
async function language(next) {
  await page.locator('[data-testid=language-selector]').first().selectOption(next);
  locale = next;
  await poll(async () => (await page.locator('html').getAttribute('lang')) === next, 'Document language did not update');
}
const button = key => page.getByRole('button', { name: L(key), exact: true });
try {
  browser = await chromium.launch({ headless: true, executablePath });
  const context = await browser.newContext({ viewport: { width: 1500, height: 1100 }, acceptDownloads: true });
  page = await context.newPage();
  page.on('pageerror', e => report.pageErrors.push(e.message));
  page.on('request', request => {
    const url = request.url();
    if (!url.startsWith(session.origin + '/') && !url.startsWith('blob:') && !url.startsWith('data:'))
      report.externalRequests.push(new URL(url).origin);
  });
  const workspace = await api('/api/workspaces', 'POST', { name: 'Product v1.3 browser', roots: [fixtureRoot], excludes: [] });
  const base = '/api/workspaces/' + workspace.id;
  const job = await api(base + '/scan', 'POST', { mode: 'full' });
  await poll(async () => ['completed', 'partial'].includes((await api('/api/jobs/' + job.id)).status), 'Initial scan failed');
  const catalog = await api(base + '/catalog');
  const db = catalog.databases[0];
  const schema = await api('/api/databases/' + db.id + '/schema');
  const docs = schema.tables.find(t => t.name === 'Documents'),
    audits = schema.tables.find(t => t.name === 'Audits');
  await api(base + '/manual-relations', 'POST', {
    name: 'Artificial documented audit link',
    fromTableId: audits.id,
    toTableId: docs.id,
    fromColumns: ['DocumentId'],
    toColumns: ['DocumentId'],
    reason: 'Artificial portable browser fixture',
  });
  const recipe = await api(base + '/recipes', 'POST', {
    id: 'ignored',
    name: 'Artificial browser regression',
    description: 'Synthetic browser acceptance',
    tags: [],
    dialect: 'sqlserver',
    databaseIds: [db.id],
    version: 0,
    sqlTemplate: 'SELECT d.DocumentId FROM dbo.Documents d WHERE d.DocumentId IN ({{ids:Ids}});',
    inputs: [{ name: 'Ids', type: 'int', deduplicate: false, preserveOrder: true, strategy: 'auto' }],
    compatibilityLevel: 130,
  });
  await openApp(page, session.launchUrl);
  await check('new regression tab switches live across English Russian Ukrainian', async () => {
    await button('SQL-рецепты').click();
    await page
      .getByRole('button')
      .filter({ has: page.getByText(recipe.definition.name, { exact: true }) })
      .first()
      .click();
    for (const next of ['en', 'ru', 'uk']) {
      await language(next);
      await button('Regression fixtures').click();
      await page.getByRole('heading', { name: L('Saved regression fixtures'), exact: true }).waitFor();
      assert.ok(await button('Save artificial baseline').isDisabled());
    }
    await language('en');
  });
  await check('artificial baseline requires explicit consent and shows saved expectations', async () => {
    const fieldset = page.getByRole('group', { name: L('Create an artificial baseline'), exact: true });
    await fieldset.getByLabel(L('Fixture name'), { exact: true }).fill('Browser artificial duplicate case');
    await fieldset.getByLabel('Ids', { exact: true }).fill('2,1,2');
    await fieldset.getByLabel(L('Required SQL fragments, one per line'), { exact: true }).fill('SELECT d.DocumentId');
    assert.ok(await button('Save artificial baseline').isDisabled());
    await fieldset.getByRole('checkbox').check();
    const response = page.waitForResponse(r => r.url().endsWith('/regression-fixtures') && r.request().method() === 'POST');
    await button('Save artificial baseline').click();
    assert.equal((await response).status(), 200);
    await poll(
      async () => (await page.getByRole('heading', { name: /Browser artificial duplicate case/ }).count()) === 1,
      'Saved fixture card missing',
    );
    await page.getByText(L('Review saved artificial data and expectations'), { exact: true }).click();
    const expected = await page.locator('.recipe-history article pre').innerText();
    assert.ok(expected.includes('expectedSqlHash') && expected.includes('"duplicates": 1'));
  });
  await check('run saved regressions uses actual API and displays success', async () => {
    const response = page.waitForResponse(r => r.url().endsWith('/regression-runs') && r.request().method() === 'POST');
    await button('Run saved regressions').click();
    assert.equal((await response).status(), 200);
    await page.getByRole('heading', { name: /Browser artificial duplicate case.*Passed/ }).waitFor();
    await shot('01-regression-baseline');
  });
  await check('C# context preview and saved Release selection use static evaluation without trust', async () => {
    await button('Связи и пути').click();
    await page.getByText(L('C# project compilation context'), { exact: true }).click();
    const panel = page.locator('details').filter({ has: page.getByText(L('C# project compilation context'), { exact: true }) });
    await panel.getByLabel(L('Configuration'), { exact: true }).fill('Release');
    await panel.getByLabel(L('Target framework'), { exact: true }).fill('net10.0');
    assert.equal(await panel.getByRole('checkbox').isChecked(), false);
    const response = page.waitForResponse(r => r.url().endsWith('/csharp-context/preview'));
    await panel.getByRole('button', { name: L('Preview context'), exact: true }).click();
    const preview = await (await response).json();
    assert.equal(preview.files.length, 2);
    assert.ok(preview.files.some(f => f.path.endsWith('ReleaseOnly.cs')));
    assert.ok(preview.fingerprint.length > 0);
    assert.ok(Array.isArray(preview.diagnostics));
    await panel.getByText(new RegExp(L('Source files') + ': 2')).waitFor();
    const saved = page.waitForResponse(r => r.url().endsWith('/csharp-context') && r.request().method() === 'PUT');
    await panel.getByRole('button', { name: L('Save context'), exact: true }).click();
    assert.equal((await saved).status(), 200);
    const projects = await api(base + '/csharp-context');
    assert.equal(projects[0].settings.configuration, 'Release');
    assert.equal(projects[0].settings.trustedEvaluation, false);
    await shot('02-csharp-context');
  });
  await check('settings new labels switch live across all supported locales', async () => {
    await button('Настройки и инструменты').click();
    for (const next of ['ru', 'uk', 'en']) {
      await language(next);
      for (const key of ['Portable workspace configuration', 'Memory and history', 'Diagnostics and support report'])
        await page.getByRole('heading', { name: L(key), exact: true }).waitFor();
      await button('Export configuration v2').waitFor();
      await button('Preview history cleanup').waitFor();
      await button('Download diagnostic report').waitFor();
    }
  });
  let portable;
  await check('portable v2 download includes manual assertion and artificial baseline', async () => {
    const download = page.waitForEvent('download');
    await button('Export configuration v2').click();
    const file = await download;
    portable = JSON.parse(await fs.readFile(await file.path(), 'utf8'));
    assert.equal(portable.formatVersion, 2);
    assert.ok(portable.assertions.some(a => a.name === 'Artificial documented audit link'));
    assert.equal(portable.regressionFixtures.length, 1);
    assert.equal(portable.regressionFixtures[0].artificialInputs.Ids, '2,1,2');
    assert.ok(!JSON.stringify(portable).includes(fixtureRoot));
  });
  await check('portable import requires explicit mapping preview and stages assertions for review', async () => {
    await page.locator('input[type=file][accept=".json,application/json"]').setInputFiles({
      name: 'artificial-configuration.json',
      mimeType: 'application/json',
      buffer: Buffer.from(JSON.stringify(portable)),
    });
    const mapping = page.locator('.configuration-preview select').first();
    await mapping.selectOption(db.id);
    for (const [index, project] of (portable.projects || []).entries())
      await page
        .locator('.configuration-preview select')
        .nth(portable.databases.length + index)
        .selectOption(project.id);
    const previewResponse = page.waitForResponse(r => r.url().endsWith('/configuration/preview'));
    await button('Preview import').click();
    const preview = await (await previewResponse).json();
    assert.equal(preview.canImport, true);
    assert.ok(preview.items.some(i => i.status === 'review-required'));
    const importResponse = page.waitForResponse(r => r.url().endsWith('/configuration/import-v2'));
    await button('Import reviewed configuration').click();
    assert.equal((await importResponse).status(), 200);
    await page.getByRole('heading', { name: L('Relationships awaiting individual review'), exact: true }).waitFor();
    await button('Confirm against current schema').waitFor();
    assert.equal((await api(base + '/configuration/pending')).length, 1);
    await button('Discard imported assertion').click();
    await poll(async () => (await api(base + '/configuration/pending')).length === 0, 'Discard did not remove pending assertion');
  });
  await check('retention policy saves and history preview requires a separate cleanup action', async () => {
    await page.getByLabel(L('Minimum snapshots per database'), { exact: true }).fill('3');
    await page.getByLabel(L('Keep history for days'), { exact: true }).fill('7');
    const saved = page.waitForResponse(r => r.url().endsWith('/storage/policy') && r.request().method() === 'PUT');
    await button('Save retention policy').click();
    assert.equal((await saved).status(), 200);
    assert.equal((await api('/api/storage/policy')).keepSnapshotsPerDatabase, 3);
    const preview = page.waitForResponse(r => r.url().endsWith('/storage/cleanup/preview'));
    await button('Preview history cleanup').click();
    const plan = await (await preview).json();
    assert.ok(plan.protectedSnapshotIds.length >= 1);
    await button('Back up and clean eligible history').waitFor();
    assert.ok(await button('Back up and clean eligible history').isDisabled());
  });
  await check('diagnostic report downloads with root paths excluded by default', async () => {
    const download = page.waitForEvent('download');
    await button('Download diagnostic report').click();
    const file = await download;
    const diagnostic = JSON.parse(await fs.readFile(await file.path(), 'utf8'));
    assert.equal(diagnostic.pathsIncluded, false);
    assert.equal(diagnostic.paths, null);
    assert.ok(!JSON.stringify(diagnostic).includes(session.token));
    assert.ok(!JSON.stringify(diagnostic).includes(fixtureRoot));
    await shot('02-settings');
  });
  await check('new UI workflows have no browser page errors or external requests', async () => {
    assert.deepEqual(report.pageErrors, []);
    assert.deepEqual(report.externalRequests, []);
  });
  report.success = true;
} catch (error) {
  report.error = String(error?.message || error).replaceAll(session.token, '[redacted]');
  console.error(report.error);
  if (page)
    try {
      await shot('product-v13-failure');
    } catch {}
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  report.at = new Date().toISOString();
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2), 'utf8');
}
