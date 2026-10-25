import DockerDiagnosticsPanel from './DockerDiagnosticsPanel';
import ManagedFilesPanel from './ManagedFilesPanel';
import BuildReceipts from './BuildReceipts';
import type { BuildReceipt } from './BuildReceipts';
import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage, localeTag } from './i18n';
import { useLocale } from './i18nReact';
import SourceLocation from './SourceLocation';
import CollectionSqlPanel from './CollectionSqlPanel';
import BackupSchedulePanel from './BackupSchedulePanel';
import type { RecipeEntry } from './recipeTypes';
const validationLabels: Record<string, string> = {
  Valid: 'Compatible',
  Warning: 'Has warnings',
  Broken: 'Incompatible',
  NeedsReview: 'Review required',
};
type Receipt = {
  id: string;
  createdAt: string;
  mode: string;
  applicationVersion: string;
  runtimeVersion: string;
  dacFxVersion: string;
  analysisVersion: number;
  sources: { path: string; hash: string }[];
  projects: { projectId: string; configuration: string; framework?: string }[];
  snapshots: Record<string, string>;
  builds?: BuildReceipt[];
};
type Provenance = {
  latest?: Receipt;
  previous?: Receipt;
  stale: boolean;
  settingsChanged: boolean;
  currentCheckComplete: boolean;
  changes: { path: string; change: string; before?: string; after?: string }[];
  recordedChangedPaths: string[];
};
type Hit = {
  id: string;
  kind: string;
  name: string;
  databaseId?: string;
  sourcePath?: string;
  line?: number;
  detail: string;
  definitionLine?: number;
};
type Page = { items: Hit[]; total: number; offset: number; limit: number; query?: string };
type Collection = { id: string; name: string; recipeIds: string[]; version: number };
type Batch = {
  current: {
    id: string;
    createdAt: string;
    snapshots: Record<string, string>;
    checks: { recipeId: string; name: string; version: number; status: string; diagnosticCodes: string[] }[];
  };
  previous?: { createdAt: string; snapshots: Record<string, string> };
  changes: { recipeId: string; before?: string; after: string; beforeCodes?: string[]; afterCodes: string[] }[];
};
type Recovery = { id: string; catalogVersion: number; workspaces: number; recipes: number; snapshots: number; sha256: string };
export default function WorkspaceTools({
  workspaceId,
  onRecipe,
  onObject,
}: {
  workspaceId: string;
  onRecipe: (workspace: string, recipe: string) => void;
  onObject: (workspace: string, database: string, object: string, matchText?: string) => void;
}) {
  useLocale();
  const [provenance, setProvenance] = useState<Provenance | null>(null),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false),
    [query, setQuery] = useState(''),
    [kind, setKind] = useState(''),
    [searchDatabase, setSearchDatabase] = useState(''),
    [databases, setDatabases] = useState<{ id: string; name: string }[]>([]),
    [page, setPage] = useState<Page | null>(null),
    [recipes, setRecipes] = useState<RecipeEntry[]>([]),
    [collections, setCollections] = useState<Collection[]>([]),
    [collection, setCollection] = useState<Collection>({ id: '', name: '', recipeIds: [], version: 0 }),
    [batch, setBatch] = useState<Batch | null>(null),
    [integrity, setIntegrity] = useState<{ healthy: boolean; checks: string[]; catalogVersion: number } | null>(null),
    [path, setPath] = useState(''),
    [recovery, setRecovery] = useState<Recovery | null>(null),
    [confirmed, setConfirmed] = useState(false),
    [restarting, setRestarting] = useState(false),
    [recipeQuery, setRecipeQuery] = useState(''),
    [recipeOffset, setRecipeOffset] = useState(0),
    [backups, setBackups] = useState<{ path: string; name: string }[]>([]);
  useEffect(() => {
    setProvenance(null);
    setPage(null);
    setBatch(null);
    setCollection({ id: '', name: '', recipeIds: [], version: 0 });
    setCollections([]);
    setRecipes([]);
    setError('');
    const c = new AbortController();
    void Promise.all([
      request<RecipeEntry[]>('/api/workspaces/' + workspaceId + '/recipes', { signal: c.signal }),
      request<{ collections: Collection[] }>('/api/workspaces/' + workspaceId + '/recipe-collections', { signal: c.signal }),
      request<{ databases: { id: string; name: string }[] }>('/api/workspaces/' + workspaceId + '/catalog', { signal: c.signal }),
    ])
      .then(([r, c, catalog]) => {
        setRecipes(r);
        setCollections(c.collections);
        setDatabases(catalog.databases);
        setSearchDatabase('');
      })
      .catch(e => {
        if (!c.signal.aborted) setError(errorMessage(e, t('Unable to load workspace tools.')));
      });
    return () => c.abort();
  }, [workspaceId]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e, t('Workspace operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  async function search(offset = 0) {
    const p = new URLSearchParams({ query, kind, databaseId: searchDatabase, offset: String(offset), limit: '50' });
    const result = await request<Page>('/api/workspaces/' + workspaceId + '/search?' + p);
    setPage({ ...result, query: query.trim() });
  }
  const candidates = recipes.filter(r => r.definition.name.toLowerCase().includes(recipeQuery.toLowerCase()));
  return (
    <>
      {error && <p role="alert">{tMessage(error)}</p>}
      <details className="professional-section">
        <summary>{t('Analysis provenance')}</summary>
        <p>{t('Check source hashes without executing builds. Earlier runs without a receipt are explicitly unknown.')}</p>
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() => void run(async () => setProvenance(await request<Provenance>('/api/workspaces/' + workspaceId + '/provenance')))}
        >
          {t('Check analysis freshness')}
        </button>
        {provenance && (
          <>
            <p role="status">
              {t(provenance.stale ? 'Analysis needs refresh' : 'Recorded inputs are current')} ·{' '}
              {t(provenance.currentCheckComplete ? 'File discovery complete' : 'File discovery incomplete')}
            </p>
            {provenance.latest && (
              <>
                <p>
                  {new Date(provenance.latest.createdAt).toLocaleString(localeTag())} · {provenance.latest.mode}
                </p>
                <dl>
                  <dt>Schemora</dt>
                  <dd>{provenance.latest.applicationVersion}</dd>
                  <dt>.NET</dt>
                  <dd>{provenance.latest.runtimeVersion}</dd>
                  <dt>DacFx</dt>
                  <dd>{provenance.latest.dacFxVersion}</dd>
                </dl>
                <BuildReceipts builds={provenance.latest.builds} />
                <details>
                  <summary>
                    {t('Recorded input files')}: {provenance.latest.sources.length}
                  </summary>
                  <ul>
                    {provenance.latest.sources.map(s => (
                      <li key={s.path}>
                        <SourceLocation path={s.path} />
                        <code>{s.hash}</code>
                      </li>
                    ))}
                  </ul>
                </details>
                <details>
                  <summary>{t('Recorded C# selections')}</summary>
                  {provenance.latest.projects.map(p => (
                    <p key={p.projectId}>
                      {p.projectId} · {p.configuration} · {p.framework || t('Default framework')}
                    </p>
                  ))}
                </details>
              </>
            )}
            {provenance.settingsChanged && <p>{t('Analysis settings changed since the recorded run.')}</p>}
            <h4>{t('Changes since recorded analysis')}</h4>
            {provenance.changes.map(c => (
              <article key={c.path}>
                <p>{t('Change: ' + c.change)}</p>
                <SourceLocation path={c.path} />
                <code>
                  {c.before} → {c.after}
                </code>
              </article>
            ))}
            <details>
              <summary>
                {t('Changed paths between recorded runs')}: {provenance.recordedChangedPaths.length}
              </summary>
              <ul>
                {provenance.recordedChangedPaths.map(p => (
                  <li key={p}>{p}</li>
                ))}
              </ul>
            </details>
          </>
        )}
      </details>
      <details className="professional-section">
        <summary>{t('Unified workspace search')}</summary>
        <form
          onSubmit={e => {
            e.preventDefault();
            void run(() => search());
          }}
        >
          <label>
            {t('Search all objects and recipes')}
            <input type="search" value={query} maxLength={256} onChange={e => setQuery(e.target.value)} />
          </label>
          <label>
            {t('Search kind')}
            <select value={kind} onChange={e => setKind(e.target.value)}>
              <option value="">{t('All kinds')}</option>
              {['table', 'column', 'view', 'procedure', 'scalarFunction', 'tableFunction', 'recipe', 'evidence', 'sqlCode'].map(k => (
                <option key={k} value={k}>
                  {t('Kind: ' + k)}
                </option>
              ))}
            </select>
          </label>
          {databases.length > 1 && (
            <label>
              {t('Database')}
              <select value={searchDatabase} onChange={e => setSearchDatabase(e.target.value)}>
                <option value="">{t('All databases')}</option>
                {databases.map(database => (
                  <option key={database.id} value={database.id}>
                    {database.name}
                  </option>
                ))}
              </select>
            </label>
          )}
          <button className="secondary-button" disabled={busy}>
            {t('Search workspace')}
          </button>
        </form>
        {page && (
          <>
            <p role="status">{t('Search results: {0}', page.total)}</p>
            {page.items.map((hit, i) => (
              <article key={hit.kind + hit.id + i}>
                <button
                  className="text-button"
                  onClick={() =>
                    hit.kind === 'recipe'
                      ? onRecipe(workspaceId, hit.id)
                      : hit.databaseId
                        ? onObject(workspaceId, hit.databaseId, hit.id, hit.kind === 'sqlCode' ? page.query : undefined)
                        : undefined
                  }
                >
                  {hit.name}
                </button>
                <p>
                  {t('Kind: ' + hit.kind)}
                  {hit.definitionLine && (
                    <>
                      {' '}
                      · {t('Definition line')}: {hit.definitionLine}
                    </>
                  )}
                </p>
                <pre className="search-excerpt">{hit.detail}</pre>
                <p></p>
                {hit.sourcePath && <SourceLocation path={hit.sourcePath} line={hit.line} />}
              </article>
            ))}
            <button
              className="secondary-button"
              disabled={busy || !page.offset}
              onClick={() => void run(() => search(Math.max(0, page.offset - 50)))}
            >
              {t('Previous page')}
            </button>
            <button
              className="secondary-button"
              disabled={busy || page.offset + 50 >= page.total}
              onClick={() => void run(() => search(page.offset + 50))}
            >
              {t('Next page')}
            </button>
          </>
        )}
      </details>
      <details className="professional-section">
        <summary>{t('Recipe collections and batch validation')}</summary>
        <label>
          {t('Saved collection')}
          <select
            value={collection.id}
            onChange={e => setCollection(collections.find(c => c.id === e.target.value) || { id: '', name: '', recipeIds: [], version: 0 })}
          >
            <option value="">{t('New collection')}</option>
            {collections.map(c => (
              <option key={c.id} value={c.id}>
                {c.name}
              </option>
            ))}
          </select>
        </label>
        <label>
          {t('Collection name')}
          <input value={collection.name} maxLength={100} onChange={e => setCollection(c => ({ ...c, name: e.target.value }))} />
        </label>
        <label>
          {t('Filter recipes')}
          <input
            type="search"
            value={recipeQuery}
            onChange={e => {
              setRecipeQuery(e.target.value);
              setRecipeOffset(0);
            }}
          />
        </label>
        {candidates.slice(recipeOffset, recipeOffset + 50).map(r => (
          <label key={r.definition.id}>
            <input
              type="checkbox"
              checked={collection.recipeIds.includes(r.definition.id)}
              onChange={e =>
                setCollection(c => ({
                  ...c,
                  recipeIds: e.target.checked ? [...c.recipeIds, r.definition.id] : c.recipeIds.filter(id => id !== r.definition.id),
                }))
              }
            />
            {r.definition.name}
          </label>
        ))}
        <button className="text-button" disabled={!recipeOffset} onClick={() => setRecipeOffset(Math.max(0, recipeOffset - 50))}>
          {t('Previous page')}
        </button>
        <button
          className="text-button"
          disabled={recipeOffset + 50 >= candidates.length}
          onClick={() => setRecipeOffset(recipeOffset + 50)}
        >
          {t('Next page')}
        </button>
        <p>{t('Selected recipes: {0}', collection.recipeIds.length)}</p>
        <button
          className="secondary-button"
          disabled={busy || !collection.name.trim()}
          onClick={() =>
            void run(async () => {
              const saved = await request<Collection>('/api/workspaces/' + workspaceId + '/recipe-collections', {
                method: 'PUT',
                body: JSON.stringify(collection),
              });
              setCollection(saved);
              setCollections(c => [...c.filter(v => v.id !== saved.id), saved]);
            })
          }
        >
          {t('Save collection')}
        </button>
        <button
          className="secondary-button"
          disabled={busy || !collection.id}
          onClick={() =>
            void run(async () => {
              await request(
                '/api/workspaces/' + workspaceId + '/recipe-collections/' + collection.id + '?expectedVersion=' + collection.version,
                { method: 'DELETE' },
              );
              setCollections(c => c.filter(v => v.id !== collection.id));
              setCollection({ id: '', name: '', recipeIds: [], version: 0 });
            })
          }
        >
          {t('Delete collection only')}
        </button>
        <button
          className="secondary-button"
          disabled={busy || !collection.recipeIds.length || collection.recipeIds.length > 200}
          onClick={() =>
            void run(async () =>
              setBatch(
                await request<Batch>('/api/workspaces/' + workspaceId + '/recipe-batch', {
                  method: 'POST',
                  body: JSON.stringify({ recipeIds: collection.recipeIds }),
                }),
              ),
            )
          }
        >
          {t('Validate selected recipes')}
        </button>
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() =>
            void run(async () => {
              const result = await request<Batch>('/api/workspaces/' + workspaceId + '/recipe-batch');
              setBatch(result.current ? result : null);
            })
          }
        >
          {t('Load last batch results')}
        </button>
        {batch && (
          <>
            <p>
              {t('Batch validation finished')}: {batch.current.checks.length}
            </p>
            <p>
              {t('Current snapshots')}: {Object.values(batch.current.snapshots).join(', ')}
            </p>
            {batch.previous && (
              <p>
                {t('Previous snapshots')}: {Object.values(batch.previous.snapshots).join(', ')}
              </p>
            )}
            {batch.changes.map(c => (
              <p key={c.recipeId}>
                {batch.current.checks.find(r => r.recipeId === c.recipeId)?.name}:{' '}
                {t(validationLabels[c.before || ''] || c.before || 'Unavailable')} → {t(validationLabels[c.after] || c.after)} ·{' '}
                {c.beforeCodes?.join(', ')} → {c.afterCodes.join(', ')}
              </p>
            ))}
          </>
        )}
        {collection.id && (
          // Rendering uses the saved membership, so unsaved checkbox edits do not change the script.
          <CollectionSqlPanel
            key={collection.id}
            workspaceId={workspaceId}
            collectionId={collection.id}
            collectionName={collection.name}
            recipes={recipes.filter(r => collections.find(c => c.id === collection.id)?.recipeIds.includes(r.definition.id))}
          />
        )}
      </details>
      <DockerDiagnosticsPanel />
      <ManagedFilesPanel />
      <details className="professional-section">
        <summary>{t('Catalog health and recovery')}</summary>
        <BackupSchedulePanel />
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() => void run(async () => setIntegrity(await request('/api/storage/integrity')))}
        >
          {t('Check catalog integrity')}
        </button>
        {integrity && (
          <p role="status">
            {t(integrity.healthy ? 'Catalog integrity verified' : 'Catalog integrity failed')} · v{integrity.catalogVersion} ·{' '}
            {integrity.checks.join(', ')}
          </p>
        )}
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() => void run(async () => setBackups(await request('/api/storage/recovery/backups')))}
        >
          {t('List local backups')}
        </button>
        {backups.length > 0 && (
          <label>
            {t('Local backups')}
            <select
              value={path}
              onChange={e => {
                setPath(e.target.value);
                setRecovery(null);
                setConfirmed(false);
              }}
            >
              <option value="">{t('Choose backup')}</option>
              {backups.map(b => (
                <option value={b.path} key={b.path}>
                  {b.name}
                </option>
              ))}
            </select>
          </label>
        )}
        <label>
          {t('Backup SQLite path')}
          <input
            value={path}
            onChange={e => {
              setPath(e.target.value);
              setRecovery(null);
              setConfirmed(false);
            }}
          />
        </label>
        <button
          className="secondary-button"
          disabled={busy || !path.trim()}
          onClick={() =>
            void run(async () => {
              setRecovery(await request<Recovery>('/api/storage/recovery/preview', { method: 'POST', body: JSON.stringify({ path }) }));
              setConfirmed(false);
            })
          }
        >
          {t('Preview restore')}
        </button>
        {recovery && (
          <>
            <p>
              {t('Backup contents')}: {recovery.workspaces} {t('Workspaces')}, {recovery.recipes} {t('Recipes')}, {recovery.snapshots}{' '}
              {t('Snapshots')}
            </p>
            <p>
              {t('Catalog migration')}: v{recovery.catalogVersion} → v2 ·{' '}
              {t(recovery.catalogVersion === 2 ? 'No version migration required' : 'A protected version migration will run at startup')}
            </p>
            <code>SHA256: {recovery.sha256}</code>
            <p>
              {t(
                'A separate staged copy is verified. The current catalog is preserved before restore; that copy can be selected to roll back.',
              )}
            </p>
            <label>
              <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
              {t('I reviewed the backup and approve replacing the catalog and restarting Schemora.')}
            </label>
            <button
              className="secondary-button"
              disabled={busy || !confirmed || restarting}
              onClick={() =>
                void run(async () => {
                  await request('/api/storage/recovery/apply', {
                    method: 'POST',
                    body: JSON.stringify({ previewId: recovery.id, confirmed }),
                  });
                  setRestarting(true);
                })
              }
            >
              {t('Restore and restart')}
            </button>
            {restarting && <p role="status">{t('Schemora is restarting. Continue in the newly opened browser tab.')}</p>}
          </>
        )}
      </details>
    </>
  );
}
