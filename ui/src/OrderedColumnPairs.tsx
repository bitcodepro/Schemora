import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import type { GraphTable } from './graphModel';
import type { ColumnPair } from './relationEditorModel';
import { movePair } from './relationEditorModel';

export default function OrderedColumnPairs({
  fromTable,
  toTable,
  value,
  onChange,
  disabled = false,
}: {
  fromTable: GraphTable | null;
  toTable: GraphTable | null;
  value: ColumnPair[];
  onChange: (pairs: ColumnPair[]) => void;
  disabled?: boolean;
}) {
  useLocale();
  function update(index: number, side: 'fromColumn' | 'toColumn', column: string) {
    onChange(value.map((pair, current) => (current === index ? { ...pair, [side]: column } : pair)));
  }
  return (
    <div className="ordered-column-pairs">
      <div className="section-heading">
        <h3>{t('Колонки связи')}</h3>
        <span className="quiet-label">{t('Пары сопоставляются по порядку')}</span>
      </div>
      {value.map((pair, index) => {
        const fromColumn = fromTable?.columns.find(column => column.name === pair.fromColumn);
        const toColumn = toTable?.columns.find(column => column.name === pair.toColumn);
        return (
          <div className="column-pair-row" key={index}>
            <span className="column-pair-number">{index + 1}</span>
            <div>
              <select
                aria-label={t('Исходная колонка {0}', index + 1)}
                disabled={disabled || !fromTable}
                value={pair.fromColumn}
                onChange={event => update(index, 'fromColumn', event.target.value)}
              >
                <option value="">{t('Выберите колонку')}</option>
                {pair.fromColumn && !fromColumn && (
                  <option value={pair.fromColumn}>
                    {pair.fromColumn} {t('· отсутствует')}
                  </option>
                )}
                {fromTable?.columns.map(column => (
                  <option key={column.name} value={column.name}>
                    {column.name} · {column.dataType}
                    {column.isNullable ? ' · NULL' : ''}
                  </option>
                ))}
              </select>
            </div>
            <span>→</span>
            <div>
              <select
                aria-label={t('Целевая колонка {0}', index + 1)}
                disabled={disabled || !toTable}
                value={pair.toColumn}
                onChange={event => update(index, 'toColumn', event.target.value)}
              >
                <option value="">{t('Выберите колонку')}</option>
                {pair.toColumn && !toColumn && (
                  <option value={pair.toColumn}>
                    {pair.toColumn} {t('· отсутствует')}
                  </option>
                )}
                {toTable?.columns.map(column => (
                  <option key={column.name} value={column.name}>
                    {column.name} · {column.dataType}
                    {toTable.primaryKey.includes(column.name) ? ' · PK' : ''}
                  </option>
                ))}
              </select>
            </div>
            <div className="column-pair-actions">
              <button
                type="button"
                className="secondary-button"
                aria-label={t('Пару {0} выше', index + 1)}
                disabled={disabled || index === 0}
                onClick={() => onChange(movePair(value, index, -1))}
              >
                ↑
              </button>
              <button
                type="button"
                className="secondary-button"
                aria-label={t('Пару {0} ниже', index + 1)}
                disabled={disabled || index === value.length - 1}
                onClick={() => onChange(movePair(value, index, 1))}
              >
                ↓
              </button>
              <button
                type="button"
                className="text-button danger-text"
                aria-label={t('Удалить пару {0}', index + 1)}
                disabled={disabled || value.length < 2}
                onClick={() => onChange(value.filter((_, current) => current !== index))}
              >
                {t('Удалить')}
              </button>
            </div>
            {fromColumn && toColumn && fromColumn.dataType.toLowerCase() !== toColumn.dataType.toLowerCase() && (
              <p className="pair-type-note">
                {fromColumn.dataType} → {toColumn.dataType}
                {t('. Сервер проверит совместимость типов, длину и ключи перед сохранением.')}
              </p>
            )}
          </div>
        );
      })}
      <button
        type="button"
        className="secondary-button"
        disabled={disabled || !fromTable || !toTable || value.length >= 32}
        onClick={() => onChange([...value, { fromColumn: '', toColumn: '' }])}
      >
        {t('+ Добавить пару колонок')}
      </button>
    </div>
  );
}
