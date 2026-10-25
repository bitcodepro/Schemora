import test from 'node:test';
import assert from 'node:assert/strict';
import { schemaOverview } from './schemaOverview.ts';
import { RequestScope } from './requestScope.ts';

test('a focus that sorts after sixty neighbours remains in the bounded overview', () => {
  const tables = Array.from({ length: 80 }, (_, index) => ({
    id: String(index),
    name: String(index),
    schema: 'dbo',
    columns: [],
    primaryKey: [],
  }));
  const focus = tables.at(-1);
  const relations = tables.slice(0, -1).map(table => ({ fromTableId: table.id, toTableId: focus.id }));
  const overview = schemaOverview(tables, relations, focus);
  assert.equal(overview.tables.length, 60);
  assert.equal(overview.tables[0], focus);
  assert.equal(new Set(overview.tables.map(table => table.id)).size, 60);
  assert.equal(overview.partial, true);
  assert.equal(tables.at(-1), focus);
});

test('a response already received by a superseded request cannot update the model', async () => {
  const scope = new RequestScope();
  const old = scope.begin();
  const received = Promise.resolve('workspace A');
  const current = scope.begin();
  let shown = 'workspace B';
  const value = await received;
  if (old.current()) shown = value;
  assert.equal(shown, 'workspace B');
  assert.equal(old.signal.aborted, true);
  assert.equal(current.current(), true);
  scope.cancel();
  assert.equal(current.current(), false);
});
