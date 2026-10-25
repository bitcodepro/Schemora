import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { lazy, Suspense, useEffect, useRef, useState } from 'react';
import { errorMessage, request } from './api';
import TablePicker, { qualifiedTableName } from './TablePicker';
import type { GraphTable, JoinPath } from './graphModel';
import { inputFromSqlType } from './recipeTypes';
import type { RecipeDefinition, RecipeSeed } from './recipeTypes';
import './enhancements.css';
import { moveProjection, projectionIssues } from './chainModel';
import type { Projection } from './chainModel';
const SqlEditor = lazy(() => import('./SqlEditor'));
type Preview = { definition: RecipeDefinition; warnings: string[] };
export default function ChainRecipeWizard({ workspaceId, onCreate }: { workspaceId: string; onCreate: (seed: RecipeSeed) => void }) {
  useLocale();
  const [fromTable, setFromTable] = useState<GraphTable | null>(null);
  const [toTable, setToTable] = useState<GraphTable | null>(null);
  const [inputColumn, setInputColumn] = useState('');
  const [name, setName] = useState('');
  const [paths, setPaths] = useState<JoinPath[] | null>(null);
  const [pathIndex, setPathIndex] = useState<number | null>(null);
  const [tables, setTables] = useState<GraphTable[]>([]);
  const [columns, setColumns] = useState<Projection[]>([]);
  const [preview, setPreview] = useState<Preview | null>(null);
  const [busy, setBusy] = useState(false);
  const [tablesBusy, setTablesBusy] = useState(false);
  const [error, setError] = useState('');
  const controller = useRef<AbortController | null>(null);
  const revision = useRef(0);
  const selectedPath = pathIndex === null ? null : paths?.[pathIndex] || null;
  useEffect(() => {
    controller.current?.abort();
    revision.current++;
    setFromTable(null);
    setToTable(null);
    setInputColumn('');
    setPaths(null);
    setPathIndex(null);
    setTables([]);
    setColumns([]);
    setPreview(null);
    setError('');
    setBusy(false);
    setTablesBusy(false);
    setName('');
  }, [workspaceId]);
  useEffect(() => () => controller.current?.abort(), []);
  function changedRoute() {
    controller.current?.abort();
    revision.current++;
    setPaths(null);
    setPathIndex(null);
    setTables([]);
    setColumns([]);
    setPreview(null);
    setError('');
    setBusy(false);
    setTablesBusy(false);
  }
  async function findPaths() {
    if (!fromTable || !toTable || fromTable.id === toTable.id) return;
    controller.current?.abort();
    const requestController = new AbortController();
    controller.current = requestController;
    const current = ++revision.current;
    setBusy(true);
    setError('');
    setPaths(null);
    setPathIndex(null);
    setPreview(null);
    setTables([]);
    setColumns([]);
    try {
      const found = await request<JoinPath[]>(`/api/workspaces/${workspaceId}/paths`, {
        method: 'POST',
        body: JSON.stringify({ fromTableId: fromTable.id, toTableId: toTable.id }),
        signal: requestController.signal,
      });
      if (current === revision.current) setPaths(found);
    } catch (cause) {
      if (!requestController.signal.aborted) setError(errorMessage(cause, t('Не удалось найти маршрут.')));
    } finally {
      if (current === revision.current) setBusy(false);
    }
  }
  async function choosePath(index: number) {
    const selected = paths?.[index];
    if (!selected) return;
    controller.current?.abort();
    const requestController = new AbortController();
    controller.current = requestController;
    const current = ++revision.current;
    setPathIndex(index);
    setTables([]);
    setColumns([]);
    setPreview(null);
    setTablesBusy(true);
    setError('');
    try {
      const loaded = await Promise.all(
        selected.tableIds.map(id =>
          request<GraphTable>(`/api/workspaces/${workspaceId}/tables/${encodeURIComponent(id)}`, { signal: requestController.signal }),
        ),
      );
      if (current === revision.current) {
        setTables(loaded);
        setName(previous => previous || `${fromTable?.name} → ${toTable?.name}`);
      }
    } catch (cause) {
      if (!requestController.signal.aborted)
        setError(errorMessage(cause, t('Колонки маршрута недоступны. Обновите схему и найдите путь снова.')));
    } finally {
      if (current === revision.current) setTablesBusy(false);
    }
  }
  function toggleColumn(tableId: string, columnName: string) {
    setPreview(null);
    setColumns(previous =>
      previous.some(column => column.tableId === tableId && column.columnName === columnName)
        ? previous.filter(column => column.tableId !== tableId || column.columnName !== columnName)
        : [...previous, { tableId, columnName }],
    );
  }
  async function buildPreview() {
    if (!fromTable || !toTable || !selectedPath || !columns.length || !inputColumn || busy || tablesBusy) return;
    controller.current?.abort();
    const requestController = new AbortController();
    controller.current = requestController;
    const current = ++revision.current;
    setBusy(true);
    setError('');
    setPreview(null);
    try {
      const result = await request<Preview>(`/api/workspaces/${workspaceId}/recipe-preview`, {
        method: 'POST',
        body: JSON.stringify({
          fromTableId: fromTable.id,
          toTableId: toTable.id,
          relationIds: selectedPath.relationIds,
          inputColumn,
          inputName: 'Ids',
          columns,
          name: name.trim() || undefined,
        }),
        signal: requestController.signal,
      });
      if (current === revision.current) setPreview(result);
    } catch (cause) {
      if (!requestController.signal.aborted)
        setError(errorMessage(cause, t('Не удалось подготовить SQL. Проверьте колонки и выбранные связи.')));
    } finally {
      if (current === revision.current) setBusy(false);
    }
  }
  function create() {
    if (!preview) return;
    const definition = preview.definition;
    onCreate({
      workspaceId,
      name: definition.name,
      sqlTemplate: definition.sqlTemplate,
      databaseIds: definition.databaseIds,
      inputs: definition.inputs,
      definition,
    });
  }
  const projectionErrors = projectionIssues(columns);
  const input = fromTable?.columns.find(column => column.name === inputColumn);
  return (
    <details className="enhancement-card chain-wizard">
      <summary>{t('Создать рецепт по цепочке таблиц')}</summary>
      <p className="muted-text">
        {t('Начните со списка ID в исходной таблице. Выберите проверенный маршрут и поля результата из связанных таблиц.')}
      </p>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      <div className="chain-step">
        <div className="chain-step-heading">
          <span>1</span>
          <h3>{t('Откуда начинаются ID и к какой таблице идти')}</h3>
        </div>
        <div className="enhancement-grid">
          <TablePicker
            workspaceId={workspaceId}
            label={t('Цепочка: исходная таблица')}
            value={fromTable}
            disabled={busy}
            onChange={table => {
              changedRoute();
              setFromTable(table);
              setInputColumn('');
            }}
          />
          <TablePicker
            workspaceId={workspaceId}
            label={t('Цепочка: целевая таблица')}
            value={toTable}
            disabled={busy}
            onChange={table => {
              changedRoute();
              setToTable(table);
            }}
          />
        </div>
        <div className="enhancement-grid">
          <label>
            <span className="field-label">{t('Поле исходного списка ID')}</span>
            <select
              aria-label={t('Поле ID для цепочки')}
              value={inputColumn}
              disabled={!fromTable || busy}
              onChange={event => {
                setInputColumn(event.target.value);
                setPreview(null);
              }}
            >
              <option value="">{t('Выберите колонку')}</option>
              {fromTable?.columns.map(column => (
                <option key={column.name} value={column.name} disabled={!inputFromSqlType('Ids', column.dataType)}>
                  {column.name} · {column.dataType}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span className="field-label">{t('Название будущего рецепта')}</span>
            <input
              aria-label={t('Название рецепта по цепочке')}
              value={name}
              maxLength={160}
              disabled={busy}
              onChange={event => {
                setName(event.target.value);
                setPreview(null);
              }}
            />
          </label>
        </div>
        <div className="enhancement-actions">
          <button
            className="secondary-button"
            disabled={!fromTable || !toTable || fromTable.id === toTable.id || busy || !inputColumn}
            onClick={() => void findPaths()}
          >
            {busy ? t('Поиск…') : t('Найти маршруты для рецепта')}
          </button>
          {input && (
            <p className="muted-text">
              {t('Тип списка ID:')} {input.dataType}
              {t('. Значения будут вводиться после сохранения рецепта.')}
            </p>
          )}
        </div>
      </div>
      {paths && (
        <div className="chain-step">
          <div className="chain-step-heading">
            <span>2</span>
            <h3>{t('Выберите маршрут')}</h3>
          </div>
          {!paths.length ? (
            <p className="object-warning">
              {t('Доверенный маршрут не найден. Создайте ручную связь или проверьте существующие связи в графе.')}
            </p>
          ) : (
            <div className="chain-route-list">
              {paths.map((route, index) => (
                <button
                  key={`${route.relationIds.join(':')}:${index}`}
                  className={pathIndex === index ? 'selected' : ''}
                  disabled={busy}
                  onClick={() => void choosePath(index)}
                >
                  <strong>
                    {t('Маршрут')} {index + 1} · {route.tableIds.length} {t('таблиц ·')} {route.relationIds.length} {t('связей')}
                  </strong>
                  <small>
                    {route.explanations?.map(tMessage).join(' · ') ||
                      t('Связи проверены сервером. Выберите маршрут для просмотра колонок.')}
                  </small>
                  {route.warnings?.map((warning, warningIndex) => (
                    <p className="object-warning" key={warningIndex}>
                      {tMessage(warning)}
                    </p>
                  ))}
                </button>
              ))}
            </div>
          )}
        </div>
      )}
      {selectedPath && (
        <div className="chain-step">
          <div className="chain-step-heading">
            <span>3</span>
            <h3>{t('Какие поля включить в результат')}</h3>
          </div>
          {tablesBusy ? (
            <p className="muted-text">{t('Читаем колонки таблиц…')}</p>
          ) : (
            <>
              <div className="chain-column-groups">
                {tables.map(table => (
                  <section className="chain-column-group" key={table.id}>
                    <h4>{qualifiedTableName(table)}</h4>
                    <div>
                      {table.columns.map(column => (
                        <label className="chain-column-check" key={column.name}>
                          <input
                            type="checkbox"
                            aria-label={t('Результат: {0}.{1}', table.name, column.name)}
                            checked={columns.some(selected => selected.tableId === table.id && selected.columnName === column.name)}
                            disabled={busy}
                            onChange={() => toggleColumn(table.id, column.name)}
                          />
                          <span>{column.name}</span>
                          <small>{column.dataType}</small>
                        </label>
                      ))}
                    </div>
                  </section>
                ))}
              </div>
              <div className="chain-projection-list">
                {columns.map((column, index) => (
                  <div className="chain-projection-row" key={`${column.tableId}:${column.columnName}`}>
                    <span>
                      {index + 1}. {tables.find(table => table.id === column.tableId)?.name}.{column.columnName}
                    </span>
                    <input
                      aria-label={t('Имя результата {0}', index + 1)}
                      value={column.alias ?? column.columnName}
                      maxLength={128}
                      disabled={busy}
                      onChange={event => {
                        setPreview(null);
                        setColumns(previous =>
                          previous.map((item, current) => (current === index ? { ...item, alias: event.target.value } : item)),
                        );
                      }}
                    />
                    <button
                      type="button"
                      className="secondary-button"
                      aria-label={t('Поле {0} выше', index + 1)}
                      disabled={busy || index === 0}
                      onClick={() => {
                        setPreview(null);
                        setColumns(previous => moveProjection(previous, index, -1));
                      }}
                    >
                      ↑
                    </button>
                    <button
                      type="button"
                      className="secondary-button"
                      aria-label={t('Поле {0} ниже', index + 1)}
                      disabled={busy || index === columns.length - 1}
                      onClick={() => {
                        setPreview(null);
                        setColumns(previous => moveProjection(previous, index, 1));
                      }}
                    >
                      ↓
                    </button>
                  </div>
                ))}
              </div>
              {columns.length > 0 &&
                projectionErrors.map(issue => (
                  <p className="job-error" key={issue}>
                    {issue}
                  </p>
                ))}
              <div className="enhancement-actions">
                <button
                  className="primary-button"
                  disabled={projectionErrors.length > 0 || !inputColumn || busy || !tables.length}
                  onClick={() => void buildPreview()}
                >
                  {busy ? t('Подготовка SQL…') : t('Предварительный SQL рецепта')}
                </button>
                <p className="muted-text">
                  {t('Выбрано полей:')} {columns.length}
                  {t('. Порядок выбора определяет порядок колонок результата.')}
                </p>
              </div>
            </>
          )}
        </div>
      )}
      {preview && (
        <div className="chain-step chain-preview">
          <div className="chain-step-heading">
            <span>4</span>
            <h3>{t('Проверьте SQL и создайте черновик')}</h3>
          </div>
          {preview.warnings?.map((warning, index) => (
            <p className="object-warning" key={index}>
              {tMessage(warning)}
            </p>
          ))}
          <Suspense fallback={<p className="object-loading">{t('Загрузка SQL-редактора…')}</p>}>
            <SqlEditor label={t('Предварительный SQL цепочки')} value={preview.definition.sqlTemplate} readOnly height={300} />
          </Suspense>
          <div className="enhancement-actions">
            <button className="primary-button" onClick={create}>
              {t('Создать черновик из цепочки')}
            </button>
            <p className="muted-text">{t('Шаблон откроется в редакторе. Сохранение рецепта выполняется отдельно.')}</p>
          </div>
        </div>
      )}
    </details>
  );
}
