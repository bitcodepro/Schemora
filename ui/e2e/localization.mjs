import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { chromium } from 'playwright-core';
import { openApp } from './support.mjs';
const [sessionPath, fixtureRoot, reportPath, screenshotDirectory, executablePath, mode = 'full'] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionPath, 'utf8'));
const dictionaries = {};
for (const file of ['shell', 'graph', 'recipes', 'common', 'backend-host', 'backend-graph', 'backend-concat'])
  Object.assign(
    dictionaries,
    JSON.parse(await fs.readFile(fileURLToPath(new URL('../src/locales/' + file + '.json', import.meta.url)), 'utf8')),
  );
const report =
  mode === 'persist'
    ? JSON.parse(await fs.readFile(reportPath, 'utf8'))
    : { success: false, checks: [], screenshots: [], pageErrors: [], externalRequests: [], locales: ['en', 'ru', 'uk'] };
report.success = false;
let browser,
  page,
  locale = 'en';
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
  return page.evaluate(
    async ({ url, method, body, token }) => {
      const response = await fetch(url, {
        method,
        headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': token },
        ...(body === undefined ? {} : { body: JSON.stringify(body) }),
      });
      return { status: response.status, body: await response.json() };
    },
    { url, method, body, token: session.token },
  );
}
async function language(next) {
  await page.locator('[data-testid=language-selector]').first().selectOption(next);
  locale = next;
  await poll(async () => (await page.locator('html').getAttribute('lang')) === next, 'Document language did not update');
  await poll(async () => (await api('/api/settings/language')).body.value === next, 'Language was not saved');
}
async function named(label, name) {
  const select = page.getByRole('combobox', { name: L(label), exact: true });
  await poll(async () => (await select.locator('option').allTextContents()).some(text => text.includes(name)), 'Missing option ' + name);
  const value = await select
    .locator('option')
    .evaluateAll((options, name) => options.find(option => option.textContent.includes(name))?.value, name);
  await select.selectOption(value);
}
async function shot(name) {
  await fs.mkdir(screenshotDirectory, { recursive: true });
  const target = path.join(screenshotDirectory, name + '.png');
  await page.screenshot({ path: target, fullPage: false });
  report.screenshots.push(target);
}
try {
  browser = await chromium.launch({ headless: true, executablePath });
  const context = await browser.newContext({ viewport: { width: 1500, height: 1100 }, locale: 'ru-RU' });
  page = await context.newPage();
  page.on('pageerror', error => report.pageErrors.push(error.message));
  page.on('request', request => {
    const url = request.url();
    if (!url.startsWith(session.origin + '/') && !url.startsWith('blob:') && !url.startsWith('data:'))
      report.externalRequests.push(new URL(url).origin);
  });
  await openApp(page, session.launchUrl);
  if (mode === 'persist') {
    locale = 'uk';
    await check('Ukrainian preference survives host restart and a fresh browser context', async () => {
      await poll(async () => (await page.locator('html').getAttribute('lang')) === 'uk', 'Restart lost server locale');
      assert.equal(await page.locator('[data-testid=language-selector]').first().inputValue(), 'uk');
      assert.equal((await api('/api/settings/language')).body.value, 'uk');
      await page.getByRole('button', { name: L('Workspace'), exact: true }).waitFor();
    });
    await check('rapid language changes persist the last choice without stale startup overwrite', async () => {
      const picker = page.locator('[data-testid=language-selector]').first();
      await picker.selectOption('en');
      await picker.selectOption('ru');
      await picker.selectOption('uk');
      await poll(async () => (await api('/api/settings/language')).body.value === 'uk', 'Last rapid selection was not saved');
      await page.reload({ waitUntil: 'networkidle' });
      assert.equal(await page.locator('html').getAttribute('lang'), 'uk');
    });
  } else {
    await check('English is the fresh-install default even when the browser prefers Russian', async () => {
      assert.equal(await page.locator('html').getAttribute('lang'), 'en');
      assert.equal(await page.locator('[data-testid=language-selector]').first().inputValue(), 'en');
      await page.getByRole('button', { name: 'Workspace', exact: true }).waitFor();
      const texts = await page.locator('h1,nav button,.workspace-editor label,.workspace-editor button').allTextContents();
      assert.ok(
        texts.every(text => !/[А-Яа-яІіЇїЄє]/.test(text)),
        texts.join('\n'),
      );
      await shot('01-default-english');
    });
    await check('all three locales switch navigation, headings, settings and aria labels live', async () => {
      for (const next of ['ru', 'uk', 'en']) {
        await language(next);
        await page.getByRole('button', { name: L('Settings and tools'), exact: true }).click();
        await page.getByRole('heading', { name: L('Interface language'), exact: true }).waitFor();
        assert.equal(await page.locator('.settings-card [data-testid=language-selector]').inputValue(), next);
        await page.getByRole('button', { name: L('Workspace'), exact: true }).click();
        await page.getByRole('textbox', { name: L('Name #2'), exact: true }).waitFor();
      }
    });
    await check('unsupported language values are rejected without changing the saved choice', async () => {
      for (const value of ['de', 'en-US', '', null, 1, { name: 'uk' }]) {
        const result = await api('/api/settings/language', 'PUT', { value });
        assert.equal(result.status, 400, JSON.stringify(value));
        assert.equal(result.body.code, 'LANGUAGE_INVALID');
      }
      assert.equal((await api('/api/settings/language')).body.value, 'en');
    });
    let workspaceId;
    await check('localized workspace creation preserves user-authored Unicode names', async () => {
      await page.getByRole('textbox', { name: L('Name #2'), exact: true }).fill('Локалізація / Localization');
      await page.getByRole('textbox', { name: L('Root folders · one per line'), exact: true }).fill(fixtureRoot);
      const response = page.waitForResponse(
        response => response.url().endsWith('/api/workspaces') && response.request().method() === 'POST',
      );
      await page.getByRole('button', { name: L('Create workspace'), exact: true }).click();
      workspaceId = (await (await response).json()).id;
      const scan = page.waitForResponse(response => response.url().endsWith('/scan') && response.request().method() === 'POST');
      await page.getByRole('button', { name: L('Full Rescan'), exact: true }).click();
      const job = await (await scan).json();
      await poll(async () => ['completed', 'partial'].includes((await api('/api/jobs/' + job.id)).body.status), 'Scan failed');
      await poll(async () => (await page.locator('.database-card').count()) === 1, 'Schema missing');
      await language('uk');
      assert.equal((await api('/api/workspaces')).body[0].name, 'Локалізація / Localization');
      await language('en');
    });
    await check('relationship labels update in the existing graph and preserve the chosen JOIN path', async () => {
      await page.getByRole('button', { name: L('Relationships and paths'), exact: true }).click();
      await poll(async () => (await page.locator('.react-flow__node').count()) === 2, 'Graph missing');
      await named('Исходная таблица', 'Transactions');
      await named('Целевая таблица', 'Accounts');
      await page.getByRole('button', { name: L('Find paths'), exact: true }).click();
      const preview = page.getByRole('textbox', { name: L('JOIN SQL preview'), exact: true });
      await preview.waitFor();
      const sql = await preview.inputValue();
      for (const next of ['uk', 'ru', 'en']) {
        await language(next);
        assert.equal(await page.getByRole('textbox', { name: L('JOIN SQL preview'), exact: true }).inputValue(), sql);
        const edge = page.locator('.react-flow__edge-text');
        await poll(
          async () => (await edge.allTextContents()).some(text => text.includes(L('Declared FK'))),
          'Graph edge label froze in old locale',
        );
      }
    });
    const name = 'Неперекладене ім’я ✓';
    const description = 'Описание пользователя / опис користувача <tag>';
    let recipe;
    await check('language changes preserve an unsaved recipe and its SQL editor model', async () => {
      await page.getByRole('button', { name: L('SQL recipes'), exact: true }).click();
      await page.getByText(L('Create a recipe from a table and column'), { exact: true }).click();
      await named('Таблица для рецепта', 'Transactions');
      await page.getByRole('combobox', { name: L('ID list column'), exact: true }).selectOption('ExternalId');
      await page.getByRole('button', { name: L('Create draft'), exact: true }).click();
      await page.locator('.sql-editor .monaco-editor').waitFor();
      await page.getByRole('textbox', { name: L('Recipe name'), exact: true }).fill(name);
      await page.getByRole('textbox', { name: L('Recipe description'), exact: true }).fill(description);
      const sql = await page.locator('.sql-editor .view-lines').innerText();
      for (const next of ['ru', 'uk', 'en']) {
        await language(next);
        assert.equal(await page.getByRole('textbox', { name: L('Recipe name'), exact: true }).inputValue(), name);
        assert.equal(await page.getByRole('textbox', { name: L('Recipe description'), exact: true }).inputValue(), description);
        assert.equal(await page.locator('.sql-editor .view-lines').innerText(), sql);
      }
      const response = page.waitForResponse(
        response => /\/workspaces\/[^/]+\/recipes$/.test(new URL(response.url()).pathname) && response.request().method() === 'POST',
      );
      await page.getByRole('button', { name: L('Save recipe'), exact: true }).click();
      recipe = await (await response).json();
      assert.equal(recipe.definition.name, name);
      assert.equal(recipe.definition.description, description);
      await page.getByText(L('Saved'), { exact: true }).waitFor();
    });
    await check('existing parser diagnostics retranslate while ID text and raw invalid values stay intact', async () => {
      await page.getByRole('button', { name: L('Inputs and SQL'), exact: true }).click();
      await page.getByRole('textbox', { name: L('ID list: {0}', 'Ids'), exact: true }).fill('not-an-int');
      const response = page.waitForResponse(response => response.url().endsWith('/api/inputs/parse'));
      await page.getByRole('button', { name: L('Validate all IDs'), exact: true }).click();
      assert.equal((await (await response).json()).statistics.invalid, 1);
      const key = 'Value must be a signed {0} within its SQL Server range, using ASCII digits.';
      for (const next of ['en', 'ru', 'uk']) {
        await language(next);
        assert.equal(await page.getByRole('textbox', { name: L('ID list: {0}', 'Ids'), exact: true }).inputValue(), 'not-an-int');
        assert.equal(await page.locator('.input-error-table p').innerText(), L(key, 'int'));
        assert.ok((await page.locator('.input-error-table').innerText()).includes('not-an-int'));
      }
    });
    await check('valid runtime IDs and generated SQL survive language changes without browser or recipe persistence', async () => {
      const ids = '99887766\n99887766\n88776655';
      await page.getByRole('textbox', { name: L('ID list: {0}', 'Ids'), exact: true }).fill(ids);
      const parsed = page.waitForResponse(response => response.url().endsWith('/api/inputs/parse'));
      await page.getByRole('button', { name: L('Validate all IDs'), exact: true }).click();
      assert.equal((await (await parsed).json()).statistics.valid, 3);
      const rendered = page.waitForResponse(response => response.url().endsWith('/render'));
      await page.getByRole('button', { name: L('Generate SQL'), exact: true }).click();
      const result = await (await rendered).json();
      assert.ok(result.sql.includes('99887766'));
      await page.locator('.recipe-output .monaco-editor').waitFor();
      const output = await page.locator('.recipe-output .view-lines').innerText();
      for (const next of ['en', 'ru', 'uk']) {
        await language(next);
        assert.equal(await page.getByRole('textbox', { name: L('ID list: {0}', 'Ids'), exact: true }).inputValue(), ids);
        assert.equal(await page.locator('.recipe-output .view-lines').innerText(), output);
      }
      const storage = await page.evaluate(() =>
        JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage }, url: location.href }),
      );
      assert.ok(!storage.includes('99887766'));
      assert.ok(!JSON.stringify((await api('/api/workspaces/' + workspaceId + '/recipes')).body).includes('99887766'));
      await shot('02-ukrainian-runtime');
      await page.reload({ waitUntil: 'networkidle' });
      assert.equal(await page.locator('html').getAttribute('lang'), 'uk');
      assert.equal(await page.locator('[data-testid=language-selector]').first().inputValue(), 'uk');
    });
    await check('all localized assets remain local and browser execution has no page errors', async () => {
      assert.deepEqual(report.pageErrors, []);
      assert.deepEqual(report.externalRequests, []);
    });
  }
  assert.deepEqual(report.pageErrors, []);
  assert.deepEqual(report.externalRequests, []);
  report.success = true;
} catch (error) {
  report.error = String(error?.message || error).replaceAll(session.token, '[redacted]');
  console.error(report.error);
  if (page)
    try {
      await shot('localization-failure');
    } catch {}
  process.exitCode = 1;
} finally {
  if (browser) await browser.close();
  report.at = new Date().toISOString();
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2), 'utf8');
}
