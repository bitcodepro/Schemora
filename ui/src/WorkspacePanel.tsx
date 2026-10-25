import { localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import './workspace.css';
import { errorMessage, request, sessionToken } from './api';
import AutoRefreshPanel from './AutoRefreshPanel';
import { useDisplayPath, useWorkspaces } from './workspaceContext';
import type { Catalog, Database, Graph, Job, Snapshot, Workspace } from './types';

type Props = { onOpenSchema: (graph: Graph, database: Database, snapshotId?: string) => void };
type ScanMode = 'quick' | 'full' | 'rebuild';
const terminalStatuses = new Set(['completed', 'partial', 'failed', 'cancelled']);
const labels: Record<string, string> = {
  queued: 'В очереди',
  running: 'Выполняется',
  completed: 'Готово',
  partial: 'Частичный результат',
  failed: 'Ошибка',
  cancelled: 'Отменено',
  ready: 'Готова',
  analyzed: 'Проанализирована',
  discovered: 'Обнаружена',
  pending: 'Ожидает анализа',
  warning: 'Есть замечания',
  broken: 'Ошибка',
  quick: 'Quick Refresh',
  full: 'Full Rescan',
  rebuild: 'Rebuild Schema',
};
const sourceKindLabel = (value: string) =>
  t(
    (
      {
        sqlProject: 'SQL project',
        csharpProject: 'C# project',
        solution: 'Solution',
        repository: 'Repository',
        sql: 'SQL file',
        csharp: 'C# file',
        buildConfig: 'Build configuration',
        config: 'Configuration',
      } as Record<string, string>
    )[value] || value,
  );
const lines = (value: string) =>
  value
    .split(/\r?\n/)
    .map(line => line.trim())
    .filter(Boolean);
const formatDate = (value?: string) => (value ? new Date(value).toLocaleString(localeTag()) : '—');
const statusClass = (status: string) =>
  ['completed', 'ready', 'analyzed'].includes(status.toLowerCase())
    ? 'confirmed-tag'
    : ['failed', 'broken'].includes(status.toLowerCase())
      ? 'failure-tag'
      : 'neutral-tag';
const label = (value: string) => t(labels[value.toLowerCase().replace(/^scan:/, '')]) || value;

export default function WorkspacePanel({ onOpenSchema }: Props) {
  useLocale();
  const { workspaces, workspaceId: activeId, select: setActiveId, loading: listBusy, upsert } = useWorkspaces();
  const displayPath = useDisplayPath();
  const activeIdRef = useRef(activeId);
  activeIdRef.current = activeId;
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [catalogBusy, setCatalogBusy] = useState(false);
  const [error, setError] = useState('');
  const [editing, setEditing] = useState(false);
  const [draftId, setDraftId] = useState('');
  const [draftName, setDraftName] = useState('');
  const [draftRoots, setDraftRoots] = useState('');
  const [draftExcludes, setDraftExcludes] = useState('');
  const [saving, setSaving] = useState(false);
  const [trustProjects, setTrustProjects] = useState(false);
  const [selectedRebuildId, setSelectedRebuildId] = useState('');
  const [startingScan, setStartingScan] = useState(false);
  const [jobId, setJobId] = useState('');
  const [job, setJob] = useState<Job | null>(null);
  const [pollPaused, setPollPaused] = useState(false);
  const [pollError, setPollError] = useState('');
  const [cancelling, setCancelling] = useState(false);
  const [sourceQuery, setSourceQuery] = useState('');
  const [sourceFilter, setSourceFilter] = useState('all');
  const [openingId, setOpeningId] = useState('');
  const [snapshotDatabaseId, setSnapshotDatabaseId] = useState('');
  const [snapshots, setSnapshots] = useState<Snapshot[] | null>(null);
  const [snapshotsBusy, setSnapshotsBusy] = useState(false);
  const [snapshotError, setSnapshotError] = useState('');

  const workspace = workspaces.find(item => item.id === activeId);
  const scanActive = Boolean(jobId && (!job || !terminalStatuses.has(job.status.toLowerCase())));

  async function loadCatalog(id: string, signal?: AbortSignal) {
    const loaded = await request<Catalog>(`/api/workspaces/${encodeURIComponent(id)}/catalog`, { signal });
    if (activeIdRef.current === id && !signal?.aborted) setCatalog(loaded);
    return loaded;
  }

  useEffect(() => {
    if (!listBusy && sessionToken && !workspaces.length) setEditing(true);
  }, [listBusy, workspaces.length]);

  useEffect(() => {
    setCatalog(null);
    setJob(null);
    setPollError('');
    setPollPaused(false);
    setCancelling(false);
    setTrustProjects(false);
    setSelectedRebuildId('');
    setSnapshotDatabaseId('');
    setSnapshots(null);
    if (activeId) setEditing(false);
    if (!activeId || !sessionToken) {
      setJobId('');
      return;
    }
    setJobId(sessionStorage.getItem(`schemora.job.${activeId}`) || '');
    const controller = new AbortController();
    setCatalogBusy(true);
    loadCatalog(activeId, controller.signal)
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Не удалось загрузить каталог.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setCatalogBusy(false);
      });
    return () => controller.abort();
  }, [activeId]);

  useEffect(() => {
    if (!jobId || pollPaused) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let failures = 0;
    async function poll() {
      try {
        const next = await request<Job>(`/api/jobs/${encodeURIComponent(jobId)}`, { signal: controller.signal });
        if (controller.signal.aborted) return;
        failures = 0;
        setJob(next);
        setPollError('');
        if (terminalStatuses.has(next.status.toLowerCase())) {
          sessionStorage.removeItem(`schemora.job.${next.workspaceId}`);
          setCancelling(false);
          await loadCatalog(next.workspaceId, controller.signal);
          return;
        }
        timer = setTimeout(() => void poll(), 1000);
      } catch (cause) {
        if (controller.signal.aborted) return;
        failures++;
        setPollError(errorMessage(cause, t('Не удалось получить состояние задачи.')));
        if (failures >= 3) setPollPaused(true);
        else timer = setTimeout(() => void poll(), 1000);
      }
    }
    void poll();
    return () => {
      controller.abort();
      if (timer) clearTimeout(timer);
    };
  }, [jobId, pollPaused]);

  async function refreshCatalog() {
    if (!activeId) return;
    setCatalogBusy(true);
    setError('');
    try {
      await loadCatalog(activeId);
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось обновить каталог.')));
    } finally {
      setCatalogBusy(false);
    }
  }

  function editWorkspace(existing?: Workspace) {
    setDraftId(existing?.id || '');
    setDraftName(existing?.name || '');
    setDraftRoots(existing?.roots.join('\n') || '');
    setDraftExcludes(existing?.excludes.join('\n') || '');
    setEditing(true);
    setError('');
  }

  async function saveWorkspace(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (!draftName.trim() || !lines(draftRoots).length || !sessionToken) return;
    setSaving(true);
    setError('');
    try {
      const saved = await request<Workspace>(draftId ? `/api/workspaces/${encodeURIComponent(draftId)}` : '/api/workspaces', {
        method: draftId ? 'PUT' : 'POST',
        body: JSON.stringify({ name: draftName.trim(), roots: lines(draftRoots), excludes: lines(draftExcludes) }),
      });
      upsert(saved);
      setEditing(false);
      if (activeId === saved.id) await loadCatalog(saved.id);
      else setActiveId(saved.id);
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось сохранить рабочую область.')));
    } finally {
      setSaving(false);
    }
  }

  async function startScan(mode: ScanMode) {
    if (!activeId || scanActive || startingScan || (mode === 'rebuild' && !trustProjects)) return;
    setStartingScan(true);
    setError('');
    try {
      const created = await request<Job>(`/api/workspaces/${encodeURIComponent(activeId)}/scan`, {
        method: 'POST',
        body: JSON.stringify({
          mode,
          trustProjects: mode === 'rebuild' && trustProjects,
          ...(mode === 'rebuild' && selectedRebuildId ? { databaseId: selectedRebuildId } : {}),
        }),
      });
      setJob(created);
      sessionStorage.setItem(`schemora.job.${activeId}`, created.id);
      setPollError('');
      setPollPaused(false);
      setCancelling(false);
      setJobId(created.id);
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось запустить анализ.')));
    } finally {
      setStartingScan(false);
    }
  }

  async function cancelScan() {
    if (!jobId) return;
    setCancelling(true);
    try {
      await request<unknown>(`/api/jobs/${encodeURIComponent(jobId)}`, { method: 'DELETE' });
      setPollPaused(false);
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось отменить задачу.')));
      setCancelling(false);
    }
  }

  async function openDatabase(database: Database, snapshotId?: string) {
    setOpeningId(snapshotId || database.id);
    setError('');
    try {
      const graph = await request<Graph>(
        snapshotId ? `/api/schema/snapshots/${encodeURIComponent(snapshotId)}` : `/api/databases/${encodeURIComponent(database.id)}/schema`,
      );
      onOpenSchema(graph, database, snapshotId);
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось открыть снимок схемы.')));
    } finally {
      setOpeningId('');
    }
  }

  async function showSnapshots(databaseId: string) {
    if (snapshotDatabaseId === databaseId) {
      setSnapshotDatabaseId('');
      return;
    }
    setSnapshotDatabaseId(databaseId);
    setSnapshots(null);
    setSnapshotsBusy(true);
    setSnapshotError('');
    try {
      setSnapshots(await request<Snapshot[]>(`/api/databases/${encodeURIComponent(databaseId)}/snapshots`));
    } catch (cause) {
      setSnapshotError(errorMessage(cause, t('Не удалось загрузить историю снимков.')));
    } finally {
      setSnapshotsBusy(false);
    }
  }

  const sourceKinds = [...new Set(catalog?.sources.map(source => source.kind) || [])].sort();
  const filteredSources = (catalog?.sources || []).filter(
    source =>
      (sourceFilter === 'all' || source.kind === sourceFilter) &&
      `${source.path} ${source.kind}`.toLocaleLowerCase().includes(sourceQuery.toLocaleLowerCase()),
  );
  const visibleSources = filteredSources.slice(0, 250);
  const snapshotDatabase = catalog?.databases.find(database => database.id === snapshotDatabaseId);
  const warningSources = catalog?.sources.filter(source => source.diagnostics.length > 0).length || 0;

  return (
    <div className="workspace-panel">
      <div className="workspace-switcher">
        <div className="workspace-switcher-actions">
          <button className="secondary-button" disabled={scanActive || saving || !sessionToken} onClick={() => editWorkspace()}>
            {t('+ Новая область')}
          </button>
          {workspace && (
            <button className="secondary-button" disabled={scanActive || saving} onClick={() => editWorkspace(workspace)}>
              {t('Настроить папки')}
            </button>
          )}
        </div>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <div>
            <strong>{t('Действие не выполнено')}</strong>
            <p>{tMessage(error)}</p>
          </div>
          <button className="icon-button" onClick={() => setError('')} aria-label={t('Скрыть ошибку')}>
            ×
          </button>
        </div>
      )}
      {editing && (
        <section className="workspace-editor source-card">
          <div className="source-header">
            <div className="section-title">
              <h2>{draftId ? t('Настройки рабочей области') : t('Новая рабочая область')}</h2>
            </div>
            {workspaces.length > 0 && (
              <button className="text-button" disabled={saving} onClick={() => setEditing(false)}>
                {t('Закрыть')}
              </button>
            )}
          </div>
          <form onSubmit={event => void saveWorkspace(event)}>
            <label className="field-label" htmlFor="workspace-name">
              {t('Название')}
            </label>
            <input
              className="workspace-text-input"
              id="workspace-name"
              value={draftName}
              onChange={event => setDraftName(event.target.value)}
              placeholder={t('Например, Backend services')}
              required
              disabled={saving}
            />
            <div className="workspace-form-columns">
              <label>
                <span className="field-label">{t('Корневые папки · по одной в строке')}</span>
                <textarea
                  value={draftRoots}
                  onChange={event => setDraftRoots(event.target.value)}
                  placeholder={'E:\\Projects\\ServiceA\nE:\\Projects\\ServiceB'}
                  rows={4}
                  required
                  disabled={saving}
                  spellCheck={false}
                />
              </label>
              <label>
                <span className="field-label">{t('Исключения · glob-шаблоны по одному в строке')}</span>
                <textarea
                  value={draftExcludes}
                  onChange={event => setDraftExcludes(event.target.value)}
                  placeholder={'**/generated/**\n**/archive/**'}
                  rows={4}
                  disabled={saving}
                  spellCheck={false}
                />
              </label>
            </div>
            <div className="workspace-form-footer">
              <p>{t('Сканирование читает исходники. Служебные данные и снимки хранятся отдельно от репозиториев.')}</p>
              <button
                className="primary-button"
                type="submit"
                disabled={saving || !sessionToken || !draftName.trim() || !lines(draftRoots).length}
              >
                {saving ? t('Сохранение…') : draftId ? t('Сохранить настройки') : t('Создать область')}
              </button>
            </div>
          </form>
        </section>
      )}
      {workspace && (
        <>
          <section className="workspace-overview">
            <div className="workspace-summary-heading">
              <div>
                <span className="eyebrow">{t('РАБОЧАЯ ОБЛАСТЬ')}</span>
                <h2>{workspace.name}</h2>
              </div>
              <button className="secondary-button" disabled={catalogBusy} onClick={() => void refreshCatalog()}>
                {catalogBusy ? t('Обновление…') : t('Обновить каталог')}
              </button>
            </div>
            <div className="workspace-roots">
              {workspace.roots.map(root => (
                <code key={root}>{root}</code>
              ))}
            </div>
            <div className="workspace-metrics">
              <div>
                <strong>{catalog?.databases.length ?? '—'}</strong>
                <span>{t('баз данных')}</span>
              </div>
              <div>
                <strong>{catalog?.sources.length ?? '—'}</strong>
                <span>{t('источников')}</span>
              </div>
              <div>
                <strong>{warningSources}</strong>
                <span>{t('с диагностикой')}</span>
              </div>
              <div>
                <strong>{workspace.roots.length}</strong>
                <span>{t('корневых папок')}</span>
              </div>
            </div>
          </section>
          <section className="scan-controls source-card">
            <div className="source-header">
              <div className="section-title">
                <h2>{t('Обновление модели')}</h2>
              </div>
              <span className="quiet-label">{t('Фоновые задачи')}</span>
            </div>
            <div className="scan-actions">
              <button
                className="primary-button"
                disabled={!sessionToken || scanActive || startingScan || saving || editing}
                onClick={() => void startScan('quick')}
              >
                {t('Quick Refresh')}
              </button>
              <button
                className="secondary-button"
                disabled={!sessionToken || scanActive || startingScan || saving || editing}
                onClick={() => void startScan('full')}
              >
                {t('Full Rescan')}
              </button>
              <p>{t('Quick: изменённые файлы. Full: повторное обнаружение всех источников. Оба режима работают без сборки проектов.')}</p>
            </div>
            <details className="rebuild-options">
              <summary>{t('Пересобрать SQL-проект / DACPAC')}</summary>
              <div className="rebuild-body">
                <label className="field-label" htmlFor="rebuild-database">
                  {t('Область пересборки')}
                </label>
                <select
                  id="rebuild-database"
                  value={selectedRebuildId}
                  onChange={event => setSelectedRebuildId(event.target.value)}
                  disabled={scanActive}
                >
                  <option value="">{t('Все найденные базы')}</option>
                  {catalog?.databases.map(database => (
                    <option value={database.id} key={database.id}>
                      {database.name} · {sourceKindLabel(database.sourceKind)}
                    </option>
                  ))}
                </select>
                <label className="trust-checkbox">
                  <input
                    type="checkbox"
                    checked={trustProjects}
                    disabled={scanActive}
                    onChange={event => setTrustProjects(event.target.checked)}
                  />
                  <span>{t('Я доверяю выбранным SQL-проектам. Пересборка может выполнять код MSBuild и сторонние build targets.')}</span>
                </label>
                <button
                  className="secondary-button"
                  disabled={!sessionToken || scanActive || startingScan || saving || editing || !trustProjects}
                  onClick={() => void startScan('rebuild')}
                >
                  {t('Rebuild Schema')}
                </button>
              </div>
            </details>
            {jobId && (
              <div className={`job-panel ${job && terminalStatuses.has(job.status.toLowerCase()) ? 'finished' : ''}`} role="status">
                <div className="job-heading">
                  <strong>{job ? label(job.kind) : t('Задача анализа')}</strong>
                  <span className={job ? statusClass(job.status) : 'neutral-tag'}>
                    {job ? label(job.status) : t('Получение состояния…')}
                  </span>
                  {scanActive && (
                    <button className="text-button danger-text" onClick={() => void cancelScan()} disabled={cancelling}>
                      {cancelling ? t('Отмена запрошена…') : t('Отменить')}
                    </button>
                  )}
                </div>
                <div className="job-stage">
                  <span>{tMessage(job?.stage) || t('Ожидаем информацию от сервера')}</span>
                  <span>{Math.round(Math.max(0, Math.min(100, job?.progress || 0)))}%</span>
                </div>
                <progress aria-label={t('Прогресс анализа')} max="100" value={Math.max(0, Math.min(100, job?.progress || 0))} />
                {job?.error && <p className="job-error">{tMessage(job.error)}</p>}
                {pollError && (
                  <p className="job-error">
                    {tMessage(pollError)}
                    {pollPaused && (
                      <button
                        className="text-button"
                        onClick={() => {
                          setPollError('');
                          setPollPaused(false);
                        }}
                      >
                        {t('Повторить получение статуса')}
                      </button>
                    )}
                  </p>
                )}
                <div className="job-timing">
                  {job?.startedAt && (
                    <span>
                      {t('Начало:')} {formatDate(job.startedAt)}
                    </span>
                  )}
                  {job?.finishedAt && (
                    <span>
                      {t('Завершение:')} {formatDate(job.finishedAt)}
                    </span>
                  )}
                </div>
              </div>
            )}
          </section>
          <AutoRefreshPanel
            workspaceId={activeId}
            onJob={id => {
              setJobId(id);
              sessionStorage.setItem(`schemora.job.${activeId}`, id);
              setPollPaused(false);
              setPollError('');
              setCancelling(false);
            }}
          />
          <section className="workspace-databases">
            <div className="section-heading">
              <h2>
                {t('Базы данных')} <span className="section-count">{catalog?.databases.length || 0}</span>
              </h2>
              <span className="quiet-label">{t('Каждый проект сохраняет собственную модель')}</span>
            </div>
            {catalogBusy && !catalog ? (
              <div className="panel-placeholder">
                <span className="spinner" />
                {t('Загружаем каталог…')}
              </div>
            ) : catalog?.databases.length ? (
              <div className="database-cards">
                {catalog.databases.map(database => (
                  <article key={database.id} className="database-card">
                    <div className="database-card-title">
                      <div>
                        <span className="object-kind">{database.sourceKind}</span>
                        <h3>{database.name}</h3>
                      </div>
                      <span className={statusClass(database.status)}>{label(database.status)}</span>
                    </div>
                    <code className="database-source-path" title={database.sourcePath}>
                      {displayPath(database.sourcePath)}
                    </code>
                    <div className="database-meta">
                      <span>
                        {database.lastAnalyzedAt ? t('Обновлена {0}', formatDate(database.lastAnalyzedAt)) : t('Снимок ещё не создан')}
                      </span>
                      {database.status.toLowerCase() === 'partial' && (
                        <p>
                          {t(
                            'Статический анализ дал частичный результат. Проверьте диагностику источников и выполните доверенную пересборку при необходимости.',
                          )}
                        </p>
                      )}
                    </div>
                    <div className="database-actions">
                      <button
                        className="secondary-button"
                        disabled={!database.currentSnapshotId || Boolean(openingId)}
                        onClick={() => void openDatabase(database)}
                      >
                        {openingId === database.id ? t('Открытие…') : t('Открыть схему')}
                      </button>
                      <button
                        className="text-button"
                        disabled={snapshotsBusy && snapshotDatabaseId === database.id}
                        onClick={() => void showSnapshots(database.id)}
                      >
                        {snapshotDatabaseId === database.id ? t('Скрыть историю') : t('Снимки')}
                      </button>
                    </div>
                  </article>
                ))}
              </div>
            ) : (
              <div className="panel-placeholder">
                <strong>{t('Базы пока не обнаружены')}</strong>
                <p>{t('Запустите Full Rescan, чтобы найти SQL-проекты, DACPAC, C#-проекты и отдельные SQL-файлы в указанных папках.')}</p>
              </div>
            )}
          </section>
          {snapshotDatabase && (
            <section className="snapshot-history">
              <div className="section-heading">
                <h2>
                  {t('Снимки ·')} {snapshotDatabase.name}
                </h2>
                <button className="text-button" onClick={() => setSnapshotDatabaseId('')}>
                  {t('Закрыть')}
                </button>
              </div>
              {snapshotsBusy ? (
                <div className="panel-placeholder">
                  <span className="spinner" />
                  {t('Загрузка снимков…')}
                </div>
              ) : snapshotError ? (
                <div className="alert error">
                  <p>{tMessage(snapshotError)}</p>
                </div>
              ) : snapshots?.length ? (
                <div className="workspace-table-scroll">
                  <table className="workspace-table">
                    <thead>
                      <tr>
                        <th>{t('Создан')}</th>
                        <th>{t('Режим')}</th>
                        <th>{t('Таблицы / связи')}</th>
                        <th>{t('Fingerprint')}</th>
                        <th />
                      </tr>
                    </thead>
                    <tbody>
                      {snapshots.map(snapshot => (
                        <tr key={snapshot.id}>
                          <td>{formatDate(snapshot.createdAt)}</td>
                          <td>{label(snapshot.mode)}</td>
                          <td>
                            {snapshot.tableCount} / {snapshot.relationCount}
                          </td>
                          <td>
                            <code title={snapshot.fingerprint}>{snapshot.fingerprint.slice(0, 12)}</code>
                          </td>
                          <td>
                            <button
                              className="text-button"
                              disabled={Boolean(openingId)}
                              onClick={() => void openDatabase(snapshotDatabase, snapshot.id)}
                            >
                              {openingId === snapshot.id ? t('Открытие…') : t('Открыть')}
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              ) : (
                <p className="muted-text">{t('Снимков ещё нет.')}</p>
              )}
            </section>
          )}
          <section className="workspace-sources">
            <div className="section-heading">
              <h2>{t('Источники и проекты')}</h2>
              <span className="quiet-label">{t('SQL / C# / solutions / repositories')}</span>
            </div>
            <div className="source-filters">
              <input
                aria-label={t('Поиск источников по пути')}
                value={sourceQuery}
                onChange={event => setSourceQuery(event.target.value)}
                placeholder={t('Поиск по пути или типу…')}
              />
              <select aria-label={t('Фильтр типа источника')} value={sourceFilter} onChange={event => setSourceFilter(event.target.value)}>
                <option value="all">{t('Все типы')}</option>
                {sourceKinds.map(kind => (
                  <option value={kind} key={kind}>
                    {sourceKindLabel(kind)}
                  </option>
                ))}
              </select>
              <span>
                {filteredSources.length} {t('источников')}
              </span>
            </div>
            {visibleSources.length ? (
              <div className="workspace-table-scroll source-table-scroll">
                <table className="workspace-table">
                  <thead>
                    <tr>
                      <th>{t('Тип')}</th>
                      <th>{t('Путь / диагностика')}</th>
                      <th>{t('Изменён')}</th>
                      <th>SHA-256</th>
                    </tr>
                  </thead>
                  <tbody>
                    {visibleSources.map(source => (
                      <tr key={source.id}>
                        <td>
                          <span className="source-kind">{sourceKindLabel(source.kind)}</span>
                        </td>
                        <td className="source-path-cell">
                          <code title={source.path}>{displayPath(source.path)}</code>
                          {source.diagnostics.map((diagnostic, index) => (
                            <p className="source-diagnostic" key={index}>
                              {tMessage(diagnostic)}
                            </p>
                          ))}
                        </td>
                        <td>{formatDate(source.lastWrite)}</td>
                        <td>
                          <code title={source.hash}>{source.hash ? source.hash.slice(0, 10) : '—'}</code>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            ) : (
              <p className="panel-placeholder">
                {sourceQuery || sourceFilter !== 'all'
                  ? t('Источники по этому фильтру не найдены.')
                  : t('Запустите сканирование для заполнения каталога.')}
              </p>
            )}
            {filteredSources.length > visibleSources.length && (
              <p className="workspace-limit-note">
                {t('Показаны первые')} {visibleSources.length} {t('из')} {filteredSources.length} {t('источников. Уточните поиск или тип.')}
              </p>
            )}
          </section>
          {Boolean(catalog?.scanRuns.length) && (
            <section className="scan-history">
              <div className="section-heading">
                <h2>{t('Последние сканирования')}</h2>
              </div>
              <div className="workspace-table-scroll">
                <table className="workspace-table">
                  <thead>
                    <tr>
                      <th>{t('Режим')}</th>
                      <th>{t('Состояние')}</th>
                      <th>{t('Начало')}</th>
                      <th>{t('Обновления')}</th>
                      <th>{t('Завершение / диагностика')}</th>
                    </tr>
                  </thead>
                  <tbody>
                    {catalog?.scanRuns.slice(0, 10).map(run => (
                      <tr key={run.id}>
                        <td>{label(run.mode || run.kind || 'scan')}</td>
                        <td>
                          <span className={statusClass(run.status)}>{label(run.status)}</span>
                        </td>
                        <td>{formatDate(run.startedAt)}</td>
                        <td>
                          {run.changedSources ?? 0} {t('изменённых ·')} {run.unchangedSources ?? 0} {t('без изменений')}
                          <br />
                          {run.databasesUpdated ?? 0} {t('обновлённых БД')}
                        </td>
                        <td>
                          {formatDate(run.finishedAt)}
                          {run.error && <p className="source-diagnostic">{tMessage(run.error)}</p>}
                          {run.diagnostics?.map((diagnostic, index) => (
                            <p className="source-diagnostic" key={index}>
                              {tMessage(diagnostic)}
                            </p>
                          ))}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </section>
          )}
        </>
      )}
      {!workspace && !editing && !listBusy && (
        <div className="panel-placeholder">
          <strong>{t('Выберите рабочую область')}</strong>
          <p>{t('Создайте область с одной или несколькими корневыми папками, чтобы начать обнаружение проектов.')}</p>
        </div>
      )}
    </div>
  );
}
