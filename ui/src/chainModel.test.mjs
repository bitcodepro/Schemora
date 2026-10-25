import test from 'node:test';
import assert from 'node:assert/strict';
import { moveProjection, projectionIssues } from './chainModel.ts';
test('result projections require unique output names while allowing explicit aliases from different tables', () => {
  const columns = [
    { tableId: 'a', columnName: 'Id' },
    { tableId: 'b', columnName: 'ID' },
  ];
  assert.equal(projectionIssues(columns).length, 1);
  assert.deepEqual(
    projectionIssues([
      { ...columns[0], alias: ' SourceId ' },
      { ...columns[1], alias: 'TargetId' },
    ]),
    [],
  );
  assert.equal(projectionIssues([{ ...columns[0], alias: 'bad\nname' }]).length, 1);
});
test('projection reorder preserves selected column and explicit alias together without mutating draft', () => {
  const source = [
    { tableId: 'a', columnName: 'Id', alias: 'SourceId' },
    { tableId: 'b', columnName: 'Id', alias: 'TargetId' },
  ];
  const updated = moveProjection(source, 1, -1);
  assert.equal(updated[0].alias, 'TargetId');
  assert.equal(updated[0].tableId, 'b');
  assert.equal(source[0].tableId, 'a');
  assert.deepEqual(projectionIssues(updated), []);
});
