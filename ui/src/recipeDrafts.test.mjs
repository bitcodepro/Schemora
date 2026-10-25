import { test } from 'node:test';
import assert from 'node:assert/strict';
import { encodeDraft, decodeDraft, compareRecipe } from './recipeDrafts.ts';
import { emptyRecipe } from './recipeTypes.ts';
test('draft persistence whitelists authoring data and excludes runtime values', () => {
  const recipe = {
    ...emptyRecipe(['db']),
    sqlTemplate: 'SELECT {{ids:Ids}}',
    inputText: { Ids: 'SECRET_RUNTIME' },
    rendered: { sql: 'SECRET_RENDERED' },
    parameters: ['SECRET_PARAMETER'],
  };
  const text = encodeDraft(recipe, false);
  assert.ok(!text.includes('SECRET'));
  assert.equal(decodeDraft(text).definition.sqlTemplate, recipe.sqlTemplate);
  assert.equal(decodeDraft(text).imported, false);
});
test('version comparison reports SQL, input policies and dependencies without mutating versions', () => {
  const a = emptyRecipe(['db']);
  const b = {
    ...a,
    sqlTemplate: 'SELECT 1',
    inputs: [{ ...a.inputs[0], deduplicate: true }],
    dependencies: [{ databaseId: 'db', tableId: 'table' }],
  };
  assert.deepEqual(
    compareRecipe(a, b).map(c => c.key),
    ['sqlTemplate', 'inputs', 'dependencies'],
  );
  assert.equal(a.sqlTemplate, '');
  assert.equal(a.inputs[0].deduplicate, false);
});
test('malformed recovery envelope is rejected', () => {
  assert.throws(() => decodeDraft('{"format":9}'));
  assert.throws(() => decodeDraft('{"format":1,"savedAt":"now","definition":{}}'));
});
