import test from 'node:test';
import assert from 'node:assert/strict';
import { formatRoute, readRoute } from './route.ts';

test('routes keep the page and identifiers and ignore the launch token fragment', () => {
  const hash = formatRoute('recipes', { ws: 'a1', recipe: 'r 2', tab: 'runtime', empty: '' });
  assert.equal(hash, '#/recipes?ws=a1&recipe=r+2&tab=runtime');
  assert.deepEqual(readRoute(hash), { page: 'recipes', params: { ws: 'a1', recipe: 'r 2', tab: 'runtime' } });
  assert.deepEqual(readRoute('#/graph'), { page: 'graph', params: {} });
  assert.deepEqual(readRoute('#token=secret'), { page: '', params: {} });
  assert.deepEqual(readRoute(''), { page: '', params: {} });
});
