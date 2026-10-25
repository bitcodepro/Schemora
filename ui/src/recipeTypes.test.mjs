import assert from 'node:assert/strict';
import test from 'node:test';
import {
  decodeRecipeImport,
  defaultInput,
  emptyRecipe,
  inputFromSqlType,
  quoteSqlIdentifier,
  recipeForSave,
  datePresets,
  isoDate,
  periodPresets,
  valueFormatHint,
  valueInputNames,
} from './recipeTypes.ts';

test('recipe authoring serialization excludes runtime ID text, parsed values and rendered SQL', () => {
  const sentinel = 'PRIVATE_RUNTIME_SENTINEL_987654321';
  const recipe = {
    ...emptyRecipe(['database']),
    name: 'Fixture',
    sqlTemplate: 'SELECT 1;',
    inputText: { Ids: sentinel },
    rendered: { sql: `SELECT '${sentinel}';` },
    values: [sentinel],
    inputs: [{ ...defaultInput(), runtimeValue: sentinel }],
    dependencies: [{ databaseId: 'database', tableId: 'table', runtimeValue: sentinel }],
  };
  const serialized = JSON.stringify(recipeForSave(recipe));
  assert.equal(serialized.includes(sentinel), false);
  assert.equal(serialized.includes('inputText'), false);
  assert.equal(serialized.includes('rendered'), false);
  assert.equal(JSON.parse(serialized).sqlTemplate, 'SELECT 1;');
});

test('export envelope imports definition only and preserves text encoding/collation policy', () => {
  const definition = {
    ...emptyRecipe(['db']),
    name: 'Text IDs',
    sqlTemplate: 'SELECT * FROM dbo.T WHERE Id IN ({{ids:Ids}});',
    inputs: [
      {
        ...defaultInput('Ids', 'varchar'),
        encoding: 'windows-1251',
        collation: 'Cyrillic_General_100_BIN2',
        maxLength: 80,
        deduplicate: true,
      },
    ],
  };
  const decoded = decodeRecipeImport({
    formatVersion: 1,
    recipe: definition,
    databaseReferences: [{ id: 'db', name: 'Name' }],
    runtimeValues: ['never-import-this'],
  });
  assert.deepEqual(decoded, recipeForSave(definition));
  assert.equal(decoded.inputs[0].collation, 'Cyrillic_General_100_BIN2');
  assert.equal(JSON.stringify(decoded).includes('never-import-this'), false);
  assert.throws(() => decodeRecipeImport({ inputText: { Ids: '1' } }));
});

test('table wizard preserves supported SQL identifier types and string lengths without guessing other types', () => {
  assert.equal(inputFromSqlType('Ids', '[bigint]').type, 'bigint');
  assert.equal(inputFromSqlType('Ids', 'nvarchar(80)').maxLength, 80);
  assert.equal(inputFromSqlType('Ids', '[nvarchar](max)').maxLength, -1);
  assert.equal(inputFromSqlType('Ids', 'uniqueidentifier').type, 'uniqueidentifier');
  assert.equal(inputFromSqlType('Ids', 'decimal(18,2)').scale, 2);
  assert.equal(inputFromSqlType('Ids', 'float'), null);
  assert.equal(defaultInput('Ids', 'varchar').encoding, 'ascii');
  assert.equal(defaultInput().deduplicate, false);
  assert.equal(defaultInput().preserveOrder, true);
});

test('wizard SQL names are delimited and closing brackets escaped', () => {
  assert.equal(quoteSqlIdentifier('db]name'), '[db]]name]');
  assert.equal(quoteSqlIdentifier('x]; DROP TABLE t;--'), '[x]]; DROP TABLE t;--]');
});

test('single-value placeholders are recognised and date/bit columns map to typed inputs', () => {
  const template =
    'SELECT 1 FROM dbo.Orders WHERE CreatedAt >= {{value:From}} AND Id IN ({{ids:Ids}}) AND x = {{value:From}} -- {{value:1bad}}';
  assert.deepEqual([...valueInputNames(template)], ['From']);
  assert.equal(inputFromSqlType('Day', 'date')?.type, 'date');
  assert.equal(inputFromSqlType('At', 'datetime')?.type, 'datetime2');
  assert.equal(inputFromSqlType('At', 'datetime2(3)')?.type, 'datetime2');
  assert.equal(inputFromSqlType('Flag', 'bit')?.type, 'bit');
  assert.deepEqual([inputFromSqlType('Amount', 'decimal(10,2)')?.precision, inputFromSqlType('Amount', 'decimal(10,2)')?.scale], [10, 2]);
  assert.deepEqual([inputFromSqlType('Amount', 'numeric(12)')?.precision, inputFromSqlType('Amount', 'numeric(12)')?.scale], [12, 0]);
  assert.deepEqual([inputFromSqlType('Price', 'money')?.precision, inputFromSqlType('Price', 'money')?.scale], [19, 4]);
  assert.equal(inputFromSqlType('Ratio', 'float'), null);
  assert.equal(defaultInput('Day', 'date').maxLength, undefined);
  assert.equal(valueFormatHint('date'), 'yyyy-MM-dd');
  assert.equal(valueFormatHint('bit'), '1 / 0');
});

test('date presets use the local calendar, roll over months and years, and suit datetime2', () => {
  const now = new Date(2026, 0, 15, 22, 45);
  const dates = Object.fromEntries(datePresets('date', now).map(preset => [preset.label, preset.value]));
  assert.equal(dates['Today'], '2026-01-15');
  assert.equal(dates['Tomorrow'], '2026-01-16');
  assert.equal(dates['Start of this month'], '2026-01-01');
  assert.equal(dates['Start of last month'], '2025-12-01');
  assert.equal(dates['30 days ago'], '2025-12-16');
  assert.equal(dates['Start of this year'], '2026-01-01');
  assert.equal(datePresets('datetime2', now)[0].value, '2026-01-15T00:00');
  assert.deepEqual(datePresets('int', now), []);
});

test('period presets are whole periods with an exclusive end, across year boundaries', () => {
  const periods = Object.fromEntries(
    periodPresets(new Date(2026, 0, 15, 9)).map(p => [p.label, [isoDate(p.from, 'date'), isoDate(p.to, 'date')]]),
  );
  assert.deepEqual(periods['Last month'], ['2025-12-01', '2026-01-01']);
  assert.deepEqual(periods['This month'], ['2026-01-01', '2026-02-01']);
  assert.deepEqual(periods['Last 7 days'], ['2026-01-08', '2026-01-16']);
  assert.deepEqual(periods['Last 30 days'], ['2025-12-16', '2026-01-16']);
  assert.deepEqual(periods['Last year'], ['2025-01-01', '2026-01-01']);
  assert.deepEqual(periods['This year'], ['2026-01-01', '2027-01-01']);
  assert.equal(isoDate(new Date(2026, 9, 1), 'datetime2'), '2026-10-01T00:00');
});
