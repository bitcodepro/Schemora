import { t } from './i18n.ts';
import type { GraphTable } from './graphModel';

export type ColumnPair = { fromColumn: string; toColumn: string };
export function pairColumns(pairs: ColumnPair[]) {
  return { fromColumns: pairs.map(pair => pair.fromColumn), toColumns: pairs.map(pair => pair.toColumn) };
}
export function pairIssues(pairs: ColumnPair[], from: GraphTable | null, to: GraphTable | null): string[] {
  const issues: string[] = [];
  if (!from || !to) issues.push(t('Выберите обе таблицы.'));
  if (!pairs.length) issues.push(t('Добавьте хотя бы одну пару колонок.'));
  if (pairs.some(pair => !pair.fromColumn || !pair.toColumn)) issues.push(t('Выберите обе колонки в каждой паре.'));
  if (
    new Set(pairs.map(pair => pair.fromColumn).filter(Boolean)).size < pairs.filter(pair => pair.fromColumn).length ||
    new Set(pairs.map(pair => pair.toColumn).filter(Boolean)).size < pairs.filter(pair => pair.toColumn).length
  )
    issues.push(t('Колонка может входить в связь только один раз с каждой стороны.'));
  if (from && pairs.some(pair => pair.fromColumn && !from.columns.some(column => column.name === pair.fromColumn)))
    issues.push(t('В исходной таблице больше нет одной из выбранных колонок.'));
  if (to && pairs.some(pair => pair.toColumn && !to.columns.some(column => column.name === pair.toColumn)))
    issues.push(t('В целевой таблице больше нет одной из выбранных колонок.'));
  return issues;
}
export function movePair(pairs: ColumnPair[], index: number, delta: -1 | 1): ColumnPair[] {
  const target = index + delta;
  if (index < 0 || index >= pairs.length || target < 0 || target >= pairs.length) return pairs;
  const updated = [...pairs];
  [updated[index], updated[target]] = [updated[target], updated[index]];
  return updated;
}
