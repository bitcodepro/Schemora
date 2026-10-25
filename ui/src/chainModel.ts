import { t } from './i18n.ts';
export type Projection = { tableId: string; columnName: string; alias?: string };
export function projectionIssues(columns: Projection[]): string[] {
  if (!columns.length) return [t('Выберите хотя бы одно поле результата.')];
  if (columns.length > 200) return [t('В одном рецепте можно выбрать до 200 полей результата.')];
  const names = columns.map(column => column.alias?.trim() || column.columnName);
  const issues: string[] = [];
  if (names.some(name => name.length > 128 || /\p{Cc}/u.test(name)))
    issues.push(t('Имя поля результата должно содержать до 128 символов без управляющих знаков.'));
  if (new Set(names.map(name => name.toLocaleLowerCase('en-US'))).size !== names.length)
    issues.push(t('Поля результата должны иметь разные имена. Укажите псевдоним для одинаковых колонок из разных таблиц.'));
  return issues;
}
export function moveProjection(columns: Projection[], index: number, delta: -1 | 1) {
  const target = index + delta;
  if (index < 0 || index >= columns.length || target < 0 || target >= columns.length) return columns;
  const updated = [...columns];
  [updated[index], updated[target]] = [updated[target], updated[index]];
  return updated;
}
