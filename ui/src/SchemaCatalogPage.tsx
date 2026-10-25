import { lazy, useEffect, useMemo, useState } from 'react';
import { t, tMessage, localeTag } from './i18n.ts';
import { useLocale } from './i18nReact';
import { sessionToken } from './api';
import type { Graph } from './types';
import { useDisplayPath, useWorkspaces } from './workspaceContext';
import Icon from './Icon';
import { updateRouteParams } from './route';
const ObjectExplorer = lazy(() => import('./ObjectExplorer'));
const listPage = 200;
const overviewLimit = 60;

function downloadGraph(graph: Graph) {
  const url = URL.createObjectURL(new Blob([JSON.stringify(graph, null, 2)], { type: 'application/json' }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = `${graph.databaseName.replace(/[^a-zA-Z0-9а-яА-Я._-]/g, '_')}-schema.json`;
  anchor.click();
  URL.revokeObjectURL(url);
}

export { useSchemaBrowser } from './useSchemaBrowser';
import { useSchemaBrowser } from './useSchemaBrowser';
import { schemaOverview } from './schemaOverview';
export type SchemaBrowser = ReturnType<typeof useSchemaBrowser>;

export default function SchemaCatalogPage({ browser }: { browser: SchemaBrowser }) {
  useLocale();
  const displayPath = useDisplayPath();
  const { workspaceId } = useWorkspaces();
  const {
    sourceMode,
    setSourceMode,
    allowDocker,
    setAllowDocker,
    sourcePath,
    setSourcePath,
    trustProject,
    setTrustProject,
    graph,
    databaseId,
    schemaWorkspaceId,
    busy,
    error,
    setError,
    query,
    setQuery,
    searchObjectId,
    searchCodeText,
    selectedId,
    setSelectedId,
    view,
    setView,
    workspaceDatabases,
    openingDatabaseId,
    loadingCatalog,
    openWorkspaceDatabase,
    openSchema,
  } = browser;
  const tables = graph?.tables || [];
  const selected = tables.find(table => table.id === selectedId) || null;
  const filtered = useMemo(() => {
    const needle = query.toLocaleLowerCase();
    return tables.filter(table =>
      `${table.schema}.${table.name} ${table.columns.map(column => column.name).join(' ')}`.toLocaleLowerCase().includes(needle),
    );
  }, [tables, query]);
  useEffect(() => {
    if (browser.routeReady && !openingDatabaseId && !busy)
      updateRouteParams('schema', { ws: workspaceId, db: databaseId, object: selectedId, view, snap: browser.pinnedSnapshotId || null });
  }, [workspaceId, databaseId, selectedId, view, browser.routeReady, openingDatabaseId, busy, browser.pinnedSnapshotId]);
  // Large catalogs render the list in pages, so typing and selecting stay fast with thousands of tables.
  const [listLimit, setListLimit] = useState(listPage);
  useEffect(() => setListLimit(listPage), [query, graph]);
  // The overview draws every table only for small models; otherwise the selected table and its direct neighbours.
  const overview = useMemo(() => schemaOverview(tables, graph?.relations || [], selected, overviewLimit), [tables, graph, selected]);
  const overviewIndex = useMemo(() => new Map(overview.tables.map((table, index) => [table.id, index])), [overview]);
  const selectedRelations =
    graph?.relations.filter(relation => relation.fromTableId === selected?.id || relation.toTableId === selected?.id) || [];
  const findTable = (id: string) => tables.find(table => table.id === id);
  const tableName = (id: string) => {
    const table = findTable(id);
    return table ? `${table.schema}.${table.name}` : id;
  };
  return (
    <>
      {workspaceDatabases.length > 0 && (
        <section className="workspace-databases" aria-label={t('Databases in this workspace')}>
          <span className="field-label">{t('Databases in this workspace')}</span>
          <div>
            {workspaceDatabases.map(database => (
              <button
                key={database.id}
                className={databaseId === database.id && graph ? 'selected' : ''}
                aria-pressed={databaseId === database.id && Boolean(graph)}
                disabled={Boolean(openingDatabaseId)}
                title={database.sourcePath}
                onClick={() => void openWorkspaceDatabase(database)}
              >
                {openingDatabaseId === database.id && <span className="spinner" />}
                {database.name}
              </button>
            ))}
          </div>
        </section>
      )}
      <section className="source-card" aria-label={t('Источник схемы')}>
        <div className="source-header">
          <div className="section-title">
            <Icon name="folder" />
            <h2>{t('Источник схемы')}</h2>
          </div>
          <span className="quiet-label">{t('Только метаданные')}</span>
        </div>
        <div className="segmented" role="tablist" aria-label={t('Тип источника')}>
          <button
            role="tab"
            aria-selected={sourceMode === 'import'}
            className={sourceMode === 'import' ? 'selected' : ''}
            disabled={busy}
            onClick={() => {
              setSourceMode('import');
              setError('');
            }}
          >
            <Icon name="file" size={15} />
            {t('Готовый DACPAC')}
          </button>
          <button
            role="tab"
            aria-selected={sourceMode === 'build'}
            className={sourceMode === 'build' ? 'selected' : ''}
            disabled={busy}
            onClick={() => {
              setSourceMode('build');
              setError('');
            }}
          >
            <Icon name="code" size={15} />
            SDK / legacy SSDT
          </button>
          <button
            role="tab"
            aria-selected={sourceMode === 'docker'}
            className={sourceMode === 'docker' ? 'selected' : ''}
            disabled={busy}
            onClick={() => {
              setSourceMode('docker');
              setError('');
            }}
          >
            <Icon name="layers" size={15} />
            {t('SQL Server в Docker')}
          </button>
        </div>
        <form onSubmit={event => void openSchema(event)}>
          <label className="field-label" htmlFor="source-path">
            {sourceMode !== 'build' ? t('Путь к файлу .dacpac') : t('Путь к файлу .sqlproj (SDK-style или legacy SSDT)')}
          </label>
          <div className="source-input-row">
            <div className="path-input">
              <Icon name="folder" size={19} />
              <input
                id="source-path"
                type="text"
                value={sourcePath}
                onChange={event => setSourcePath(event.target.value)}
                placeholder={
                  sourceMode !== 'build'
                    ? 'E:\\Projects\\Database\\bin\\Debug\\Database.dacpac'
                    : 'E:\\Projects\\Database\\Database.sqlproj'
                }
                autoComplete="off"
                spellCheck={false}
                disabled={busy}
              />
            </div>
            <button
              className="primary-button"
              type="submit"
              disabled={
                !sessionToken ||
                busy ||
                !sourcePath.trim() ||
                (sourceMode === 'build' && !trustProject) ||
                (sourceMode === 'docker' && !allowDocker)
              }
            >
              {busy ? <span className="spinner" /> : <Icon name="arrow" size={17} />}{' '}
              {busy
                ? sourceMode === 'build'
                  ? t('Сборка проекта…')
                  : t('Чтение модели…')
                : sourceMode === 'build'
                  ? t('Собрать и открыть')
                  : sourceMode === 'docker'
                    ? t('Прочитать через SQL Server')
                    : t('Открыть схему')}
            </button>
          </div>
          {sourceMode === 'build' ? (
            <label className="trust-checkbox">
              <input type="checkbox" checked={trustProject} onChange={event => setTrustProject(event.target.checked)} disabled={busy} />
              <span>{t('Я доверяю этому проекту. Сборка может выполнять код и MSBuild targets из репозитория.')}</span>
            </label>
          ) : sourceMode === 'docker' ? (
            <label className="trust-checkbox">
              <input type="checkbox" checked={allowDocker} onChange={event => setAllowDocker(event.target.checked)} disabled={busy} />
              <span>
                {t(
                  'Создать временный SQL Server контейнер (только схема). DACPAC будет развёрнут изолированно; данные и deployment-скрипты не переносятся.',
                )}
              </span>
            </label>
          ) : (
            <p className="field-hint">
              <Icon name="shield" size={14} />
              {t('Импорт читает модель DACPAC без подключения к базе и выполнения deployment-скриптов.')}
            </p>
          )}
        </form>
        {error && (
          <div className="alert error" role="alert">
            <Icon name="info" />
            <div>
              <strong>{t('Не удалось получить схему')}</strong>
              <p>{tMessage(error)}</p>
            </div>
            <button className="icon-button" aria-label={t('Скрыть ошибку')} onClick={() => setError('')}>
              <Icon name="close" size={16} />
            </button>
          </div>
        )}
      </section>
      {graph ? (
        <>
          <div className="schema-summary">
            <div>
              <span className="database-icon">
                <Icon name="layers" size={21} />
              </span>
              <div>
                <h2>{graph.databaseName}</h2>
                <p title={graph.sourcePath}>{displayPath(graph.sourcePath)}</p>
              </div>
            </div>
            <div className="schema-count">
              <strong>{tables.length}</strong> {t('таблиц')} <span>·</span> <strong>{graph.relations.length}</strong> {t('связей')}
            </div>
            <button className="secondary-button" onClick={() => downloadGraph(graph)}>
              <Icon name="code" size={15} />
              {t('Экспорт JSON')}
            </button>
          </div>
          {graph.diagnostics.length > 0 && (
            <details className="model-diagnostics" open={graph.diagnostics.some(item => item.severity.toLowerCase() === 'error')}>
              <summary>
                {t('Метаданные прочитаны с замечаниями ·')} {graph.diagnostics.length}
              </summary>
              {graph.diagnostics.map((diagnostic, index) => (
                <div
                  key={`${diagnostic.code}-${index}`}
                  className={`alert ${diagnostic.severity.toLowerCase() === 'error' ? 'error' : 'warning'}`}
                >
                  <Icon name="info" />
                  <div>
                    <strong>{diagnostic.code}</strong>
                    <p>{tMessage(diagnostic.message)}</p>
                  </div>
                </div>
              ))}
            </details>
          )}
          <section className="schema-browser" aria-label={t('Каталог объектов')} data-database-id={databaseId || undefined}>
            <div className="browser-toolbar">
              <div className="view-tabs">
                <button className={view === 'tables' ? 'selected' : ''} onClick={() => setView('tables')}>
                  <Icon name="table" size={16} />
                  {t('Таблицы')}
                </button>
                <button className={view === 'graph' ? 'selected' : ''} onClick={() => setView('graph')}>
                  <Icon name="branch" size={16} />
                  {t('Обзор связей')}
                </button>
                <button className={view === 'objects' ? 'selected' : ''} onClick={() => setView('objects')}>
                  <Icon name="file" size={16} />
                  {t('Все объекты')}
                </button>
              </div>
              <span className="quiet-label">{t('Метаданные схемы')}</span>
            </div>
            {view === 'objects' ? (
              <ObjectExplorer
                initialObjectId={searchObjectId}
                initialSearchText={searchCodeText}
                workspaceId={schemaWorkspaceId}
                databaseId={databaseId}
                graph={graph}
                snapshotId={browser.snapshotId}
                onSelect={setSelectedId}
              />
            ) : view === 'tables' ? (
              <div className="browser-body">
                <div className="table-pane">
                  <label className="search-field">
                    <Icon name="search" size={15} />
                    <input
                      aria-label={t('Поиск таблиц и колонок')}
                      placeholder={t('Таблица или колонка…')}
                      value={query}
                      onChange={event => setQuery(event.target.value)}
                    />
                  </label>
                  <div className="table-list">
                    {filtered.length ? (
                      filtered.slice(0, listLimit).map(table => (
                        <button
                          key={table.id}
                          className={`table-item ${selected?.id === table.id ? 'selected' : ''}`}
                          onClick={() => setSelectedId(table.id)}
                        >
                          <Icon name="table" size={16} />
                          <span>
                            <small>{table.schema}</small>
                            {table.name}
                          </span>
                          <em>{table.columns.length}</em>
                        </button>
                      ))
                    ) : (
                      <p className="no-results">{tables.length ? t('Ничего не найдено') : t('В модели нет таблиц')}</p>
                    )}
                    {filtered.length > listLimit && (
                      <button className="text-button table-list-more" onClick={() => setListLimit(limit => limit + listPage)}>
                        {t('Show {0} more of {1}', Math.min(listPage, filtered.length - listLimit), filtered.length - listLimit)}
                      </button>
                    )}
                  </div>
                </div>
                <div className="detail-pane">
                  {selected ? (
                    <>
                      <div className="object-heading">
                        <div>
                          <span className="object-kind">TABLE</span>
                          <h3>
                            <span>{selected.schema}.</span>
                            {selected.name}
                          </h3>
                        </div>
                        <span className="neutral-tag">
                          {selected.columns.length} {t('колонок')}
                        </span>
                      </div>
                      <div className="column-table-wrap">
                        <table className="column-table">
                          <thead>
                            <tr>
                              <th>{t('Колонка')}</th>
                              <th>{t('Тип данных')}</th>
                              <th>NULL</th>
                              <th>{t('Ключ')}</th>
                            </tr>
                          </thead>
                          <tbody>
                            {selected.columns.map(column => (
                              <tr key={column.name}>
                                <td className="code-text">{column.name}</td>
                                <td className="type-text">{column.dataType}</td>
                                <td>
                                  <span className={column.isNullable ? 'nullable' : 'not-null'}>
                                    {column.isNullable ? t('да') : t('нет')}
                                  </span>
                                </td>
                                <td>
                                  {selected.primaryKey.includes(column.name) && (
                                    <span className="key-tag">
                                      <Icon name="key" size={12} />
                                      PK
                                    </span>
                                  )}
                                  {graph.relations.some(
                                    relation => relation.fromTableId === selected.id && relation.fromColumns.includes(column.name),
                                  ) && <span className="fk-tag">FK</span>}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                      <div className="relations-section">
                        <h4>
                          <Icon name="branch" size={16} />
                          {t('Связи таблицы')} <span>{selectedRelations.length}</span>
                        </h4>
                        {selectedRelations.length ? (
                          selectedRelations.map(relation => (
                            <div className="relation-row" key={relation.id}>
                              <div>
                                <strong>{relation.name}</strong>
                                <p>
                                  <button onClick={() => setSelectedId(relation.fromTableId)}>{tableName(relation.fromTableId)}</button>
                                  <Icon name="arrow" size={13} />
                                  <button onClick={() => setSelectedId(relation.toTableId)}>{tableName(relation.toTableId)}</button>
                                </p>
                                <code>
                                  {relation.fromColumns.join(', ')} → {relation.toColumns.join(', ')}
                                </code>
                              </div>
                              <span className="confirmed-tag">
                                <Icon name="check" size={12} />
                                {t('Declared FK')}
                              </span>
                            </div>
                          ))
                        ) : (
                          <p className="muted-text">{t('В модели нет объявленных внешних ключей для этой таблицы.')}</p>
                        )}
                      </div>
                    </>
                  ) : (
                    <div className="detail-empty">
                      <Icon name="table" size={32} />
                      <p>{t('Выберите таблицу в каталоге')}</p>
                    </div>
                  )}
                </div>
              </div>
            ) : (
              <div className="graph-view">
                <div className="graph-note">
                  <Icon name="info" size={15} />
                  {t('Обзор физических FK. Выберите таблицу, чтобы открыть её колонки.')}
                </div>
                {overview.partial && (
                  <p className="muted-text graph-partial" role="status">
                    {t(
                      'The model has {0} tables, so the overview shows {1} and its direct neighbours. Relationships and paths shows any part of the model.',
                      tables.length,
                      (selected || tables[0]).name,
                    )}
                  </p>
                )}
                {overview.tables.length ? (
                  <div className="graph-scroll">
                    <div
                      className="graph-canvas"
                      style={{
                        width: Math.max(3, Math.min(overview.tables.length, 3)) * 285,
                        height: Math.max(1, Math.ceil(overview.tables.length / 3)) * 155 + 20,
                      }}
                    >
                      <svg className="graph-edges" width="100%" height="100%">
                        <defs>
                          <marker id="arrowhead" markerWidth="8" markerHeight="8" refX="7" refY="4" orient="auto">
                            <path d="M0 0 L8 4 L0 8 Z" fill="currentColor" />
                          </marker>
                        </defs>
                        {graph.relations.map(relation => {
                          const from = overviewIndex.get(relation.fromTableId) ?? -1;
                          const to = overviewIndex.get(relation.toTableId) ?? -1;
                          if (from < 0 || to < 0) return null;
                          const x1 = (from % 3) * 285 + 135;
                          const y1 = Math.floor(from / 3) * 155 + 110;
                          const x2 = (to % 3) * 285 + 135;
                          const y2 = Math.floor(to / 3) * 155 + 28;
                          return (
                            <path
                              key={relation.id}
                              d={`M${x1},${y1} C${x1},${y1 + 55} ${x2},${y2 - 55} ${x2},${y2}`}
                              markerEnd="url(#arrowhead)"
                            />
                          );
                        })}
                      </svg>
                      {overview.tables.map((table, index) => (
                        <button
                          key={table.id}
                          className="graph-node"
                          style={{ left: (index % 3) * 285 + 20, top: Math.floor(index / 3) * 155 + 28 }}
                          onClick={() => {
                            setSelectedId(table.id);
                            setView('tables');
                          }}
                        >
                          <span>
                            <Icon name="table" size={16} />
                            <strong>{table.name}</strong>
                          </span>
                          <small>
                            {table.schema} <b>·</b> {table.columns.length} {t('колонок')}
                          </small>
                          <span className="graph-key">
                            <Icon name="key" size={12} />
                            {table.primaryKey.length ? table.primaryKey.join(', ') : t('Нет первичного ключа')}
                          </span>
                        </button>
                      ))}
                    </div>
                  </div>
                ) : (
                  <div className="detail-empty">
                    <Icon name="branch" size={32} />
                    <p>{t('В модели нет таблиц для отображения')}</p>
                  </div>
                )}
              </div>
            )}
          </section>
          <div className="snapshot-note">
            <Icon name="check" size={14} />
            {t('Модель прочитана')} {new Date(graph.extractedAt).toLocaleString(localeTag())}
            <span>{t('Deployment-скрипты не выполнялись')}</span>
          </div>
        </>
      ) : openingDatabaseId || loadingCatalog ? (
        <p className="muted-text schema-loading" role="status">
          {t('Loading the current schema snapshot…')}
        </p>
      ) : (
        <section className="empty-state">
          <div className="empty-illustration">
            <span className="empty-orbit orbit-one" />
            <span className="empty-orbit orbit-two" />
            <span className="empty-center">
              <Icon name="layers" size={39} />
            </span>
            <span className="orbit-node node-one">
              <Icon name="table" size={18} />
            </span>
            <span className="orbit-node node-two">
              <Icon name="key" size={17} />
            </span>
            <span className="orbit-node node-three">
              <Icon name="branch" size={18} />
            </span>
          </div>
          <div className="eyebrow">{t('НАЧНИТЕ С МОДЕЛИ')}</div>
          <h2>{t('Ваша схема — целиком и наглядно')}</h2>
          <p>
            {t('Укажите локальный DACPAC или SQL-проект выше.')}
            <br />
            {t('Schemora покажет таблицы, типы, первичные и внешние ключи.')}
          </p>
          <div className="empty-features">
            <span>
              <Icon name="shield" size={16} />
              {t('Без подключения к рабочей БД')}
            </span>
            <span>
              <Icon name="check" size={16} />
              {t('Без отправки данных в сеть')}
            </span>
          </div>
        </section>
      )}
    </>
  );
}
