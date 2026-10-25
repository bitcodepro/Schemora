import SourceLocation from './SourceLocation';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { errorMessage, request } from './api';
import type { Graph } from './types';
import { objectKindNames } from './objectTypes';
import type { CatalogObject, ObjectDetail, ObjectPage } from './objectTypes';
import './enhancements.css';
const SqlEditor = lazy(() => import('./SqlEditor'));
const pageSize = 100;
export default function ObjectExplorer({
  workspaceId = '',
  databaseId = '',
  graph,
  initialObjectId = '',
  initialSearchText = '',
  snapshotId = '',
  onSelect,
}: {
  workspaceId?: string;
  databaseId?: string;
  graph: Graph;
  initialObjectId?: string;
  initialSearchText?: string;
  /** The snapshot the catalog shows; every list and detail request reads it, so history never mixes with the current model. */
  snapshotId?: string;
  /** A choice made here is reported, so the catalog and its address follow it. */
  onSelect?: (objectId: string) => void;
}) {
  useLocale();
  const [kind, setKind] = useState('');
  const [query, setQuery] = useState('');
  const [offset, setOffset] = useState(0);
  const [page, setPage] = useState<ObjectPage | null>(null);
  const [selectedId, setSelected] = useState('');
  function choose(objectId: string) {
    setSelected(objectId);
    onSelect?.(objectId);
  }
  const setSelectedId = setSelected;
  const [detail, setDetail] = useState<ObjectDetail | null>(null);
  const [busy, setBusy] = useState(false);
  const [detailBusy, setDetailBusy] = useState(false);
  const [error, setError] = useState('');
  const [detailError, setDetailError] = useState('');
  const localObjects = useMemo<CatalogObject[]>(
    () => [
      ...graph.tables.map(table => ({ ...table, kind: 'table' as const, databaseId, databaseName: graph.databaseName })),
      ...(graph.objects || []).map(object => ({ ...object, databaseId, databaseName: graph.databaseName })),
    ],
    [graph, databaseId],
  );
  useEffect(() => {
    setOffset(0);
    setSelectedId(initialObjectId);
    setDetail(null);
  }, [workspaceId, databaseId, graph, kind, query, initialObjectId]);
  useEffect(() => {
    const controller = new AbortController();
    setBusy(true);
    setError('');
    const timer = setTimeout(() => {
      if (!workspaceId) {
        const needle = query.toLocaleLowerCase();
        const filtered = localObjects.filter(
          object => (!kind || object.kind === kind) && `${object.schema}.${object.name}`.toLocaleLowerCase().includes(needle),
        );
        const result = { items: filtered.slice(offset, offset + pageSize), total: filtered.length, offset, limit: pageSize };
        setPage(result);
        setSelectedId(previous => previous || result.items[0]?.id || '');
        setBusy(false);
        return;
      }
      const params = new URLSearchParams({ databaseId, offset: String(offset), limit: String(pageSize) });
      if (snapshotId) params.set('snapshotId', snapshotId);
      if (kind) params.set('kind', kind);
      if (query.trim()) params.set('query', query.trim());
      void request<ObjectPage>(`/api/workspaces/${workspaceId}/objects?${params}`, { signal: controller.signal })
        .then(result => {
          setPage(result);
          setSelectedId(previous => previous || result.items[0]?.id || '');
        })
        .catch(cause => {
          if (!controller.signal.aborted) setError(errorMessage(cause, t('Каталог объектов недоступен.')));
        })
        .finally(() => {
          if (!controller.signal.aborted) setBusy(false);
        });
    }, 250);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [workspaceId, databaseId, kind, query, offset, localObjects]);
  useEffect(() => {
    setDetail(null);
    setDetailError('');
    if (!selectedId) return;
    const controller = new AbortController();
    setDetailBusy(true);
    if (!workspaceId) {
      const object = localObjects.find(item => item.id === selectedId);
      if (object)
        setDetail({
          ...object,
          indexes: (graph.indexes || []).filter(index => index.objectId === selectedId),
          dependencies: (graph.objectDependencies || []).filter(dependency => dependency.fromObjectId === selectedId),
          dependents: (graph.objectDependencies || []).filter(dependency => dependency.toObjectId === selectedId),
        });
      setDetailBusy(false);
      return;
    }
    const pinned = snapshotId ? '?' + new URLSearchParams({ snapshotId }) : '';
    void request<ObjectDetail>(`/api/workspaces/${workspaceId}/objects/${encodeURIComponent(selectedId)}${pinned}`, {
      signal: controller.signal,
    })
      .then(setDetail)
      .catch(cause => {
        if (!controller.signal.aborted) setDetailError(errorMessage(cause, t('Не удалось открыть объект.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setDetailBusy(false);
      });
    return () => controller.abort();
  }, [workspaceId, selectedId, localObjects, graph]);
  const columns = detail?.columns || [];
  const parameters = detail?.parameters || [];
  const indexes = detail?.indexes || [];
  const dependencies = detail?.dependencies || [];
  const dependents = detail?.dependents || [];
  return (
    <section className="object-explorer" aria-label={t('Обозреватель объектов')}>
      <div className="object-explorer-layout">
        <aside className="object-explorer-catalog">
          <input
            type="search"
            aria-label={t('Поиск объектов схемы')}
            value={query}
            onChange={event => setQuery(event.target.value)}
            placeholder={t('Имя объекта…')}
          />
          <select aria-label={t('Тип объекта схемы')} value={kind} onChange={event => setKind(event.target.value)}>
            <option value="">{t('Все объекты')}</option>
            {Object.entries(objectKindNames).map(([key, title]) => (
              <option key={key} value={key}>
                {t(title)}
              </option>
            ))}
          </select>
          <p className="object-note">{busy ? t('Поиск…') : t('Объектов: {0}', page?.total || 0)}</p>
          {error && (
            <p className="job-error" role="alert">
              {tMessage(error)}
            </p>
          )}
          <div className="object-list">
            {page?.items.map(object => (
              <button key={object.id} className={selectedId === object.id ? 'selected' : ''} onClick={() => choose(object.id)}>
                <small>
                  {t(objectKindNames[object.kind]) || object.kind} · {object.databaseName}
                </small>
                <strong>
                  {object.schema}.{object.name}
                </strong>
              </button>
            ))}
          </div>
          {!busy && !page?.items.length && <p className="object-no-data">{t('Объектов по этому фильтру нет.')}</p>}
          {Boolean(page && page.total > pageSize) && (
            <div className="enhancement-actions">
              <button
                className="text-button"
                disabled={offset === 0 || busy}
                onClick={() => setOffset(value => Math.max(0, value - pageSize))}
              >
                {t('← Назад')}
              </button>
              <span className="quiet-label">
                {offset + 1}–{Math.min(offset + pageSize, page?.total || 0)}
              </span>
              <button
                className="text-button"
                disabled={busy || offset + pageSize >= (page?.total || 0)}
                onClick={() => setOffset(value => value + pageSize)}
              >
                {t('Далее →')}
              </button>
            </div>
          )}
        </aside>
        <div className="object-explorer-detail">
          {detailBusy ? (
            <p className="object-loading">{t('Читаем метаданные объекта…')}</p>
          ) : detailError ? (
            <p className="job-error" role="alert">
              {tMessage(detailError)}
            </p>
          ) : !detail ? (
            <p className="object-no-data">{t('Выберите объект в каталоге.')}</p>
          ) : (
            <>
              <span className="object-kind">{t(objectKindNames[detail.kind]) || detail.kind}</span>
              <h3>
                {detail.schema}.{detail.name}
              </h3>
              <div className="object-detail-meta">
                <span className="neutral-tag">{detail.databaseName}</span>
                {detail.isPartial && <span className="neutral-tag">{t('Частичные метаданные')}</span>}
                {detail.primaryKey?.length ? <span className="neutral-tag">PK · {detail.primaryKey.join(', ')}</span> : null}
                {detail.returnType && (
                  <span className="neutral-tag">
                    {t('Возвращает')} {detail.returnType}
                  </span>
                )}
              </div>
              {detail.target && (
                <p className="object-note">
                  {t('Ссылается на:')} <code>{detail.target}</code>
                </p>
              )}
              {detail.isPartial && (
                <p className="object-warning">
                  {t('Для этого объекта доступны частичные сведения. Неподтверждённые столбцы и связи не используются как точная схема.')}
                </p>
              )}
              {columns.length > 0 && (
                <>
                  <h4 className="object-subheading">
                    {t('Колонки ·')} {columns.length}
                  </h4>
                  <div className="workspace-table-scroll">
                    <table className="column-table">
                      <thead>
                        <tr>
                          <th>{t('Колонка')}</th>
                          <th>{t('Тип')}</th>
                          <th>NULL</th>
                        </tr>
                      </thead>
                      <tbody>
                        {columns.map(column => (
                          <tr key={column.name}>
                            <td className="code-text">{column.name}</td>
                            <td className="type-text">{column.dataType}</td>
                            <td>{column.isNullable ? t('да') : t('нет')}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </>
              )}
              {parameters.length > 0 && (
                <>
                  <h4 className="object-subheading">
                    {t('Параметры ·')} {parameters.length}
                  </h4>
                  <div className="workspace-table-scroll">
                    <table className="column-table">
                      <thead>
                        <tr>
                          <th>{t('Имя')}</th>
                          <th>{t('Тип')}</th>
                          <th>{t('Направление')}</th>
                          <th>NULL</th>
                        </tr>
                      </thead>
                      <tbody>
                        {parameters.map(parameter => (
                          <tr key={parameter.name}>
                            <td className="code-text">{parameter.name}</td>
                            <td className="type-text">{parameter.dataType}</td>
                            <td>{parameter.isOutput ? 'OUTPUT' : 'IN'}</td>
                            <td>{parameter.isNullable ? t('да') : t('нет')}</td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                </>
              )}
              {detail.uniqueKeys?.length ? (
                <>
                  <h4 className="object-subheading">{t('Уникальные ключи')}</h4>
                  {detail.uniqueKeys.map(key => (
                    <p className="object-note" key={key.id}>
                      <strong>{key.name}</strong> · {key.columns.join(', ')}
                    </p>
                  ))}
                </>
              ) : null}
              {indexes.length > 0 && (
                <>
                  <h4 className="object-subheading">
                    {t('Индексы ·')} {indexes.length}
                  </h4>
                  {indexes.map(index => (
                    <article className="manual-relation-item" key={index.id}>
                      <div>
                        <strong>{index.name}</strong>
                        <small>
                          {index.kind}
                          {index.isUnique ? ' · UNIQUE' : ''}
                          {index.isPrimaryKey ? ' · PK' : ''}
                        </small>
                        <code>
                          {(index.columns || []).map(column => `${column.name}${column.descending ? ' DESC' : ' ASC'}`).join(', ')}
                        </code>
                        {index.includedColumns?.length ? (
                          <p className="object-note">
                            {t('Включены:')} {index.includedColumns.join(', ')}
                          </p>
                        ) : null}
                        {index.filter && (
                          <p className="object-note">
                            {t('Фильтр:')} <code>{index.filter}</code>
                          </p>
                        )}
                      </div>
                    </article>
                  ))}
                </>
              )}
              {(['dependencies', 'dependents'] as const).map(side => {
                const items = side === 'dependencies' ? dependencies : dependents;
                return (
                  items.length > 0 && (
                    <section key={side}>
                      <h4 className="object-subheading">
                        {side === 'dependencies' ? t('Использует объекты') : t('Зависимые объекты')} · {items.length}
                      </h4>
                      {items.map(dependency => (
                        <article className="manual-relation-item" key={dependency.id}>
                          <div>
                            {dependency.toObjectId && side === 'dependencies' ? (
                              <button className="text-button" onClick={() => choose(dependency.toObjectId!)}>
                                {dependency.targetName}
                              </button>
                            ) : (
                              <strong>
                                {side === 'dependents'
                                  ? localObjects.find(object => object.id === dependency.fromObjectId)?.name || t('Зависимый объект')
                                  : dependency.targetName}
                              </strong>
                            )}
                            <small>
                              {dependency.kind} · {Math.round(dependency.confidence * 100)}%
                            </small>
                            {dependency.sourcePath && <SourceLocation path={dependency.sourcePath} line={dependency.line} />}
                            {!dependency.toObjectId && <p className="object-note">{t('Целевой объект не разрешён в текущей модели.')}</p>}
                          </div>
                          {side === 'dependents' && (
                            <button className="text-button" onClick={() => choose(dependency.fromObjectId)}>
                              {t('Открыть объект')}
                            </button>
                          )}
                        </article>
                      ))}
                    </section>
                  )
                );
              })}
              {detail.impact && (
                <p className="object-note">
                  {t('Область влияния:')} {(detail.impact.objectIds || []).length} {t('объектов ·')}{' '}
                  {(detail.impact.recipeIds || []).length} {t('рецептов. Совместимость рецептов проверяется при обновлении схемы.')}
                </p>
              )}
              {detail.definition ? (
                <details className="object-definition" open={initialSearchText && detail.id === initialObjectId ? true : undefined}>
                  <summary>{t('Определение SQL')}</summary>
                  {detail.sourcePath && <SourceLocation path={detail.sourcePath} line={detail.line} />}
                  <Suspense fallback={<p className="object-loading">{t('Загрузка SQL-редактора…')}</p>}>
                    <SqlEditor
                      key={detail.id}
                      label={t('Определение объекта SQL')}
                      value={detail.definition}
                      matchText={detail.id === initialObjectId ? initialSearchText : undefined}
                      readOnly
                      height={340}
                    />
                  </Suspense>
                </details>
              ) : (
                detail.kind !== 'table' && <p className="object-note">{t('Определение SQL недоступно в этом источнике.')}</p>
              )}
              {!columns.length && (detail.kind === 'view' || detail.kind === 'tableFunction') && (
                <p className="object-note">
                  {t('Состав результата не определён в текущем источнике. Выполнение SQL для получения колонок не выполнялось.')}
                </p>
              )}
            </>
          )}
        </div>
      </div>
    </section>
  );
}
