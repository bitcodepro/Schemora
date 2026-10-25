import assert from 'node:assert/strict';
import test from 'node:test';
import { layoutTables, readPositions, relationAppearance, retainedRelations, visibleColumns } from './graphModel.ts';

const table = (id, name = id, databaseName = 'Commerce') => ({
  id,
  name,
  databaseId: databaseName.toLowerCase(),
  databaseName,
  schema: 'dbo',
  columns: [{ name: 'Id', dataType: 'int', isNullable: false }],
  primaryKey: ['Id'],
});
const relation = (id, from, to, extra = {}) => ({
  id,
  name: id,
  fromTableId: from,
  toTableId: to,
  fromColumns: ['ParentId'],
  toColumns: ['Id'],
  kind: 'inferred-candidate',
  status: 'candidate',
  confidence: 0.9,
  canUseForJoin: false,
  evidence: [],
  ...extra,
});

test('saved graph positions reject malformed, non-finite and unreasonable coordinates', () => {
  const result = readPositions({
    version: 1,
    positions: {
      valid: { x: -125.5, y: 250 },
      invalidString: { x: '0', y: 1 },
      invalidNaN: { x: NaN, y: 0 },
      invalidInfinity: { x: 1, y: Infinity },
      unreasonable: { x: 1_000_001, y: 0 },
      missing: { x: 2 },
      empty: null,
    },
  });
  assert.deepEqual(result, { valid: { x: -125.5, y: 250 } });
  for (const malformed of [null, 'text', [], {}, { positions: [] }, { positions: 'text' }]) assert.deepEqual(readPositions(malformed), {});
});

test('saved graph keys cannot alter object prototypes', () => {
  const result = readPositions(JSON.parse('{"positions":{"__proto__":{"x":1,"y":2},"constructor":{"x":3,"y":4}}}'));
  assert.equal(Object.getPrototypeOf(result), Object.prototype);
  assert.equal(Object.hasOwn(result, '__proto__'), true);
  assert.deepEqual(result.__proto__, { x: 1, y: 2 });
  assert.equal(Object.prototype.x, undefined);
});

test('layout is deterministic for reordered sources, cycles and disconnected databases', () => {
  const tables = [table('a', 'Customers'), table('b', 'Orders'), table('c', 'Documents'), table('d', 'Customers', 'Billing')];
  const relations = [relation('ba', 'b', 'a'), relation('cb', 'c', 'b'), relation('ac', 'a', 'c')];
  const first = layoutTables(tables, relations);
  const reordered = layoutTables([...tables].reverse(), [...relations].reverse());
  assert.deepEqual(first, reordered);
  assert.equal(Object.keys(first).length, 4);
  assert.equal(new Set(Object.values(first).map(point => `${point.x},${point.y}`)).size, 4);
  for (const point of Object.values(first)) {
    assert.ok(Number.isFinite(point.x));
    assert.ok(Number.isFinite(point.y));
  }
});

test('without focus, referencing tables sit left of the tables they reference', () => {
  const tables = [table('orders', 'Orders'), table('customers', 'Customers'), table('lines', 'OrderLines'), table('notes', 'Notes')];
  const relations = [relation('oc', 'orders', 'customers'), relation('lo', 'lines', 'orders'), relation('nc', 'notes', 'customers')];
  const positions = layoutTables(tables, relations);
  assert.ok(positions.lines.x < positions.orders.x);
  assert.ok(positions.orders.x < positions.customers.x);
  assert.ok(positions.notes.x < positions.customers.x);
  assert.equal(new Set(Object.values(positions).map(point => `${point.x},${point.y}`)).size, 4);
});

test('cycles and self references still give every table one finite, distinct place', () => {
  const tables = [table('a'), table('b'), table('c')];
  const relations = [relation('ab', 'a', 'b'), relation('bc', 'b', 'c'), relation('ca', 'c', 'a'), relation('aa', 'a', 'a')];
  const positions = layoutTables(tables, relations);
  assert.equal(Object.keys(positions).length, 3);
  assert.equal(new Set(Object.values(positions).map(point => `${point.x},${point.y}`)).size, 3);
  assert.ok(positions.a.x < positions.b.x && positions.b.x < positions.c.x);
});

test('requested focus anchors the neighborhood and layout never mutates metadata', () => {
  const tables = Object.freeze([table('a'), table('b'), table('c')]);
  const relations = Object.freeze([relation('ab', 'a', 'b'), relation('bc', 'b', 'c')]);
  const before = JSON.stringify({ tables, relations });
  const positions = layoutTables(tables, relations, 'b');
  assert.deepEqual(positions.b, { x: 30, y: 30 });
  assert.equal(positions.a.x, 410);
  assert.equal(positions.c.x, 410);
  assert.equal(JSON.stringify({ tables, relations }), before);
});

test('graph fragment retains only edges with both endpoints, preserving composite keys and trust', () => {
  const tables = [table('a'), table('b')];
  const composite = relation('physical', 'a', 'b', {
    kind: 'declared-fk',
    status: 'physical',
    canUseForJoin: true,
    fromColumns: ['TenantId', 'Id'],
    toColumns: ['TenantId', 'Id'],
  });
  const candidate = relation('candidate', 'a', 'b');
  const retained = retainedRelations(tables, [composite, candidate, relation('outside', 'b', 'missing')]);
  assert.deepEqual(retained, [composite, candidate]);
  assert.deepEqual(retained[0].fromColumns, ['TenantId', 'Id']);
  assert.equal(retained[1].canUseForJoin, false);
});

test('confidence and display status cannot elevate backend join eligibility', () => {
  for (const data of [
    relation('perfect-score', 'a', 'b', { confidence: 1 }),
    relation('user-label', 'a', 'b', { kind: 'user-confirmed', status: 'confirmed', confidence: 1 }),
    relation('physical-label', 'a', 'b', { kind: 'declared-fk', status: 'physical', confidence: 1 }),
  ])
    assert.equal(relationAppearance(data).dashed, true);
  assert.equal(relationAppearance(relation('server-approved', 'a', 'b', { canUseForJoin: true })).dashed, false);
  assert.equal(relationAppearance(relation('conflict', 'a', 'b', { status: 'conflict' })).color, '#dc929a');
});

test('cards always list key and join columns, then fill up to four rows in table order', () => {
  const table = {
    id: 't',
    schema: 'dbo',
    name: 'Orders',
    primaryKey: ['Id'],
    columns: ['Id', 'Note', 'Total', 'Status', 'Created', 'CustomerId'].map(name => ({ name, dataType: 'int', isNullable: false })),
  };
  assert.deepEqual(
    visibleColumns(table, ['CustomerId']).map(column => column.name),
    ['Id', 'Note', 'Total', 'CustomerId'],
  );
  assert.deepEqual(
    visibleColumns(table, []).map(column => column.name),
    ['Id', 'Note', 'Total', 'Status'],
  );
});
