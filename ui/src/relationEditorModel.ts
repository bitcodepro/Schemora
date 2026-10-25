import { t } from './i18n.ts';
import type { GraphTable } from './graphModel';

export type ColumnPair = { fromColumn: string; toColumn: string };
export function pairColumns(pairs: ColumnPair[]) {
  return { fromColumns: pairs.map(pair => pair.fromColumn), toColumns: pairs.map(pair => pair.toColumn) };
}
export function pairIssues(pairs: ColumnPair[], from: GraphTable | null, to: GraphTable | null): string[] {
  const issues: string[] = [];
  if (!from || !to) issues.push(t('Select both tables.'));
  if (!pairs.length) issues.push(t('Add at least one column pair.'));
  if (pairs.some(pair => !pair.fromColumn || !pair.toColumn)) issues.push(t('Select both columns in every pair.'));
  if (
    new Set(pairs.map(pair => pair.fromColumn).filter(Boolean)).size < pairs.filter(pair => pair.fromColumn).length ||
    new Set(pairs.map(pair => pair.toColumn).filter(Boolean)).size < pairs.filter(pair => pair.toColumn).length
  )
    issues.push(t('A column may appear only once on each side of a relationship.'));
  if (from && pairs.some(pair => pair.fromColumn && !from.columns.some(column => column.name === pair.fromColumn)))
    issues.push(t('A selected column no longer exists in the source table.'));
  if (to && pairs.some(pair => pair.toColumn && !to.columns.some(column => column.name === pair.toColumn)))
    issues.push(t('A selected column no longer exists in the target table.'));
  return issues;
}
export function movePair(pairs: ColumnPair[], index: number, delta: -1 | 1): ColumnPair[] {
  const target = index + delta;
  if (index < 0 || index >= pairs.length || target < 0 || target >= pairs.length) return pairs;
  const updated = [...pairs];
  [updated[index], updated[target]] = [updated[target], updated[index]];
  return updated;
}
