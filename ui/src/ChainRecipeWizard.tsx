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
      if (!requestController.signal.aborted) setError(errorMessage(cause, t('Could not find a path. #2')));
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
        setError(errorMessage(cause, t('Path columns are unavailable. Refresh the schema and find the path again.')));
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
        setError(errorMessage(cause, t('Could not prepare the SQL. Check the columns and selected relationships.')));
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
      <summary>{t('Create a recipe from a chain of tables')}</summary>
      <p className="muted-text">
        {t('Start with an ID list in the source table. Select a verified path and output columns from related tables.')}
      </p>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      <div className="chain-step">
        <div className="chain-step-heading">
          <span>1</span>
          <h3>{t('Where IDs start and which table to reach')}</h3>
        </div>
        <div className="enhancement-grid">
          <TablePicker
            workspaceId={workspaceId}
            label={t('Chain: source table')}
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
            label={t('Chain: target table')}
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
            <span className="field-label">{t('Source ID list column')}</span>
            <select
              aria-label={t('Chain ID column')}
              value={inputColumn}
              disabled={!fromTable || busy}
              onChange={event => {
                setInputColumn(event.target.value);
                setPreview(null);
              }}
            >
              <option value="">{t('Select a column')}</option>
              {fromTable?.columns.map(column => (
                <option key={column.name} value={column.name} disabled={!inputFromSqlType('Ids', column.dataType)}>
                  {column.name} · {column.dataType}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span className="field-label">{t('New recipe name')}</span>
            <input
              aria-label={t('Chain recipe name')}
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
            {busy ? t('Searching…') : t('Find paths for recipe')}
          </button>
          {input && (
            <p className="muted-text">
              {t('ID list type:')} {input.dataType}
              {t('. Values will be entered after saving the recipe.')}
            </p>
          )}
        </div>
      </div>
      {paths && (
        <div className="chain-step">
          <div className="chain-step-heading">
            <span>2</span>
            <h3>{t('Select a path')}</h3>
          </div>
          {!paths.length ? (
            <p className="object-warning">
              {t('No trusted path found. Create a manual relationship or review existing relationships in the graph.')}
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
                    {t('Path')} {index + 1} · {route.tableIds.length} {t('tables ·')} {route.relationIds.length} {t('relationships')}
                  </strong>
                  <small>
                    {route.explanations?.map(tMessage).join(' · ') ||
                      t('Relationships verified by the server. Select a path to view columns.')}
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
            <h3>{t('Which columns to include in the result')}</h3>
          </div>
          {tablesBusy ? (
            <p className="muted-text">{t('Reading table columns…')}</p>
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
                            aria-label={t('Output: {0}.{1}', table.name, column.name)}
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
                      aria-label={t('Output name {0}', index + 1)}
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
                      aria-label={t('Move column {0} up', index + 1)}
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
                      aria-label={t('Move column {0} down', index + 1)}
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
                  {busy ? t('Preparing SQL…') : t('Preview recipe SQL')}
                </button>
                <p className="muted-text">
                  {t('Selected columns:')} {columns.length}
                  {t('. Selection order determines the order of output columns.')}
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
            <h3>{t('Review the SQL and create a draft')}</h3>
          </div>
          {preview.warnings?.map((warning, index) => (
            <p className="object-warning" key={index}>
              {tMessage(warning)}
            </p>
          ))}
          <Suspense fallback={<p className="object-loading">{t('Loading SQL editor…')}</p>}>
            <SqlEditor label={t('Chain SQL preview')} value={preview.definition.sqlTemplate} readOnly height={300} />
          </Suspense>
          <div className="enhancement-actions">
            <button className="primary-button" onClick={create}>
              {t('Create draft from chain')}
            </button>
            <p className="muted-text">{t('The template will open in the editor. Save the recipe separately.')}</p>
          </div>
        </div>
      )}
    </details>
  );
}
