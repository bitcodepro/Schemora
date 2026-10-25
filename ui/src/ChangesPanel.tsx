import { getLocale, localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { lazy, Suspense, useEffect, useMemo, useState } from 'react';
import { errorMessage, request } from './api';
import { useWorkspaces } from './workspaceContext';
import type { Catalog, Graph, Snapshot } from './types';
import type { RecipeDiagnostic, RecipeEntry } from './recipeTypes';
import './changes.css';
import { comparisonCsv, comparisonHtml } from './compareExport.ts';
const SqlDiffEditor = lazy(() => import('./SqlEditor').then(module => ({ default: module.SqlDiffEditor })));
const SqlEditor = lazy(() => import('./SqlEditor'));
type Change = { kind: string; tableId: string; schema: string; table: string; column?: string; before?: string; after?: string };
type RenameCandidate = { kind: string; oldTableId: string; newTableId: string; oldName: string; newName: string; reason: string };
type SchemaDiff = {
  databaseId: string;
  fromSnapshotId: string;
  toSnapshotId: string;
  changes: Change[];
  renameCandidates: RenameCandidate[];
  diagnostics: RecipeDiagnostic[];
};
type Impact = {
  recipeId: string;
  name: string;
  version: number;
  status: string;
  diagnostics: RecipeDiagnostic[];
  lastCompatibleSnapshots: Record<string, string>;
};
type Proposal = {
  id: string;
  recipeId: string;
  baseVersion: number;
  status: 'proposed' | 'approved' | 'rejected';
  beforeSql: string;
  afterSql: string;
  replacementCount: number;
  validation: RecipeEntry['validation'];
  reason: string;
  createdAt: string;
  appliedVersion?: number;
};
const dates = (value: string) => new Date(value).toLocaleString(localeTag());
const statusLabels: Record<string, string> = {
  Valid: 'Compatible',
  Warning: 'Has warnings',
  Broken: 'Incompatible',
  NeedsReview: 'Review required',
  proposed: 'Proposed',
  approved: 'Applied',
  rejected: 'Rejected #2',
};
const changeLabels: Record<string, string> = {
  tableAdded: 'Table added',
  tableRemoved: 'Table removed',
  columnAdded: 'Column added',
  columnRemoved: 'Column removed',
  columnTypeChanged: 'Type changed',
  columnNullabilityChanged: 'NULL changed',
  primaryKeyChanged: 'PK changed',
  foreignKeyAdded: 'FK added',
  foreignKeyRemoved: 'FK removed',
  foreignKeyChanged: 'FK changed',
};

export default function ChangesPanel({ onOpenRecipe }: { onOpenRecipe: (workspaceId: string, recipeId: string) => void }) {
  useLocale();
  const { workspaceId } = useWorkspaces();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [databaseId, setDatabaseId] = useState('');
  const [snapshots, setSnapshots] = useState<Snapshot[]>([]);
  const [fromId, setFromId] = useState('');
  const [toId, setToId] = useState('');
  const [diff, setDiff] = useState<SchemaDiff | null>(null);
  const [beforeGraph, setBeforeGraph] = useState<Graph | null>(null);
  const [afterGraph, setAfterGraph] = useState<Graph | null>(null);
  const [diffQuery, setDiffQuery] = useState('');
  const [diffKind, setDiffKind] = useState('');
  const [impact, setImpact] = useState<Impact[]>([]);
  const [recipeId, setRecipeId] = useState('');
  const [recipe, setRecipe] = useState<RecipeEntry | null>(null);
  const [proposals, setProposals] = useState<Proposal[]>([]);
  const [proposalId, setProposalId] = useState('');
  const [tableId, setTableId] = useState('');
  const [oldColumn, setOldColumn] = useState('');
  const [repairKind, setRepairKind] = useState('column');
  const [previousSchema, setPreviousSchema] = useState('');
  const [previousTable, setPreviousTable] = useState('');
  const [newColumn, setNewColumn] = useState('');
  const [reason, setReason] = useState('');
  const [reviewed, setReviewed] = useState(false);
  const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(false);
  const [diffLoading, setDiffLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');

  useEffect(() => {
    setCatalog(null);
    setImpact([]);
    setDatabaseId('');
    setRecipeId('');
    setRecipe(null);
    setProposals([]);
    setProposalId('');
    setError('');
    setNotice('');
    if (!workspaceId) return;
    const controller = new AbortController();
    setLoading(true);
    Promise.all([
      request<Catalog>(`/api/workspaces/${encodeURIComponent(workspaceId)}/catalog`, { signal: controller.signal }),
      request<Impact[]>(`/api/workspaces/${encodeURIComponent(workspaceId)}/impact`, { signal: controller.signal }),
    ])
      .then(([loadedCatalog, impacted]) => {
        setCatalog(loadedCatalog);
        setImpact(impacted);
        setDatabaseId(loadedCatalog.databases[0]?.id || '');
        setRecipeId(impacted.find(item => item.status === 'Broken')?.recipeId || impacted[0]?.recipeId || '');
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Could not load changes.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [workspaceId]);
  useEffect(() => {
    if (!workspaceId || refresh === 0) return;
    const controller = new AbortController();
    Promise.all([
      request<Catalog>(`/api/workspaces/${encodeURIComponent(workspaceId)}/catalog`, { signal: controller.signal }),
      request<Impact[]>(`/api/workspaces/${encodeURIComponent(workspaceId)}/impact`, { signal: controller.signal }),
    ])
      .then(([loadedCatalog, impacted]) => {
        setCatalog(loadedCatalog);
        setImpact(impacted);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('State was not refreshed.')));
      });
    return () => controller.abort();
  }, [refresh, workspaceId]);
  useEffect(() => {
    setSnapshots([]);
    setFromId('');
    setToId('');
    setDiff(null);
    setBeforeGraph(null);
    setAfterGraph(null);
    setTableId('');
    setOldColumn('');
    setNewColumn('');
    setPreviousSchema('');
    setPreviousTable('');
    if (!databaseId) return;
    const controller = new AbortController();
    request<Snapshot[]>(`/api/databases/${encodeURIComponent(databaseId)}/snapshots`, { signal: controller.signal })
      .then(loaded => {
        setSnapshots(loaded);
        setToId(loaded[0]?.id || '');
        setFromId(loaded[1]?.id || '');
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Snapshots unavailable.')));
      });
    return () => controller.abort();
  }, [databaseId, refresh]);
  useEffect(() => {
    setDiff(null);
    setBeforeGraph(null);
    setAfterGraph(null);
    if (!databaseId || !fromId || !toId || fromId === toId) return;
    const controller = new AbortController();
    setDiffLoading(true);
    const parameters = new URLSearchParams({ from: fromId, to: toId });
    Promise.all([
      request<SchemaDiff>(`/api/databases/${encodeURIComponent(databaseId)}/diff?${parameters}`, { signal: controller.signal }),
      request<Graph>(`/api/schema/snapshots/${encodeURIComponent(fromId)}`, { signal: controller.signal }),
      request<Graph>(`/api/schema/snapshots/${encodeURIComponent(toId)}`, { signal: controller.signal }),
    ])
      .then(([comparison, before, after]) => {
        setDiff(comparison);
        setBeforeGraph(before);
        setAfterGraph(after);
        setTableId(after.tables.find(table => before.tables.some(old => old.id === table.id))?.id || after.tables[0]?.id || '');
        setOldColumn('');
        setNewColumn('');
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Comparison failed.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setDiffLoading(false);
      });
    return () => controller.abort();
  }, [databaseId, fromId, toId]);
  useEffect(() => {
    setRecipe(null);
    setProposals([]);
    setProposalId('');
    setReviewed(false);
    if (!recipeId) return;
    const controller = new AbortController();
    Promise.all([
      request<RecipeEntry>(`/api/recipes/${encodeURIComponent(recipeId)}`, { signal: controller.signal }),
      request<Proposal[]>(`/api/recipes/${encodeURIComponent(recipeId)}/repair-proposals`, { signal: controller.signal }),
    ])
      .then(([loaded, saved]) => {
        setRecipe(loaded);
        setProposals(saved);
        setProposalId(saved[0]?.id || '');
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Recipe or proposals unavailable.')));
      });
    return () => controller.abort();
  }, [recipeId, refresh]);
  useEffect(() => {
    setReviewed(false);
  }, [proposalId]);

  const selectedProposal = proposals.find(proposal => proposal.id === proposalId);
  const oldTable = beforeGraph?.tables.find(table => table.id === tableId);
  const newTable = afterGraph?.tables.find(table => table.id === tableId);
  const repairTables = useMemo(() => {
    const map = new Map((beforeGraph?.tables || []).map(table => [table.id, table]));
    for (const table of afterGraph?.tables || []) if (!map.has(table.id)) map.set(table.id, table);
    return [...map.values()];
  }, [beforeGraph, afterGraph]);
  const filteredChanges = (diff?.changes || []).filter(
    change =>
      (!diffKind || change.kind === diffKind) &&
      `${change.schema}.${change.table}.${change.column || ''} ${change.kind}`.toLocaleLowerCase().includes(diffQuery.toLocaleLowerCase()),
  );
  const changeKinds = [...new Set(diff?.changes.map(change => change.kind) || [])];

  async function propose() {
    if (
      !recipe ||
      !databaseId ||
      !tableId ||
      (repairKind === 'column' ? !oldColumn.trim() || !newColumn : !previousSchema.trim() || !previousTable.trim()) ||
      !reason.trim()
    )
      return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const proposal = await request<Proposal>(`/api/recipes/${encodeURIComponent(recipeId)}/repair-proposals`, {
        method: 'POST',
        body: JSON.stringify({
          databaseId,
          tableId,
          kind: repairKind,
          oldSchema: previousSchema.trim(),
          oldTable: previousTable.trim(),
          oldColumn: oldColumn.trim(),
          newColumn,
          reason: reason.trim(),
          expectedVersion: recipe.definition.version,
        }),
      });
      setProposals(previous => [proposal, ...previous]);
      setProposalId(proposal.id);
      setReviewed(false);
      setNotice(t('Proposal created. Review the SQL diff, then approve it in a separate action.'));
    } catch (cause) {
      setError(errorMessage(cause, t('The server could not create a safe proposal.')));
    } finally {
      setBusy(false);
    }
  }
  async function approve() {
    if (!selectedProposal || !reviewed) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      const updated = await request<RecipeEntry>(`/api/repair-proposals/${encodeURIComponent(selectedProposal.id)}/approve`, {
        method: 'POST',
        body: JSON.stringify({ expectedVersion: selectedProposal.baseVersion }),
      });
      setRecipe(updated);
      setProposals(previous =>
        previous.map(proposal =>
          proposal.id === selectedProposal.id ? { ...proposal, status: 'approved', appliedVersion: updated.definition.version } : proposal,
        ),
      );
      setImpact(previous =>
        previous.map(item =>
          item.recipeId === updated.definition.id
            ? {
                ...item,
                version: updated.definition.version,
                status: updated.validation.status,
                diagnostics: updated.validation.diagnostics,
              }
            : item,
        ),
      );
      setReviewed(false);
      setNotice(t('Repair applied as version {0}. Previous versions are available for rollback.', updated.definition.version));
    } catch (cause) {
      setError(errorMessage(cause, t('Proposal was not applied. Refresh the context and create a new preview.')));
    } finally {
      setBusy(false);
    }
  }
  async function reject() {
    if (!selectedProposal) return;
    setBusy(true);
    setError('');
    try {
      const updated = await request<Proposal>(`/api/repair-proposals/${encodeURIComponent(selectedProposal.id)}/reject`, {
        method: 'POST',
        body: '{}',
      });
      setProposals(previous => previous.map(proposal => (proposal.id === updated.id ? updated : proposal)));
      setNotice(t('Proposal rejected.'));
    } catch (cause) {
      setError(errorMessage(cause, t('Decision was not saved.')));
    } finally {
      setBusy(false);
    }
  }
  const previewUsable = Boolean(selectedProposal && ['Valid', 'Warning'].includes(selectedProposal.validation.status));

  return (
    <div className="changes-panel">
      <div className="changes-workspace-row">
        <button
          className="secondary-button"
          disabled={!workspaceId || busy || loading}
          onClick={() => {
            setRefresh(previous => previous + 1);
            setError('');
          }}
        >
          {loading ? t('Loading…') : t('Refresh changes')}
        </button>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
          <button className="icon-button" aria-label={t('Dismiss error')} onClick={() => setError('')}>
            ×
          </button>
        </div>
      )}
      {notice && (
        <div className="recipe-notice" role="status">
          {tMessage(notice)}
        </div>
      )}
      {!workspaceId ? (
        <div className="panel-placeholder">
          <strong>{t('Select a workspace #2')}</strong>
          <p>{t('Snapshot comparisons and recipe checks are available after the first scan.')}</p>
        </div>
      ) : (
        <>
          <section className="snapshot-comparison source-card">
            <div className="source-header">
              <div className="section-title">
                <h2>{t('Schema changes')}</h2>
              </div>
              <span className="quiet-label">{t('Immutable snapshots')}</span>
            </div>
            <div className="snapshot-selectors">
              <label>
                <span className="field-label">{t('Database')}</span>
                <select
                  aria-label={t('Database for snapshot comparison')}
                  value={databaseId}
                  disabled={busy}
                  onChange={event => setDatabaseId(event.target.value)}
                >
                  <option value="">{t('Select a database')}</option>
                  {catalog?.databases.map(database => (
                    <option key={database.id} value={database.id}>
                      {database.name}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span className="field-label">{t('Before')}</span>
                <select
                  aria-label={t('Snapshot before changes')}
                  value={fromId}
                  disabled={!snapshots.length || busy}
                  onChange={event => setFromId(event.target.value)}
                >
                  <option value="">{t('Select the original snapshot')}</option>
                  {snapshots.map(snapshot => (
                    <option key={snapshot.id} value={snapshot.id}>
                      {dates(snapshot.createdAt)} · {snapshot.mode} · {snapshot.tableCount}
                      {t('tables')}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span className="field-label">{t('After')}</span>
                <select
                  aria-label={t('Snapshot after changes')}
                  value={toId}
                  disabled={!snapshots.length || busy}
                  onChange={event => setToId(event.target.value)}
                >
                  <option value="">{t('Select the new snapshot')}</option>
                  {snapshots.map(snapshot => (
                    <option key={snapshot.id} value={snapshot.id}>
                      {dates(snapshot.createdAt)} · {snapshot.mode} · {snapshot.tableCount}
                      {t('tables')}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            {diffLoading ? (
              <div className="panel-placeholder">
                <span className="spinner" />
                {t('Comparing metadata…')}
              </div>
            ) : !diff ? (
              <p className="comparison-empty">
                {snapshots.length < 2
                  ? t(
                      'Two snapshots of the same database are required. Change the sources and run Quick Refresh or Rebuild in the workspace.',
                    )
                  : fromId === toId
                    ? t('Select different snapshots to compare.')
                    : t('Select the original and new snapshots.')}
              </p>
            ) : (
              <>
                <div className="diff-summary">
                  <div>
                    <strong>{diff.changes.length}</strong>
                    <span>{t('changes')}</span>
                  </div>
                  <div>
                    <strong>{diff.renameCandidates.length}</strong>
                    <span>{t('possible renames')}</span>
                  </div>
                  <p>{t('Structural similarity is a reason to investigate. It does not prove that an object was renamed.')}</p>
                </div>
                {diff.diagnostics.map((diagnostic, index) => (
                  <div key={index} className={`validation-diagnostic ${diagnostic.severity}`}>
                    <code>{diagnostic.code}</code>
                    <p>{tMessage(diagnostic.message)}</p>
                  </div>
                ))}
                <div className="diff-filters">
                  <input
                    type="search"
                    aria-label={t('Search schema changes')}
                    value={diffQuery}
                    onChange={event => setDiffQuery(event.target.value)}
                    placeholder={t('Table, column, or change type…')}
                  />
                  <select aria-label={t('Schema change type')} value={diffKind} onChange={event => setDiffKind(event.target.value)}>
                    <option value="">{t('All changes')}</option>
                    {changeKinds.map(kind => (
                      <option value={kind} key={kind}>
                        {t(changeLabels[kind]) || kind}
                      </option>
                    ))}
                  </select>
                </div>
                {filteredChanges.length ? (
                  <div className="workspace-table-scroll changes-table-scroll">
                    <table className="workspace-table">
                      <thead>
                        <tr>
                          <th>{t('Change')}</th>
                          <th>{t('Object')}</th>
                          <th>{t('Before')}</th>
                          <th>{t('After')}</th>
                        </tr>
                      </thead>
                      <tbody>
                        {filteredChanges.slice(0, 300).map((change, index) => (
                          <tr key={`${change.kind}-${change.tableId}-${change.column}-${index}`}>
                            <td>
                              <span
                                className={`change-kind ${change.kind.toLowerCase().includes('removed') ? 'removed' : change.kind.toLowerCase().includes('added') ? 'added' : 'modified'}`}
                              >
                                {t(changeLabels[change.kind]) || change.kind}
                              </span>
                            </td>
                            <td>
                              <code>
                                {change.schema}.{change.table}
                                {change.column ? `.${change.column}` : ''}
                              </code>
                            </td>
                            <td>
                              <code>{change.before || '—'}</code>
                            </td>
                            <td>
                              <code>{change.after || '—'}</code>
                            </td>
                          </tr>
                        ))}
                      </tbody>
                    </table>
                  </div>
                ) : (
                  <p className="comparison-empty">
                    {diff.changes.length ? t('No changes match this filter.') : t('The compared structure has not changed.')}
                  </p>
                )}
                {filteredChanges.length > 300 && (
                  <p className="workspace-limit-note">
                    {t('First 300 of')} {filteredChanges.length}
                    {t('; refine the filters.')}
                  </p>
                )}
                {diff.renameCandidates.length > 0 && (
                  <details className="rename-candidates">
                    <summary>
                      {t('Possible renames ·')} {diff.renameCandidates.length}
                    </summary>
                    <div>
                      {diff.renameCandidates.slice(0, 100).map((candidate, index) => (
                        <article key={index}>
                          <div>
                            <span className="neutral-tag">
                              {t('possible rename ·')} {candidate.kind}
                            </span>
                            <strong>
                              {candidate.oldName} → {candidate.newName}
                            </strong>
                            <p>{tMessage(candidate.reason)}</p>
                          </div>
                          {candidate.kind === 'column' && (
                            <button
                              className="text-button"
                              onClick={() => {
                                setTableId(candidate.newTableId);
                                setOldColumn(candidate.oldName);
                                setNewColumn(candidate.newName);
                                setReason('');
                                setNotice(t('Replacement fields filled from a candidate. Provide your own reason and create a preview.'));
                              }}
                            >
                              {t('Fill replacement fields')}
                            </button>
                          )}
                        </article>
                      ))}
                    </div>
                  </details>
                )}
              </>
            )}
          </section>
          <SchemaComparePanel workspaceId={workspaceId} databases={catalog?.databases || []} />
          <section className="recipe-impact">
            <div className="section-heading">
              <h2>
                {t('Recipe compatibility')} <span className="section-count">{impact.length}</span>
              </h2>
              <span className="quiet-label">{t('Against the current schema')}</span>
            </div>
            {impact.length ? (
              <div className="impact-list">
                {impact.map(item => (
                  <article key={item.recipeId} className={recipeId === item.recipeId ? 'selected' : ''}>
                    <div className="impact-heading">
                      <button onClick={() => setRecipeId(item.recipeId)}>
                        <strong>{item.name}</strong>
                        <span>v{item.version}</span>
                      </button>
                      <span className={`recipe-state ${item.status.toLowerCase()}`}>{t(statusLabels[item.status]) || item.status}</span>
                      <button className="text-button" onClick={() => onOpenRecipe(workspaceId, item.recipeId)}>
                        {t('Open recipe')}
                      </button>
                    </div>
                    {item.diagnostics.map((diagnostic, index) => (
                      <p className={`impact-diagnostic ${diagnostic.severity}`} key={index}>
                        <code>{diagnostic.code}</code>
                        {tMessage(diagnostic.message)}
                      </p>
                    ))}
                    <div className="compatible-snapshots">
                      <span>{t('Latest compatible snapshots')}</span>
                      {Object.entries(item.lastCompatibleSnapshots).length ? (
                        Object.entries(item.lastCompatibleSnapshots).map(([id, snapshot]) => (
                          <code key={id}>
                            {catalog?.databases.find(database => database.id === id)?.name || id.slice(0, 10)} · {snapshot.slice(0, 12)}
                          </code>
                        ))
                      ) : (
                        <em>{t('Not recorded')}</em>
                      )}
                    </div>
                  </article>
                ))}
              </div>
            ) : (
              <div className="panel-placeholder">{t('Create recipes to track the impact of schema changes.')}</div>
            )}
          </section>
          <section className="repair-form source-card">
            <div className="source-header">
              <div className="section-title">
                <h2>{t('Propose a column repair')}</h2>
              </div>
              <span className="quiet-label">{t('Preview → review → approval')}</span>
            </div>
            <div className="repair-selection">
              <label>
                <span className="field-label">{t('Recipe')}</span>
                <select
                  aria-label={t('Recipe to repair')}
                  value={recipeId}
                  disabled={busy}
                  onChange={event => setRecipeId(event.target.value)}
                >
                  <option value="">{t('Select a recipe')}</option>
                  {impact.map(item => (
                    <option key={item.recipeId} value={item.recipeId}>
                      {item.name} · v{item.version}
                    </option>
                  ))}
                </select>
              </label>
              <label>
                <span className="field-label">{t('Table in the selected database')}</span>
                <select
                  aria-label={t('Table to repair')}
                  value={tableId}
                  disabled={busy || !repairTables.length}
                  onChange={event => {
                    setTableId(event.target.value);
                    setOldColumn('');
                    setNewColumn('');
                  }}
                >
                  <option value="">{t('Select a table')}</option>
                  {repairTables.map(table => (
                    <option key={table.id} value={table.id}>
                      {table.schema}.{table.name}
                    </option>
                  ))}
                </select>
              </label>
            </div>
            <label className="repair-kind">
              <span className="field-label">{t('Repair kind')}</span>
              <select value={repairKind} disabled={busy} onChange={e => setRepairKind(e.target.value)}>
                <option value="column">{t('Column rename')}</option>
                <option value="table">{t('Table rename')}</option>
                <option value="schema">{t('Table moved to another schema')}</option>
              </select>
            </label>
            {repairKind !== 'column' && (
              <div>
                <p>
                  {t(
                    'Select the current target table above. Enter its previous SQL name. Explicit source aliases are required; review the SQL diff before approval.',
                  )}
                </p>
                <label>
                  {t('Previous schema')}
                  <input value={previousSchema} disabled={busy} onChange={e => setPreviousSchema(e.target.value)} />
                </label>
                <label>
                  {t('Previous table')}
                  <input value={previousTable} disabled={busy} onChange={e => setPreviousTable(e.target.value)} />
                </label>
              </div>
            )}
            {repairKind === 'column' && (
              <div className="repair-column-row">
                <label>
                  <span className="field-label">{t('Old column in SQL')}</span>
                  <input
                    aria-label={t('Old column')}
                    list="repair-old-columns"
                    value={oldColumn}
                    disabled={busy}
                    onChange={event => setOldColumn(event.target.value)}
                    placeholder={t('Name of the removed/renamed column')}
                  />
                  <datalist id="repair-old-columns">
                    {oldTable?.columns.map(column => (
                      <option value={column.name} key={column.name} />
                    ))}
                  </datalist>
                </label>
                <span>→</span>
                <label>
                  <span className="field-label">{t('New column in the current snapshot')}</span>
                  <select
                    aria-label={t('New column')}
                    value={newColumn}
                    disabled={busy || !newTable}
                    onChange={event => setNewColumn(event.target.value)}
                  >
                    <option value="">{t('Select a replacement')}</option>
                    {newTable?.columns.map(column => (
                      <option key={column.name} value={column.name} disabled={column.name === oldColumn}>
                        {column.name} · {column.dataType}
                      </option>
                    ))}
                  </select>
                </label>
              </div>
            )}
            <label className="repair-reason">
              <span className="field-label">{t('Reason for the replacement')}</span>
              <textarea
                aria-label={t('Reason for the repair')}
                value={reason}
                maxLength={2000}
                rows={3}
                disabled={busy}
                onChange={event => setReason(event.target.value)}
                placeholder={t('Provide a verified source for the mapping between the old and new columns…')}
              />
            </label>
            {recipe && !recipe.definition.databaseIds.includes(databaseId) && (
              <p className="job-error">
                {t('The selected database is outside this recipe’s scope. Change the comparison database or select another recipe.')}
              </p>
            )}
            {!newTable && tableId && (
              <p className="job-error">
                {t('The table is missing from the selected new snapshot. This change requires editing the recipe manually.')}
              </p>
            )}
            <div className="repair-form-footer">
              <p>
                {t(
                  'The server binds references to tables using the SQL AST, validates the replacement, and saves only a proposal. Assess SQL semantics when reviewing the diff.',
                )}
              </p>
              <button
                className="primary-button"
                disabled={
                  busy ||
                  !recipe ||
                  !recipe.definition.databaseIds.includes(databaseId) ||
                  !newTable ||
                  (repairKind === 'column'
                    ? !oldColumn.trim() || !newColumn || newColumn === oldColumn.trim()
                    : !previousSchema.trim() || !previousTable.trim()) ||
                  !reason.trim()
                }
                onClick={() => void propose()}
              >
                {busy ? t('Processing…') : t('Create preview')}
              </button>
            </div>
          </section>
          {recipeId && (
            <section className="repair-proposals">
              <div className="section-heading">
                <h2>
                  {t('Repair proposals')} <span className="section-count">{proposals.length}</span>
                </h2>
              </div>
              {proposals.length ? (
                <>
                  <select
                    aria-label={t('Repair proposal')}
                    value={proposalId}
                    disabled={busy}
                    onChange={event => setProposalId(event.target.value)}
                  >
                    {proposals.map(proposal => (
                      <option key={proposal.id} value={proposal.id}>
                        {dates(proposal.createdAt)} · {t(statusLabels[proposal.status])} · v{proposal.baseVersion} {t('· replacements: ')}
                        {proposal.replacementCount}
                      </option>
                    ))}
                  </select>
                  {selectedProposal && (
                    <div className="proposal-preview">
                      <div className="proposal-preview-heading">
                        <span className="neutral-tag">{t(statusLabels[selectedProposal.status])}</span>
                        <strong>
                          {t('Version')} {selectedProposal.baseVersion} · {selectedProposal.replacementCount} {t('replacements')}
                        </strong>
                        {selectedProposal.appliedVersion && (
                          <span>
                            {t('Applied in v')}
                            {selectedProposal.appliedVersion}
                          </span>
                        )}
                      </div>
                      <p className="proposal-reason">{selectedProposal.reason}</p>
                      <div className="proposal-column-labels">
                        <span>{t('Before · saved definition')}</span>
                        <span>{t('After · proposed SQL')}</span>
                      </div>
                      <Suspense fallback={<div className="monaco-loading">{t('Loading local SQL diff…')}</div>}>
                        <SqlDiffEditor
                          key={selectedProposal.id}
                          before={selectedProposal.beforeSql}
                          after={selectedProposal.afterSql}
                          label={t('Repair proposal SQL diff')}
                        />
                      </Suspense>
                      <div className="proposal-validation">
                        <span className={`recipe-state ${selectedProposal.validation.status.toLowerCase()}`}>
                          {t(statusLabels[selectedProposal.validation.status])}
                        </span>
                        {selectedProposal.validation.diagnostics.map((diagnostic, index) => (
                          <div className={`validation-diagnostic ${diagnostic.severity}`} key={index}>
                            <code>{diagnostic.code}</code>
                            <p>{tMessage(diagnostic.message)}</p>
                          </div>
                        ))}
                      </div>
                      {selectedProposal.status === 'proposed' && (
                        <div className="proposal-approval">
                          <label>
                            <input
                              type="checkbox"
                              aria-label={t('Confirmation of SQL diff review')}
                              checked={reviewed}
                              disabled={busy}
                              onChange={event => setReviewed(event.target.checked)}
                            />
                            <span>{t('I have reviewed all SQL changes and confirm the stated reason for the replacement.')}</span>
                          </label>
                          {!previewUsable && (
                            <p className="job-error">
                              {t(
                                'The proposed SQL still needs correction or manual review. The server will not apply it as a safe replacement.',
                              )}
                            </p>
                          )}
                          <div>
                            <button
                              className="primary-button"
                              disabled={busy || !reviewed || !previewUsable}
                              onClick={() => void approve()}
                            >
                              {busy ? t('Applying…') : t('Approve and create version')}
                            </button>
                            <button className="secondary-button" disabled={busy} onClick={() => void reject()}>
                              {t('Reject proposal')}
                            </button>
                          </div>
                        </div>
                      )}
                      <button className="text-button proposal-open-recipe" onClick={() => onOpenRecipe(workspaceId, recipeId)}>
                        {t('Open recipe and history for rollback')}
                      </button>
                    </div>
                  )}
                </>
              ) : (
                <p className="panel-placeholder">{t('No proposals yet. Select the old and new columns and create a preview.')}</p>
              )}
            </section>
          )}
        </>
      )}
    </div>
  );
}

type DatabaseComparison = {
  fromDatabaseId: string;
  toDatabaseId?: string;
  toPath?: string;
  changes: Change[];
  renameCandidates: RenameCandidate[];
  diagnostics: RecipeDiagnostic[];
  migrationDraft?: string;
};

/** Read-only comparison of two databases of the workspace, or of one database with a DACPAC file from another environment. */
function SchemaComparePanel({ workspaceId, databases }: { workspaceId: string; databases: Catalog['databases'] }) {
  useLocale();
  const ready = databases.filter(database => database.currentSnapshotId);
  const [baseId, setBaseId] = useState('');
  const [target, setTarget] = useState('');
  const [path, setPath] = useState('');
  const [result, setResult] = useState<DatabaseComparison | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [kindFilter, setKindFilter] = useState('');
  const [showDraft, setShowDraft] = useState(false);
  useEffect(() => {
    setBaseId(ready[0]?.id || '');
    setTarget(ready[1]?.id || 'dacpac');
    setResult(null);
    setError('');
  }, [workspaceId, ready.length]);
  const base = ready.find(database => database.id === baseId);
  const other = ready.find(database => database.id === target);
  const canCompare = Boolean(base && (target === 'dacpac' ? path.trim() : other && other.id !== base.id)) && !busy;
  async function compare() {
    if (!base || !canCompare) return;
    setBusy(true);
    setError('');
    setResult(null);
    setKindFilter('');
    setShowDraft(false);
    try {
      setResult(
        target === 'dacpac'
          ? await request<DatabaseComparison>(`/api/workspaces/${encodeURIComponent(workspaceId)}/compare-dacpac`, {
              method: 'POST',
              body: JSON.stringify({ from: base.currentSnapshotId, path: path.trim() }),
            })
          : await request<DatabaseComparison>(
              `/api/workspaces/${encodeURIComponent(workspaceId)}/compare?${new URLSearchParams({ from: base.currentSnapshotId!, to: other!.currentSnapshotId! })}`,
            ),
      );
    } catch (cause) {
      setError(errorMessage(cause, t('Comparison failed.')));
    } finally {
      setBusy(false);
    }
  }
  const comparedName = target === 'dacpac' ? 'DACPAC' : other?.name || '';
  const kinds = [...new Set(result?.changes.map(change => change.kind) || [])].sort();
  const shown = result?.changes.filter(change => !kindFilter || change.kind === kindFilter) || [];
  const headings = { kind: t('Change'), object: t('Object'), base: base?.name || '', compared: comparedName };
  function download(contents: string, extension: string, type: string) {
    const url = URL.createObjectURL(new Blob([contents], { type }));
    const link = document.createElement('a');
    link.href = url;
    link.download = `schemora-compare-${base?.name || 'base'}-${comparedName || 'other'}.${extension}`.replace(/[^\w.-]+/g, '_');
    link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
  }
  function exportCsv() {
    download(
      comparisonCsv(shown, headings, kind => t(changeLabels[kind]) || kind),
      'csv',
      'text/csv;charset=utf-8',
    );
  }
  function exportHtml() {
    const html = comparisonHtml(
      shown,
      {
        lang: getLocale(),
        title: t('Schema comparison: {0} and {1}', base?.name || '', comparedName),
        subtitle: t('Matched by schema and table name · read-only') + ' · ' + new Date().toLocaleString(localeTag()),
        summary: kindFilter
          ? t('Differences shown: {0} of {1}', shown.length, result?.changes.length || 0)
          : t('Differences found: {0}', shown.length),
        headings,
      },
      kind => t(changeLabels[kind]) || kind,
    );
    download(html, 'html', 'text/html;charset=utf-8');
  }
  if (!ready.length) return null;
  return (
    <section className="snapshot-comparison source-card database-compare" aria-label={t('Compare with another database')}>
      <div className="section-heading">
        <h2>{t('Compare with another database')}</h2>
        <span className="quiet-label">{t('Matched by schema and table name · read-only')}</span>
      </div>
      <div className="snapshot-selectors">
        <label>
          <span className="field-label">{t('Base database')}</span>
          <select aria-label={t('Base database')} value={baseId} onChange={event => setBaseId(event.target.value)}>
            {ready.map(database => (
              <option key={database.id} value={database.id}>
                {database.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          <span className="field-label">{t('Compared with')}</span>
          <select aria-label={t('Compared with')} value={target} onChange={event => setTarget(event.target.value)}>
            {ready
              .filter(database => database.id !== baseId)
              .map(database => (
                <option key={database.id} value={database.id}>
                  {database.name}
                </option>
              ))}
            <option value="dacpac">{t('A DACPAC file…')}</option>
          </select>
        </label>
        {target === 'dacpac' && (
          <label>
            <span className="field-label">{t('Path to .dacpac file')}</span>
            <input
              aria-label={t('Path to .dacpac file')}
              value={path}
              onChange={event => setPath(event.target.value)}
              placeholder="E:\Releases\Stage\Database.dacpac"
            />
          </label>
        )}
      </div>
      <button className="secondary-button" disabled={!canCompare} onClick={() => void compare()}>
        {busy ? t('Comparing…') : t('Compare')}
      </button>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      {result && (
        <>
          <div className="database-compare-toolbar">
            <p className="muted-text" role="status">
              {kindFilter
                ? t('Differences shown: {0} of {1}', shown.length, result.changes.length)
                : t('Differences found: {0}', result.changes.length)}
            </p>
            {result.changes.length > 0 && (
              <>
                <select aria-label={t('Filter by change')} value={kindFilter} onChange={event => setKindFilter(event.target.value)}>
                  <option value="">{t('All changes')}</option>
                  {kinds.map(kind => (
                    <option key={kind} value={kind}>
                      {t(changeLabels[kind]) || kind}
                    </option>
                  ))}
                </select>
                <button className="secondary-button" onClick={exportCsv}>
                  {t('Export CSV')}
                </button>
                <button className="secondary-button" onClick={exportHtml}>
                  {t('Export HTML report')}
                </button>
                {result.migrationDraft && (
                  <button className="secondary-button" aria-expanded={showDraft} onClick={() => setShowDraft(value => !value)}>
                    {t('Migration draft')}
                  </button>
                )}
              </>
            )}
          </div>
          {showDraft && result.migrationDraft && (
            <section className="migration-draft" aria-label={t('Migration draft')}>
              <div className="alert warning">
                <p>
                  {t(
                    'A starting point for a change script that makes {0} look like {1}. Schemora never runs it: check every statement, add defaults and data moves, and uncomment destructive steps only on purpose.',
                    base?.name || '',
                    comparedName,
                  )}
                </p>
              </div>
              <div className="collection-sql-actions">
                <button
                  className="secondary-button"
                  onClick={() => void navigator.clipboard.writeText(result.migrationDraft!).catch(() => undefined)}
                >
                  {t('Copy')}
                </button>
                <button className="secondary-button" onClick={() => download(result.migrationDraft!, 'sql', 'text/plain;charset=utf-8')}>
                  {t('Download file')}
                </button>
              </div>
              <Suspense fallback={<textarea readOnly value={result.migrationDraft} rows={16} aria-label={t('Migration draft')} />}>
                <SqlEditor label={t('Migration draft')} value={result.migrationDraft} readOnly height={420} />
              </Suspense>
            </section>
          )}
          {shown.length > 0 && (
            <div className="workspace-table-scroll changes-table-scroll">
              <table className="workspace-table">
                <thead>
                  <tr>
                    <th>{t('Change')}</th>
                    <th>{t('Object')}</th>
                    <th>{base?.name}</th>
                    <th>{comparedName}</th>
                  </tr>
                </thead>
                <tbody>
                  {shown.slice(0, 300).map((change, index) => (
                    <tr key={`${change.kind}-${change.tableId}-${change.column}-${index}`}>
                      <td>
                        <span
                          className={`change-kind ${change.kind.toLowerCase().includes('removed') ? 'removed' : change.kind.toLowerCase().includes('added') ? 'added' : 'modified'}`}
                        >
                          {t(changeLabels[change.kind]) || change.kind}
                        </span>
                      </td>
                      <td>
                        <code>
                          {change.schema}.{change.table}
                          {change.column ? `.${change.column}` : ''}
                        </code>
                      </td>
                      <td>
                        <code>{change.before || '—'}</code>
                      </td>
                      <td>
                        <code>{change.after || '—'}</code>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </section>
  );
}
