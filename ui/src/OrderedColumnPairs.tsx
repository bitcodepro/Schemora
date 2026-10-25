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
        <h3>{t('Relationship columns')}</h3>
        <span className="quiet-label">{t('Pairs are matched in order')}</span>
      </div>
      {value.map((pair, index) => {
        const fromColumn = fromTable?.columns.find(column => column.name === pair.fromColumn);
        const toColumn = toTable?.columns.find(column => column.name === pair.toColumn);
        return (
          <div className="column-pair-row" key={index}>
            <span className="column-pair-number">{index + 1}</span>
            <div>
              <select
                aria-label={t('Source column {0}', index + 1)}
                disabled={disabled || !fromTable}
                value={pair.fromColumn}
                onChange={event => update(index, 'fromColumn', event.target.value)}
              >
                <option value="">{t('Select a column')}</option>
                {pair.fromColumn && !fromColumn && (
                  <option value={pair.fromColumn}>
                    {pair.fromColumn} {t('· missing')}
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
                aria-label={t('Target column {0}', index + 1)}
                disabled={disabled || !toTable}
                value={pair.toColumn}
                onChange={event => update(index, 'toColumn', event.target.value)}
              >
                <option value="">{t('Select a column')}</option>
                {pair.toColumn && !toColumn && (
                  <option value={pair.toColumn}>
                    {pair.toColumn} {t('· missing')}
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
                aria-label={t('Move pair {0} up', index + 1)}
                disabled={disabled || index === 0}
                onClick={() => onChange(movePair(value, index, -1))}
              >
                ↑
              </button>
              <button
                type="button"
                className="secondary-button"
                aria-label={t('Move pair {0} down', index + 1)}
                disabled={disabled || index === value.length - 1}
                onClick={() => onChange(movePair(value, index, 1))}
              >
                ↓
              </button>
              <button
                type="button"
                className="text-button danger-text"
                aria-label={t('Delete pair {0}', index + 1)}
                disabled={disabled || value.length < 2}
                onClick={() => onChange(value.filter((_, current) => current !== index))}
              >
                {t('Remove')}
              </button>
            </div>
            {fromColumn && toColumn && fromColumn.dataType.toLowerCase() !== toColumn.dataType.toLowerCase() && (
              <p className="pair-type-note">
                {fromColumn.dataType} → {toColumn.dataType}
                {t('. The server will check type compatibility, length, and keys before saving.')}
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
        {t('+ Add column pair')}
      </button>
    </div>
  );
}
