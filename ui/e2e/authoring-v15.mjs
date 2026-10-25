import { chromium } from 'playwright-core';
import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { openApp, reloadApp } from './support.mjs';
const [sessionFile, reportFile] = process.argv.slice(2),
  session = JSON.parse(await fs.readFile(sessionFile, 'utf8'));
const checks = [],
  errors = [];
const dictionaries = {};
for (const file of await fs.readdir(new URL('../src/locales/', import.meta.url)))
  if (file.endsWith('.json'))
    Object.assign(dictionaries, JSON.parse(await fs.readFile(new URL('../src/locales/' + file, import.meta.url), 'utf8')));
// Catalog keys are English; a Russian or Ukrainian label used in a test still finds its entry.
const entry = key => dictionaries[key] || Object.values(dictionaries).find(item => item.ru === key || item.uk === key);
const L = key => entry(key)?.en || key;
async function api(url, method = 'GET', body) {
  const r = await fetch(session.origin + url, {
    method,
    headers: { 'Content-Type': 'application/json', 'X-Schemora-Token': session.token, Origin: session.origin },
    ...(body ? { body: JSON.stringify(body) } : {}),
  });
  const text = await r.text();
  assert.ok(r.ok, text);
  return text ? JSON.parse(text) : null;
}
const tutorial = await api('/api/tutorial', 'POST');
const root = tutorial.workspace.roots[0];
await fs.writeFile(
  path.join(root, 'Synthetic.csproj'),
  '<Project><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableDefaultCompileItems>false</EnableDefaultCompileItems></PropertyGroup><ItemGroup><Compile Include="Repo.cs" /></ItemGroup></Project>',
);
await fs.writeFile(
  path.join(root, 'Repo.cs'),
  'class Repo { void Read(dynamic connection) {\n#if DEBUG\nconnection.Query("SELECT o.OrderId,c.CustomerId FROM dbo.Orders o JOIN dbo.Customers c ON o.CustomerId=c.CustomerId");\n#endif\nconnection.Query(BuildSql()); } }',
);
await fs.writeFile(path.join(root, 'Excluded.cs'), 'class Excluded {}');
const job = await api('/api/workspaces/' + tutorial.workspace.id + '/scan', 'POST', { mode: 'full' });
for (let i = 0; i < 300; i++) {
  const state = await api('/api/jobs/' + job.id);
  if (['completed', 'partial'].includes(state.status)) break;
  if (state.status === 'failed') throw Error(JSON.stringify(state));
  await new Promise(r => setTimeout(r, 100));
}
const coverage = await api('/api/workspaces/' + tutorial.workspace.id + '/analysis-coverage');
assert.equal(coverage.projects.length, 1);
assert.ok(coverage.projects[0].includedFiles.some(f => f.endsWith('Repo.cs')));
assert.ok(coverage.projects[0].excludedFiles.some(f => f.endsWith('Excluded.cs')));
assert.ok(coverage.diagnostics.some(d => d.code === 'DAPPER_SQL_DYNAMIC'));
assert.ok(coverage.dapperEvidence > 0);
checks.push('Synthetic C# project reports included/excluded files and dynamic Dapper SQL');
const browser = await chromium.launch({ headless: true, executablePath: 'C:/Program Files/Google/Chrome/Application/chrome.exe' });
try {
  const page = await browser.newPage({ viewport: { width: 1500, height: 1100 }, acceptDownloads: true });
  page.on('pageerror', e => errors.push(e.message));
  await openApp(page, session.launchUrl);
  // The host may hold several tutorial workspaces; choose this one in the shared selector like a user would.
  await page.getByRole('combobox', { name: L('Workspace'), exact: true }).selectOption(tutorial.workspace.id);
  const button = key => page.getByRole('button', { name: L(key), exact: true });
  await button('SQL recipes').click();
  await button('Definition').click();
  const name = page.getByRole('textbox', { name: L('Recipe name'), exact: true });
  // Wait until the opened recipe has loaded, so its late arrival cannot overwrite the typed name.
  for (let i = 0; i < 200 && (await name.inputValue()) !== 'Orders for customers'; i++) await new Promise(r => setTimeout(r, 100));
  assert.equal(await name.inputValue(), 'Orders for customers');
  await name.fill('Recovered authoring draft');
  try {
    await page.waitForFunction(
      id => localStorage.getItem('schemora.authoring-draft:' + id)?.includes('Recovered authoring draft'),
      tutorial.workspace.id,
    );
  } catch (error) {
    const state = await page.evaluate(() => ({
      keys: Object.keys(localStorage),
      workspace: localStorage.getItem('schemora.workspace'),
      names: [...document.querySelectorAll('input')].map(input => input.getAttribute('aria-label') + '=' + input.value).slice(0, 8),
      buttons: [...document.querySelectorAll('button')]
        .map(button => button.textContent.trim())
        .filter(Boolean)
        .slice(0, 40),
    }));
    throw new Error(
      'Draft was not stored: ' + JSON.stringify({ expectedWorkspace: tutorial.workspace.id, ...state }) + ' ' + error.message,
    );
  }
  for (let i = 0; i < 100; i++) {
    if ((await api('/api/workspaces/' + tutorial.workspace.id + '/authoring-draft')).draft?.definition.name === 'Recovered authoring draft')
      break;
    await new Promise(r => setTimeout(r, 100));
  }
  assert.equal(
    (await api('/api/workspaces/' + tutorial.workspace.id + '/authoring-draft')).draft.definition.name,
    'Recovered authoring draft',
  );
  await page.evaluate(id => localStorage.removeItem('schemora.authoring-draft:' + id), tutorial.workspace.id);
  page.once('dialog', d => d.accept());
  await reloadApp(page);
  await button('SQL recipes').click();
  await button('Recover draft').click();
  await name.waitFor();
  assert.equal(await name.inputValue(), 'Recovered authoring draft');
  checks.push('Unsaved authoring definition recovers from local application storage after browser copy is removed');
  page.once('dialog', d => d.dismiss());
  await button('Workspace').click();
  assert.equal(await name.inputValue(), 'Recovered authoring draft');
  checks.push('Navigation cancellation retains editor changes');
  await button('Save new version').click();
  await page.getByText(L('Saved'), { exact: true }).waitFor();
  assert.equal(await page.evaluate(id => localStorage.getItem('schemora.authoring-draft:' + id), tutorial.workspace.id), null);
  checks.push('Saving clears the recovered draft');
  await button('Inputs and SQL').click();
  const ids = page.getByRole('textbox', { name: 'ID list: Ids', exact: true });
  await ids.fill('887766551');
  await page.evaluate(() => localStorage.setItem('test-marker', 'safe'));
  assert.ok(
    !(
      await page.evaluate(() =>
        Object.entries(localStorage)
          .filter(([k]) => k.startsWith('schemora.authoring-draft:'))
          .map(([, v]) => v)
          .join(''),
      )
    ).includes('887766551'),
  );
  assert.ok(!JSON.stringify(await api('/api/workspaces/' + tutorial.workspace.id + '/authoring-draft')).includes('887766551'));
  checks.push('Runtime IDs are absent from browser and application authoring draft storage');
  const valueSets = page.getByRole('region', { name: L('Saved values'), exact: true });
  await valueSets.getByRole('textbox', { name: L('Name for the current values'), exact: true }).fill('Customer set');
  await valueSets.getByRole('button', { name: L('Save values'), exact: true }).click();
  await valueSets.getByText(L('Saved "{0}".').replace('{0}', 'Customer set'), { exact: true }).waitFor();
  await ids.fill('');
  await valueSets.getByRole('button', { name: L('Load'), exact: true }).click();
  await valueSets.getByRole('status').filter({ hasText: 'Customer set' }).waitFor();
  assert.equal(await ids.inputValue(), '887766551');
  const listed = await api('/api/recipes/' + tutorial.recipeId + '/value-sets');
  assert.deepEqual(
    listed.map(set => [set.name, set.inputs]),
    [['Customer set', ['Ids']]],
  );
  assert.ok(!JSON.stringify(listed).includes('887766551'));
  page.once('dialog', d => d.accept());
  await valueSets.getByRole('button', { name: L('Remove'), exact: true }).click();
  await valueSets
    .getByRole('combobox', { name: L('Saved values'), exact: true })
    .getByText(L('No saved values yet'))
    .waitFor({ state: 'attached' });
  assert.deepEqual(await api('/api/recipes/' + tutorial.recipeId + '/value-sets'), []);
  checks.push('Named values save encrypted on the host, load back into the inputs and delete; listings carry names only');
  await button('Versions and lineage').click();
  await page
    .getByRole('button', { name: L('Restore'), exact: true })
    .last()
    .click();
  await page.getByRole('heading', { name: L('Compare with current version'), exact: true }).waitFor();
  await button('Restore as new version').click();
  await page.getByText(L('Saved'), { exact: true }).waitFor();
  for (let i = 0; i < 100; i++) {
    if ((await api('/api/recipes/' + tutorial.recipeId)).definition.version === 3) break;
    await new Promise(r => setTimeout(r, 100));
  }
  const recipe = await api('/api/recipes/' + tutorial.recipeId);
  assert.equal(recipe.definition.version, 3);
  assert.equal(recipe.definition.name, 'Orders for customers');
  checks.push('Version comparison restores history as a new revalidated version');
  await button('Workspace').click();
  const nav = page.getByRole('region', { name: 'Workspace navigator', exact: true });
  await page.getByRole('combobox', { name: L('Workspace'), exact: true }).selectOption(tutorial.workspace.id);
  await nav.getByRole('searchbox', { name: 'Saved navigation filter', exact: true }).fill('Orders');
  await nav.getByRole('button', { name: 'Orders · Table', exact: true }).click();
  await page.getByRole('searchbox', { name: 'Search dependent recipes', exact: true }).fill('missing');
  await page.getByText('Showing 0 of 0 recipes', { exact: false }).waitFor();
  await page.getByRole('searchbox', { name: 'Search dependent recipes', exact: true }).fill('Orders');
  await page.getByRole('button', { name: 'Orders for customers', exact: true }).waitFor();
  checks.push('Dependency recipe search returns bounded results');
  for (const language of ['ru', 'uk']) {
    await page.locator('[data-testid=language-selector]').first().selectOption(language);
    const downloadPromise = page.waitForEvent('download');
    await page.getByRole('button', { name: dictionaries['Export schema documentation'][language], exact: true }).click();
    const html = await fs.readFile(await (await downloadPromise).path(), 'utf8');
    assert.ok(html.includes('lang="' + language + '"'));
    assert.ok(html.includes(language === 'ru' ? 'Первичный ключ' : 'Первинний ключ'));
  }
  checks.push('HTML exports use selected Russian and Ukrainian locale');
  await page.locator('[data-testid=language-selector]').first().selectOption('en');
  await page.getByRole('checkbox', { name: 'Objects', exact: true }).uncheck();
  await page.getByRole('checkbox', { name: 'Recipe dependencies', exact: true }).check();
  const selectedDownload = page.waitForEvent('download');
  await nav.getByRole('button', { name: 'Export schema documentation', exact: true }).click();
  const selectedHtml = await fs.readFile(await (await selectedDownload).path(), 'utf8');
  assert.ok(!selectedHtml.includes('<table>'));
  assert.ok(selectedHtml.includes('Recipe dependencies'));
  checks.push('Report section selection excludes object details and includes recipe dependencies');
  await button('Relationships and paths').click();
  await page.getByRole('combobox', { name: L('Workspace'), exact: true }).selectOption(tutorial.workspace.id);
  await page.getByText('EF / Dapper analysis coverage', { exact: true }).click();
  await button('Refresh analysis coverage').click();
  await page.locator('.analysis-coverage').getByText('DAPPER_SQL_DYNAMIC', { exact: true }).waitFor();
  checks.push('Coverage UI exposes source diagnostics and next actions');
  await page.screenshot({ path: reportFile.replace('.json', '.png'), fullPage: false });
  assert.deepEqual(errors, []);
  await fs.writeFile(reportFile, JSON.stringify({ success: true, checks, pageErrors: errors }, null, 2));
  console.log('PASS ' + checks.length + ' authoring v1.5 workflows');
} finally {
  await browser.close();
}
