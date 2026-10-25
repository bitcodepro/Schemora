import { t } from './i18n.ts';
export type Projection = { tableId: string; columnName: string; alias?: string };
export function projectionIssues(columns: Projection[]): string[] {
  if (!columns.length) return [t('Select at least one output column.')];
  if (columns.length > 200) return [t('A recipe can have up to 200 output columns.')];
  const names = columns.map(column => column.alias?.trim() || column.columnName);
  const issues: string[] = [];
  if (names.some(name => name.length > 128 || /\p{Cc}/u.test(name)))
    issues.push(t('Output names must be at most 128 characters and contain no control characters.'));
  if (new Set(names.map(name => name.toLocaleLowerCase('en-US'))).size !== names.length)
    issues.push(t('Output columns must have unique names. Add aliases for same-named columns from different tables.'));
  return issues;
}
export function moveProjection(columns: Projection[], index: number, delta: -1 | 1) {
  const target = index + delta;
  if (index < 0 || index >= columns.length || target < 0 || target >= columns.length) return columns;
  const updated = [...columns];
  [updated[index], updated[target]] = [updated[target], updated[index]];
  return updated;
}
