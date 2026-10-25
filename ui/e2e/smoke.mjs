import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright-core';
import { openApp } from './support.mjs';

const [sessionPath, fixtureRoot, reportPath, screenshotDirectory, executablePath] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionPath, 'utf8'));
const report = {
  success: false,
  browser: 'Installed Chrome · fresh headless context',
  checks: [],
  screenshots: [],
  errors: [],
  externalRequests: [],
  testedAt: new Date().toISOString(),
};
let browser;
let page;
const pageErrors = [];
async function check(name, action) {
  await action();
  report.checks.push(name);
  console.log('PASS ' + name);
}
async function shown(locator, timeout = 45000) {
  await locator.waitFor({ state: 'visible', timeout });
}
async function poll(test, message, timeout = 45000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await test()) return;
    await new Promise(resolve => setTimeout(resolve, 150));
  }
  throw new Error(message);
}
async function screenshot(name) {
  const target = path.join(screenshotDirectory, name + '.png');
  await page.screenshot({ path: target, fullPage: false, timeout: 10000 });
  report.screenshots.push(target);
}
async function selectNamed(label, name) {
  const select = page.getByRole('combobox', { name: label, exact: true });
  await poll(async () => (await select.locator('option').allTextContents()).some(text => text.includes(name)), 'Option missing: ' + name);
  const option = await select
    .locator('option')
    .evaluateAll((options, target) => options.find(option => option.textContent.includes(target))?.value, name);
  assert.ok(option);
  await select.selectOption(option);
  return option;
}
async function api(url) {
  return page.evaluate(
    async ({ url, token }) => {
      const response = await fetch(url, { headers: { 'X-Schemora-Token': token } });
      if (!response.ok) throw new Error('HTTP ' + response.status + ' on ' + url);
      return response.json();
    },
    { url, token: session.token },
  );
}
async function scan(buttonName) {
  const response = page.waitForResponse(response => response.url().endsWith('/scan') && response.request().method() === 'POST');
  await page.getByRole('button', { name: buttonName, exact: true }).click();
  const job = await (await response).json();
  await poll(
    async () => {
      const state = await api('/api/jobs/' + job.id);
      if (['failed', 'cancelled'].includes(state.status)) throw new Error('Scan ' + state.status + ': ' + state.error);
      return ['completed', 'partial'].includes(state.status);
    },
    'UI scan timed out',
    120000,
  );
  await poll(
    async () =>
      (await page.locator('.database-card').count()) === 1 &&
      !(await page.getByRole('button', { name: 'Быстрое обновление', exact: true }).isDisabled()),
    'UI catalog did not refresh',
  );
}
try {
  await fs.mkdir(screenshotDirectory, { recursive: true });
  browser = await chromium.launch({ headless: true, executablePath, args: ['--no-first-run', '--disable-default-apps'] });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1050 },
    locale: 'ru-RU',
    permissions: ['clipboard-read', 'clipboard-write'],
  });
  page = await context.newPage();
  page.on('pageerror', error => pageErrors.push(error.message));
  page.on('request', request => {
    const url = request.url();
    if (!url.startsWith(session.origin + '/') && !url.startsWith('blob:') && !url.startsWith('data:'))
      report.externalRequests.push(new URL(url).origin);
  });
  await openApp(page, session.launchUrl);
  await page.locator('[data-testid=language-selector]').first().selectOption('ru');
  await poll(async () => (await page.locator('html').getAttribute('lang')) === 'ru', 'Russian locale did not activate');
  await check('launch token consumed and fragment removed', async () => {
    // The fragment now carries only the page route; the launch token must be gone from it.
    assert.equal(new URL(page.url()).hash, '#/workspace');
    assert.equal(await page.evaluate(() => Boolean(sessionStorage.getItem('schemora.session'))), true);
  });
  await check('create workspace through root picker', async () => {
    await page.getByRole('textbox', { name: 'Название', exact: true }).fill('UI acceptance');
    await page.getByRole('textbox', { name: /Корневые папки/ }).fill(fixtureRoot);
    await page.getByRole('button', { name: 'Создать область', exact: true }).click();
    await shown(page.getByRole('button', { name: 'Полное сканирование', exact: true }));
  });
  await check('full scan discovers synthetic SQL project and schema', async () => {
    await scan('Полное сканирование');
    assert.ok((await page.locator('.database-card').innerText()).includes('UiDemo'));
    await screenshot('01-workspace');
  });
  await check('interactive graph and trusted path show physical FK', async () => {
    await page.getByRole('button', { name: 'Связи и пути', exact: true }).click();
    await shown(page.locator('.react-flow'));
    await poll(async () => (await page.locator('.react-flow__node').count()) === 2, 'Graph nodes missing');
    await selectNamed('Исходная таблица', 'Transactions');
    await selectNamed('Целевая таблица', 'Accounts');
    await page.getByRole('button', { name: 'Найти пути', exact: true }).click();
    const preview = page.getByRole('textbox', { name: 'Предварительный JOIN SQL', exact: true });
    await shown(preview);
    assert.match(await preview.inputValue(), /JOIN/);
    await screenshot('02-graph');
  });
  await check('graph exports as PNG and SVG files', async () => {
    const png = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Экспорт PNG', exact: true }).click();
    const pngFile = await fs.readFile(await (await png).path());
    assert.equal(pngFile.subarray(1, 4).toString('ascii'), 'PNG');
    assert.ok(pngFile.length > 2000, 'PNG export is unexpectedly small');
    const svg = page.waitForEvent('download');
    await page.getByRole('button', { name: 'Экспорт SVG', exact: true }).click();
    const svgDownload = await svg;
    assert.equal(svgDownload.suggestedFilename(), 'schemora-graph.svg');
    // The browser decodes the data: URL while saving, so the file holds the SVG markup itself.
    const svgText = await fs.readFile(await svgDownload.path(), 'utf8');
    assert.match(svgText, /^<svg[\s>]/);
    assert.ok(svgText.includes('Transactions') && svgText.includes('Accounts'), 'SVG export lacks the table names');
  });
  await check('graph colours tables by schema and offers compact cards', async () => {
    await page.getByRole('button', { name: 'Цвет по схеме', exact: true }).click();
    await shown(page.getByRole('list', { name: 'Схемы', exact: true }));
    assert.equal(await page.locator('.flow-table-card.by-schema').count(), 2);
    await page.getByRole('button', { name: 'Компактные карточки', exact: true }).click();
    await poll(async () => (await page.locator('.flow-table-card.compact').count()) === 2, 'Compact cards missing');
    assert.equal(await page.locator('.flow-table-card .flow-table-columns').count(), 0);
    await page.getByRole('button', { name: 'Компактные карточки', exact: true }).click();
    await page.getByRole('button', { name: 'Цвет по схеме', exact: true }).click();
    await poll(async () => (await page.locator('.flow-table-card .flow-table-columns').count()) === 2, 'Full cards did not return');
  });
  await check('table and column wizard creates typed recipe draft', async () => {
    await page.getByRole('button', { name: 'SQL-рецепты', exact: true }).click();
    await shown(page.getByText('Создать рецепт по таблице и полю', { exact: true }));
    await page.getByText('Создать рецепт по таблице и полю', { exact: true }).click();
    await selectNamed('Таблица для рецепта', 'Transactions');
    await page.getByRole('combobox', { name: 'Поле для списка ID', exact: true }).selectOption('ExternalId');
    await page.getByRole('button', { name: 'Создать черновик', exact: true }).click();
    await shown(page.locator('.sql-editor .monaco-editor'));
    const save = page.getByRole('button', { name: 'Сохранить рецепт', exact: true });
    const response = page.waitForResponse(
      response => /\/api\/workspaces\/[^/]+\/recipes$/.test(new URL(response.url()).pathname) && response.request().method() === 'POST',
    );
    await save.click();
    const result = await (await response).json();
    assert.equal(result.definition.inputs[0].type, 'int');
    assert.ok(['Valid', 'Warning'].includes(result.validation.status), JSON.stringify(result.validation));
    report.recipeId = result.definition.id;
    report.workspaceId = result.workspaceId;
    await shown(page.getByText('Сохранено', { exact: true }));
  });
  const ids = Array.from({ length: 10000 }, (_, index) => String(901234567 + index)).join('\n');
  await check('invalid rows block SQL generation with row diagnostics', async () => {
    await page.getByRole('button', { name: 'Входы и SQL', exact: true }).click();
    await page.getByRole('textbox', { name: 'Список ID: Ids', exact: true }).fill('901234567\nnot-an-int');
    const response = page.waitForResponse(response => response.url().endsWith('/api/inputs/parse'));
    await page.getByRole('button', { name: 'Проверить все ID', exact: true }).click();
    const parsed = await (await response).json();
    assert.equal(parsed.statistics.invalid, 1);
    await shown(page.locator('.input-error-table'));
    assert.equal(await page.getByRole('button', { name: 'Сформировать SQL', exact: true }).isDisabled(), true);
  });
  await check('ten thousand IDs parse, render, and copy real standalone SQL', async () => {
    await screenshot('03-before-paste');
    console.log('Pasting10k');
    await page.evaluate(value => navigator.clipboard.writeText(value), ids);
    const idsInput = page.getByRole('textbox', { name: 'Список ID: Ids', exact: true });
    await idsInput.focus();
    await page.keyboard.press('ControlOrMeta+A');
    await page.keyboard.press('ControlOrMeta+V');
    await poll(async () => (await idsInput.inputValue()).length === ids.length, 'Clipboard paste did not populate10k IDs');
    console.log('Pasted10k');
    const parsedResponse = page.waitForResponse(response => response.url().endsWith('/api/inputs/parse'));
    await page.getByRole('button', { name: 'Проверить все ID', exact: true }).click();
    const parsed = await (await parsedResponse).json();
    assert.equal(parsed.statistics.total, 10000);
    assert.equal(parsed.statistics.valid, 10000);
    assert.equal(parsed.statistics.invalid, 0);
    const renderResponse = page.waitForResponse(response => response.url().endsWith('/render'));
    await page.getByRole('button', { name: 'Сформировать SQL', exact: true }).click();
    const rendered = await (await renderResponse).json();
    assert.equal(rendered.statistics.Ids.outputCount, 10000);
    assert.match(rendered.sql, /901234567/);
    assert.match(rendered.sql, /901244566/);
    await shown(page.getByRole('button', { name: 'Скопировать', exact: true }));
    await page.getByRole('button', { name: 'Скопировать', exact: true }).click();
    await shown(page.getByText('Скопировано в буфер обмена.', { exact: true }));
    const clipboard = await page.evaluate(() => navigator.clipboard.readText());
    const normalizeNewlines = value => value.replace(/\r\n/g, '\n');
    assert.equal(normalizeNewlines(clipboard).length, normalizeNewlines(rendered.sql).length, 'Copied SQL length differs');
    assert.ok(normalizeNewlines(clipboard) === normalizeNewlines(rendered.sql), 'Copied SQL content differs');
    report.renderedCount = 10000;
    report.renderedStrategy = rendered.strategy;
    await screenshot('03-recipe-10000');
  });
  await check('runtime IDs are absent from browser storage, URL, and saved recipe', async () => {
    const stores = await page.evaluate(() => JSON.stringify({ local: { ...localStorage }, session: { ...sessionStorage } }));
    assert.ok(!stores.includes('901234567'));
    assert.ok(!page.url().includes('901234567'));
    assert.ok(!JSON.stringify(await api('/api/recipes/' + report.recipeId)).includes('901234567'));
  });
  await check('schema edit and Quick Refresh mark dependent recipe Broken', async () => {
    const schemaPath = path.join(fixtureRoot, 'Schema.sql');
    const source = await fs.readFile(schemaPath, 'utf8');
    assert.match(source, /ExternalId/);
    await fs.writeFile(schemaPath, source.replaceAll('ExternalId', 'ExternalKey'), 'utf8');
    await page.getByRole('button', { name: 'Рабочая область', exact: true }).click();
    await scan('Быстрое обновление');
    await page.getByRole('button', { name: 'Изменения', exact: true }).click();
    await shown(page.getByRole('combobox', { name: 'Рецепт для исправления', exact: true }));
    await poll(
      async () => (await page.locator('.impact-list').innerText()).includes('Несовместим'),
      'Dependent recipe was not marked Broken',
    );
    const entry = await api('/api/recipes/' + report.recipeId);
    assert.equal(entry.validation.status, 'Broken');
    await shown(page.locator('.changes-table-scroll'));
    await screenshot('04-broken-impact');
  });
  await check('AST repair preview requires explicit approval and creates compatible version', async () => {
    await selectNamed('Рецепт для исправления', 'Transactions');
    await selectNamed('Таблица для исправления', 'Transactions');
    await page.getByLabel('Старая колонка', { exact: true }).scrollIntoViewIfNeeded();
    await page.getByLabel('Старая колонка', { exact: true }).fill('ExternalId');
    await page.getByRole('combobox', { name: 'Новая колонка', exact: true }).selectOption('ExternalKey');
    await page
      .getByRole('textbox', { name: 'Основание исправления', exact: true })
      .fill('Synthetic acceptance: source column intentionally renamed; confirmed schema change.');
    await page.getByRole('button', { name: 'Создать preview', exact: true }).click();
    await shown(page.getByLabel('SQL diff предложения исправления', { exact: true }).first());
    const approve = page.getByRole('button', { name: 'Подтвердить и создать версию', exact: true });
    assert.equal(await approve.isDisabled(), true);
    const before = await api('/api/recipes/' + report.recipeId);
    assert.equal(before.definition.version, 1);
    assert.match(before.definition.sqlTemplate, /ExternalId/);
    await screenshot('05-repair-preview');
    await page.getByRole('checkbox', { name: 'Подтверждение просмотра SQL diff', exact: true }).check();
    const response = page.waitForResponse(response => response.url().endsWith('/approve'));
    await approve.click();
    const fixed = await (await response).json();
    assert.equal(fixed.definition.version, 2);
    assert.ok(['Valid', 'Warning'].includes(fixed.validation.status));
    assert.match(fixed.definition.sqlTemplate, /ExternalKey/);
    assert.ok(!fixed.definition.sqlTemplate.includes('ExternalId'));
    await shown(page.getByText('Исправление применено как версия 2.', { exact: false }));
    report.repairedVersion = 2;
  });
  await check('versions remain available and runtime ID memory is cleared on navigation', async () => {
    await page.getByRole('button', { name: 'Открыть рецепт и историю для отката', exact: true }).click();
    await shown(page.getByRole('heading', { name: 'История версий', exact: true }));
    await poll(async () => (await page.locator('.recipe-version-row').count()) === 2, 'Expected both recipe versions to load');
    await page.getByRole('button', { name: 'Входы и SQL', exact: true }).click();
    assert.equal(await page.getByRole('textbox', { name: 'Список ID: Ids', exact: true }).inputValue(), '');
  });
  await check('light theme is persisted locally and configuration has no runtime IDs', async () => {
    await page.getByRole('button', { name: 'Настройки и инструменты', exact: true }).click();
    await shown(page.getByRole('button', { name: 'Светлая', exact: true }));
    const response = page.waitForResponse(
      response => response.url().endsWith('/api/settings/theme') && response.request().method() === 'PUT',
    );
    await page.getByRole('button', { name: 'Светлая', exact: true }).click();
    assert.equal((await response).status(), 200);
    assert.equal(await page.evaluate(() => document.documentElement.dataset.theme), 'light');
    const bundle = await api('/api/workspaces/' + report.workspaceId + '/configuration');
    assert.equal(bundle.formatVersion, 1);
    assert.ok(!JSON.stringify(bundle).includes(fixtureRoot));
    assert.ok(!JSON.stringify(bundle).includes('901234567'));
    await screenshot('06-settings-light');
  });
  await check('Docker schema route requires explicit consent', async () => {
    await page.getByRole('button', { name: 'Каталог схем', exact: true }).click();
    await page.getByRole('tab', { name: 'SQL Server в Docker', exact: true }).click();
    await page.getByRole('textbox', { name: 'Путь к файлу .dacpac', exact: true }).fill(path.join(fixtureRoot, 'not-built.dacpac'));
    const button = page.getByRole('button', { name: 'Прочитать через SQL Server', exact: true });
    assert.equal(await button.isDisabled(), true);
    await page.getByRole('checkbox', { name: /Создать временный SQL Server контейнер/ }).check();
    assert.equal(await button.isDisabled(), false);
  });
  await check('no page errors and every asset/worker request stays local', async () => {
    assert.deepEqual(pageErrors, []);
    assert.deepEqual(report.externalRequests, []);
  });
  report.success = true;
} catch (error) {
  report.errorDetails = {
    code: error?.code,
    actual: typeof error?.actual === 'number' ? error.actual : undefined,
    expected: typeof error?.expected === 'number' ? error.expected : undefined,
    at: error?.stack?.split('\n').slice(-2).join(' '),
  };
  report.errors.push(
    String(error?.message || error)
      .split('\n')[0]
      .replaceAll(session.token, '[redacted]'),
  );
  report.actionDetail = String(error?.message || error)
    .split('\n')
    .slice(-12)
    .join('\n')
    .slice(-2000)
    .replaceAll(session.token, '[redacted]');
  if (page) {
    try {
      await screenshot('failure');
    } catch {}
  }
  console.error(report.errors.at(-1));
  process.exitCode = 1;
} finally {
  report.pageErrors = pageErrors;
  if (browser) await browser.close();
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2), 'utf8');
}
