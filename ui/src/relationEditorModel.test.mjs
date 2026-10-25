import test from 'node:test';
import assert from 'node:assert/strict';
import { setLocale } from './i18n.ts';
import { movePair, pairColumns, pairIssues } from './relationEditorModel.ts';
setLocale('ru');
const table = (id, names) => ({
  id,
  schema: 'dbo',
  name: id,
  columns: names.map(name => ({ name, dataType: 'int', isNullable: false })),
  primaryKey: [],
  databaseId: 'db',
  databaseName: 'Demo',
});
test('composite pair ordering stays aligned during reorder and save without mutating editor state', () => {
  const pairs = [
    { fromColumn: 'Tenant', toColumn: 'TenantId' },
    { fromColumn: 'Parent', toColumn: 'Id' },
  ];
  const reordered = movePair(pairs, 1, -1);
  assert.deepEqual(pairColumns(reordered), { fromColumns: ['Parent', 'Tenant'], toColumns: ['Id', 'TenantId'] });
  assert.equal(pairs[0].fromColumn, 'Tenant');
  assert.deepEqual(pairIssues(reordered, table('A', ['Tenant', 'Parent']), table('B', ['TenantId', 'Id'])), []);
});
test('manual pairs reject missing columns and duplicate halves, preserving precise column case', () => {
  const issues = pairIssues(
    [
      { fromColumn: 'Tenant', toColumn: 'TenantId' },
      { fromColumn: 'Tenant', toColumn: 'id' },
    ],
    table('A', ['Tenant']),
    table('B', ['TenantId', 'Id']),
  );
  assert.equal(issues.length, 2);
  assert.ok(issues.some(issue => issue.includes('только один раз')));
  assert.ok(issues.some(issue => issue.includes('целевой таблице')));
});
