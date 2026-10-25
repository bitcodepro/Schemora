import assert from 'node:assert/strict';
import fs from 'node:fs/promises';
import path from 'node:path';
import { chromium } from 'playwright-core';
import { openApp } from './support.mjs';
const [sessionPath, fixtureRoot, reportPath, screenshotDirectory, executablePath] = process.argv.slice(2);
const session = JSON.parse(await fs.readFile(sessionPath, 'utf8'));
const report = JSON.parse(await fs.readFile(reportPath, 'utf8'));
report.success = false;
report.enhancedWorkflows = true;
let browser;
let page;
let workspaceId;
let manualId;
const errors = [];
async function check(name, action) {
  await action();
  report.checks.push(name);
  console.log('PASS ' + name);
}
async function shown(locator, timeout = 30000) {
  await locator.waitFor({ state: 'visible', timeout });
}
async function poll(test, message, timeout = 45000) {
  const deadline = Date.now() + timeout;
  while (Date.now() < deadline) {
    if (await test()) return;
    await new Promise(resolve => setTimeout(resolve, 200));
  }
  throw new Error(message);
}
async function selectNamed(label, name) {
  const select = page.getByRole('combobox', { name: label, exact: true });
  await poll(async () => (await select.locator('option').allTextContents()).some(text => text.includes(name)), 'Option missing: ' + name);
  const value = await select
    .locator('option')
    .evaluateAll((items, target) => items.find(item => item.textContent.includes(target))?.value, name);
  assert.ok(value);
  await select.selectOption(value);
  return value;
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
async function shot(name) {
  const target = path.join(screenshotDirectory, name + '.png');
  await page.screenshot({ path: target, fullPage: false, timeout: 10000 });
  report.screenshots.push(target);
}
async function scan() {
  const response = page.waitForResponse(response => response.url().endsWith('/scan') && response.request().method() === 'POST');
  await page.getByRole('button', { name: 'Полное сканирование', exact: true }).click();
  const job = await (await response).json();
  await poll(async () => {
    const state = await api('/api/jobs/' + job.id);
    if (['failed', 'cancelled'].includes(state.status)) throw new Error('Scan ' + state.status + ': ' + state.error);
    return ['completed', 'partial'].includes(state.status);
  }, 'Enhanced schema scan timed out');
  await poll(async () => (await page.locator('.database-card').count()) === 1, 'Enhanced database catalog did not refresh');
}
try {
  browser = await chromium.launch({ headless: true, executablePath });
  const context = await browser.newContext({
    viewport: { width: 1440, height: 1050 },
    locale: 'ru-RU',
    permissions: ['clipboard-read', 'clipboard-write'],
  });
  page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => {
    const url = request.url();
    if (!url.startsWith(session.origin + '/') && !url.startsWith('blob:') && !url.startsWith('data:'))
      report.externalRequests.push(new URL(url).origin);
  });
  await openApp(page, session.launchUrl);
  await page.locator('[data-testid=language-selector]').first().selectOption('ru');
  await poll(async () => (await page.locator('html').getAttribute('lang')) === 'ru', 'Russian locale did not activate');
  await check('enhanced workspace scans composite keys and rich schema objects', async () => {
    await page.getByRole('button', { name: '+ Новая область', exact: true }).click();
    await page.getByRole('textbox', { name: 'Название', exact: true }).fill('UI enhanced acceptance');
    await page.getByRole('textbox', { name: /Корневые папки/ }).fill(fixtureRoot);
    const response = page.waitForResponse(response => response.url().endsWith('/api/workspaces') && response.request().method() === 'POST');
    await page.getByRole('button', { name: 'Создать область', exact: true }).click();
    workspaceId = (await (await response).json()).id;
    await scan();
  });
  await check('manual composite relation create keeps ordered column pairs and reason', async () => {
    await page.getByRole('button', { name: 'Связи и пути', exact: true }).click();
    await page.getByRole('button', { name: '+ Новая ручная связь', exact: true }).click();
    await selectNamed('Ручная связь: исходная таблица', 'Documents');
    await selectNamed('Ручная связь: целевая таблица', 'Accounts');
    await page.getByRole('textbox', { name: 'Название ручной связи', exact: true }).fill('Documents to Accounts composite');
    await page.getByRole('combobox', { name: 'Исходная колонка 1', exact: true }).selectOption('TenantId');
    await page.getByRole('combobox', { name: 'Целевая колонка 1', exact: true }).selectOption('TenantId');
    await page.getByRole('button', { name: '+ Добавить пару колонок', exact: true }).click();
    await page.getByRole('combobox', { name: 'Исходная колонка 2', exact: true }).selectOption('AccountId');
    await page.getByRole('combobox', { name: 'Целевая колонка 2', exact: true }).selectOption('AccountId');
    await page
      .getByRole('textbox', { name: 'Основание ручной связи', exact: true })
      .fill('Synthetic source confirms tenant and account form the account reference.');
    const response = page.waitForResponse(
      response => response.url().endsWith('/manual-relations') && response.request().method() === 'POST',
    );
    await page.getByRole('button', { name: 'Создать ручную связь', exact: true }).click();
    const created = await (await response).json();
    assert.equal(created.canUseForJoin, true, JSON.stringify(created));
    assert.deepEqual(created.fromColumns, ['TenantId', 'AccountId']);
    assert.equal(created.version, 1);
    manualId = created.id;
    await shown(page.locator('.manual-relation-list').getByText('Documents to Accounts composite', { exact: true }));
  });
  await check('manual relation edit reorders composite pairs together with version guard', async () => {
    await page
      .locator('.manual-relation-item')
      .filter({ hasText: 'Documents to Accounts composite' })
      .getByRole('button', { name: 'Изменить', exact: true })
      .click();
    await shown(page.getByRole('button', { name: 'Сохранить изменения связи', exact: true }));
    await page.getByRole('button', { name: 'Пару 2 выше', exact: true }).click();
    await page
      .getByRole('textbox', { name: 'Основание ручной связи', exact: true })
      .fill('Synthetic source rechecked; pair order intentionally changed together.');
    const response = page.waitForResponse(
      response => response.url().endsWith('/manual-relations/' + manualId) && response.request().method() === 'PUT',
    );
    await page.getByRole('button', { name: 'Сохранить изменения связи', exact: true }).click();
    const updated = await (await response).json();
    assert.equal(updated.version, 2);
    assert.equal(updated.canUseForJoin, true);
    assert.deepEqual(updated.fromColumns, ['AccountId', 'TenantId']);
    assert.deepEqual(updated.toColumns, ['AccountId', 'TenantId']);
    await shot('07-manual-composite');
  });
  await check('three-table recipe wizard requires route and explicit projected columns', async () => {
    await page.getByRole('button', { name: 'SQL-рецепты', exact: true }).click();
    await page.getByText('Создать рецепт по цепочке таблиц', { exact: true }).click();
    await selectNamed('Цепочка: исходная таблица', 'Documents');
    await selectNamed('Цепочка: целевая таблица', 'Transactions');
    await page.getByRole('combobox', { name: 'Поле ID для цепочки', exact: true }).selectOption('DocumentId');
    await page
      .getByRole('textbox', { name: 'Название рецепта по цепочке', exact: true })
      .fill('Documents through accounts to transactions');
    await page.getByRole('button', { name: 'Найти маршруты для рецепта', exact: true }).click();
    await shown(page.locator('.chain-route-list>button').first());
    assert.equal(await page.getByRole('button', { name: 'Предварительный SQL рецепта', exact: true }).count(), 0);
    await page.locator('.chain-route-list>button').first().click();
    await shown(page.getByRole('checkbox', { name: 'Результат: Documents.DocumentId', exact: true }));
    await page.getByRole('checkbox', { name: 'Результат: Documents.DocumentId', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Результат: Accounts.Name', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Результат: Transactions.ExternalId', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Результат: Accounts.AccountId', exact: true }).check();
    await page.getByRole('checkbox', { name: 'Результат: Transactions.AccountId', exact: true }).check();
    assert.equal(
      await page.getByRole('button', { name: 'Предварительный SQL рецепта', exact: true }).isDisabled(),
      true,
      'Duplicate output names were accepted',
    );
    await page.getByRole('textbox', { name: 'Имя результата 4', exact: true }).fill('SourceAccountId');
    await page.getByRole('textbox', { name: 'Имя результата 5', exact: true }).fill('TargetAccountId');
    await page.getByRole('button', { name: 'Поле 5 выше', exact: true }).click();
    const response = page.waitForResponse(response => response.url().endsWith('/recipe-preview'));
    await page.getByRole('button', { name: 'Предварительный SQL рецепта', exact: true }).click();
    const preview = await (await response).json();
    assert.equal(preview.definition.inputs[0].type, 'int');
    assert.equal((preview.definition.sqlTemplate.match(/\bJOIN\b/g) || []).length, 2);
    assert.match(preview.definition.sqlTemplate, /\{\{ids:Ids\}\}/);
    assert.match(preview.definition.sqlTemplate, /ExternalId/);
    assert.ok(preview.definition.sqlTemplate.indexOf('[TargetAccountId]') < preview.definition.sqlTemplate.indexOf('[SourceAccountId]'));
    assert.equal((await api('/api/workspaces/' + workspaceId + '/recipes')).length, 0, 'Preview unexpectedly saved recipe');
    await shown(page.getByRole('button', { name: 'Создать черновик из цепочки', exact: true }));
    await shot('08-chain-preview');
    await page.getByRole('button', { name: 'Создать черновик из цепочки', exact: true }).click();
    await page.getByRole('button', { name: 'Сохранить рецепт', exact: true }).click();
    await shown(page.getByText('Сохранено', { exact: true }));
    const saved = await api('/api/workspaces/' + workspaceId + '/recipes');
    assert.equal(saved.length, 1);
    assert.ok(['Valid', 'Warning'].includes(saved[0].validation.status), JSON.stringify(saved[0].validation));
    report.chainRecipeId = saved[0].definition.id;
    report.manualCompositeChecked = true;
  });
  await check('object explorer shows views, routines, synonyms and index details', async () => {
    await page.getByRole('button', { name: 'Рабочая область', exact: true }).click();
    await page.locator('.database-card').getByRole('button', { name: 'Открыть схему', exact: true }).click();
    await page.getByRole('button', { name: 'Все объекты', exact: true }).click();
    const objectKind = page.getByRole('combobox', { name: 'Тип объекта схемы', exact: true });
    for (const [kind, name] of [
      ['view', 'vAccounts'],
      ['procedure', 'GetAccounts'],
      ['scalarFunction', 'GetLabel'],
      ['tableFunction', 'ListAccounts'],
      ['synonym', 'AccountAlias'],
    ]) {
      await objectKind.selectOption(kind);
      const object = page.locator('.object-list>button').filter({ hasText: name });
      await shown(object);
      await object.click();
      await shown(page.locator('.object-explorer-detail').getByRole('heading', { name: 'dbo.' + name, exact: true }));
      if (kind === 'procedure') {
        await shown(page.locator('.object-explorer-detail table').getByText('@TenantId', { exact: true }));
        await shown(page.locator('.object-explorer-detail table').getByText('@Count', { exact: true }));
      }
      if (kind === 'synonym') assert.match(await page.locator('.object-explorer-detail').innerText(), /Accounts/);
    }
    await objectKind.selectOption('procedure');
    await page.locator('.object-list>button').filter({ hasText: 'GetAccounts' }).click();
    await page.getByText('Определение SQL', { exact: true }).click();
    await shown(page.getByLabel('Определение объекта SQL', { exact: true }).first());
    await shot('09-object-procedure');
    await objectKind.selectOption('table');
    await page.locator('.object-list>button').filter({ hasText: 'Transactions' }).click();
    await shown(page.getByText('IX_Transactions_ExternalId', { exact: true }));
    assert.match(await page.locator('.object-explorer-detail').innerText(), /ExternalId DESC/);
    assert.match(await page.locator('.object-explorer-detail').innerText(), /TransactionId/);
    report.extendedObjectsChecked = true;
  });
  await check('file watcher auto-refresh updates schema and can be disabled', async () => {
    await page.getByRole('button', { name: 'Рабочая область', exact: true }).click();
    const toggle = page.getByRole('checkbox', { name: 'Включить автообновление', exact: true });
    await shown(toggle);
    const response = page.waitForResponse(response => response.url().endsWith('/auto-refresh') && response.request().method() === 'PUT');
    await toggle.check();
    const enabled = await (await response).json();
    assert.equal(enabled.settings.enabled, true);
    await poll(
      async () => (await api('/api/workspaces/' + workspaceId + '/auto-refresh')).status.watcherCount > 0,
      'Filesystem watcher did not start',
    );
    const previousJob = (await api('/api/workspaces/' + workspaceId + '/auto-refresh')).status.lastJobId;
    await fs.appendFile(
      path.join(fixtureRoot, 'Schema.sql'),
      '\nGO\nCREATE VIEW dbo.vAutoRefreshed AS SELECT TransactionId FROM dbo.Transactions;\n',
      'utf8',
    );
    let autoJob;
    await poll(
      async () => {
        const status = await api('/api/workspaces/' + workspaceId + '/auto-refresh');
        autoJob = status.status.lastJobId;
        if (!autoJob || autoJob === previousJob) return false;
        const job = await api('/api/jobs/' + autoJob);
        return ['completed', 'partial'].includes(job.status);
      },
      'Watcher did not refresh modified schema',
      60000,
    );
    await poll(async () => {
      const objects = await api('/api/workspaces/' + workspaceId + '/objects?query=vAutoRefreshed');
      return objects.items.some(object => object.name === 'vAutoRefreshed');
    }, 'Watcher snapshot does not contain added view');
    await shot('10-auto-refresh');
    await toggle.uncheck();
    await poll(async () => {
      const status = await api('/api/workspaces/' + workspaceId + '/auto-refresh');
      return status.settings.enabled === false && status.status.watcherCount === 0;
    }, 'Watcher did not stop');
    report.autoRefreshChecked = true;
  });
  await check('manual relation removal is explicit and removes trusted route', async () => {
    await page.getByRole('button', { name: 'Связи и пути', exact: true }).click();
    const record = page.locator('.manual-relation-item').filter({ hasText: 'Documents to Accounts composite' });
    await shown(record);
    await record.getByRole('button', { name: 'Удалить связь', exact: true }).click();
    assert.equal((await api('/api/workspaces/' + workspaceId + '/manual-relations')).length, 1);
    const response = page.waitForResponse(
      response => response.url().includes('/manual-relations/' + manualId) && response.request().method() === 'DELETE',
    );
    await page.getByRole('button', { name: 'Удалить окончательно', exact: true }).click();
    assert.ok((await response).ok());
    await poll(
      async () => (await api('/api/workspaces/' + workspaceId + '/manual-relations')).length === 0,
      'Manual relation still exists',
    );
  });
  await check('enhanced workflows have no page errors or external asset requests', async () => {
    assert.deepEqual(errors, []);
    assert.deepEqual(report.externalRequests, []);
  });
  report.success = true;
} catch (error) {
  report.errors.push(
    String(error?.message || error)
      .split('\n')[0]
      .replaceAll(session.token, '[redacted]'),
  );
  report.enhancementErrorDetail = String(error?.message || error)
    .split('\n')
    .slice(-10)
    .join('\n')
    .slice(-2000)
    .replaceAll(session.token, '[redacted]');
  console.error(report.errors.at(-1));
  if (page) {
    try {
      await shot('enhancement-failure');
    } catch {}
  }
  process.exitCode = 1;
} finally {
  report.pageErrors = [...(report.pageErrors || []), ...errors];
  if (browser) await browser.close();
  await fs.writeFile(reportPath, JSON.stringify(report, null, 2), 'utf8');
}
