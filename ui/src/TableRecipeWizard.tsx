import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useState } from 'react';
import TablePicker from './TablePicker';
import type { GraphTable } from './graphModel';
import { inputFromSqlType, quoteSqlIdentifier } from './recipeTypes';
import type { RecipeSeed } from './recipeTypes';

export default function TableRecipeWizard({ workspaceId, onCreate }: { workspaceId: string; onCreate: (seed: RecipeSeed) => void }) {
  useLocale();
  const [table, setTable] = useState<GraphTable | null>(null);
  const [columnName, setColumnName] = useState('');
  const selectedColumn = table?.columns.find(column => column.name === columnName);
  const input = selectedColumn ? inputFromSqlType('Ids', selectedColumn.dataType) : null;
  function create() {
    if (!table || !selectedColumn || !input) return;
    onCreate({
      workspaceId,
      name: t('{0} по {1}', table.name, columnName),
      databaseIds: [table.databaseId],
      inputs: [input],
      sqlTemplate: `SELECT [t].*\nFROM ${quoteSqlIdentifier(table.databaseName)}.${quoteSqlIdentifier(table.schema)}.${quoteSqlIdentifier(table.name)} AS [t]\nWHERE [t].${quoteSqlIdentifier(columnName)} IN ({{ids:Ids}});`,
    });
  }
  return (
    <details className="recipe-table-wizard">
      <summary>{t('Создать рецепт по таблице и полю')}</summary>
      <div className="recipe-wizard-body">
        <TablePicker
          workspaceId={workspaceId}
          label={t('Таблица для рецепта')}
          value={table}
          onChange={selected => {
            setTable(selected);
            setColumnName('');
          }}
        />
        <label>
          <span className="field-label">{t('Поле для списка ID')}</span>
          <select
            aria-label={t('Поле для списка ID')}
            value={columnName}
            disabled={!table}
            onChange={event => setColumnName(event.target.value)}
          >
            <option value="">{t('Выберите колонку')}</option>
            {table?.columns.map(column => (
              <option key={column.name} value={column.name} disabled={!inputFromSqlType('Ids', column.dataType)}>
                {column.name} · {column.dataType}
              </option>
            ))}
          </select>
        </label>
        <button className="secondary-button" disabled={!input} onClick={create}>
          {t('Создать черновик')}
        </button>
        <p>{t('Выберите реальное поле. Тип параметра заполнится из схемы; SQL и настройки можно проверить перед сохранением.')}</p>
      </div>
    </details>
  );
}
