import { useEffect, useRef, useState } from 'react';
import type { FormEvent } from 'react';
import { request, sessionToken } from './api';
import { t } from './i18n';
import { RequestScope } from './requestScope';
import type { Catalog, Database, Graph } from './types';

export type SchemaSelection = {
  nonce: number;
  workspaceId: string;
  databaseId?: string;
  objectId?: string;
  view?: string;
  /** A historical snapshot named by the address; the catalog stays pinned to it instead of following the current one. */
  snapshotId?: string;
};
type Model = { graph: Graph | null; workspace: string; database: string; snapshot: string; followCurrent: boolean; origin: string };

/** Every graph request has one owner and reads an immutable snapshot, so polling cannot relabel an older model as current. */
export function useSchemaBrowser(active: boolean, workspaceId: string, showPage: () => boolean, selection?: SchemaSelection) {
  const [sourceMode, setSourceMode] = useState<'import' | 'build' | 'docker'>('import');
  const [allowDocker, setAllowDocker] = useState(false);
  const [sourcePath, setSourcePath] = useState('');
  const [trustProject, setTrustProject] = useState(false);
  const [graph, setGraph] = useState<Graph | null>(null);
  const [databaseId, setDatabaseId] = useState('');
  const [schemaWorkspaceId, setSchemaWorkspaceId] = useState('');
  const [snapshotId, setSnapshotId] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [query, setQuery] = useState('');
  const [searchObjectId, setSearchObjectId] = useState('');
  const [searchCodeText, setSearchCodeText] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);
  const [view, setView] = useState<'tables' | 'graph' | 'objects'>('tables');
  const [workspaceDatabases, setWorkspaceDatabases] = useState<Database[]>([]);
  const [openingDatabaseId, setOpeningDatabaseId] = useState('');
  const [loadingCatalog, setLoadingCatalog] = useState(false);
  const [appliedSelection, setAppliedSelection] = useState(0);
  const currentWorkspace = useRef(workspaceId);
  currentWorkspace.current = workspaceId;
  const scope = useRef(new RequestScope());
  const intent = useRef(0);
  const manualLoading = useRef(false);
  const previousWorkspace = useRef(workspaceId);
  const model = useRef<Model>({ graph: null, workspace: '', database: '', snapshot: '', followCurrent: true, origin: workspaceId });
  const selected = useRef(selectedId);
  selected.current = selectedId;
  const wanted = useRef(selection);
  wanted.current = selection;
  const applied = useRef(0);

  function clear() {
    model.current = { graph: null, workspace: '', database: '', snapshot: '', followCurrent: true, origin: currentWorkspace.current };
    setGraph(null);
    setDatabaseId('');
    setSchemaWorkspaceId('');
    setSnapshotId('');
    setSelectedId(null);
    setSearchObjectId('');
    setSearchCodeText('');
    setQuery('');
  }

  function apply(loaded: Graph, database: Database | null, snapshot: string, objectId?: string, nextView?: string, followCurrent = true) {
    const workspace = database?.workspaceId || '';
    const exists = (id: string | null | undefined) =>
      loaded.tables.some(table => table.id === id) || loaded.objects?.some(object => object.id === id);
    const chosen = exists(objectId)
      ? objectId!
      : exists(selected.current)
        ? selected.current!
        : loaded.tables[0]?.id || loaded.objects?.[0]?.id || null;
    model.current = { graph: loaded, workspace, database: database?.id || '', snapshot, followCurrent, origin: currentWorkspace.current };
    setGraph(loaded);
    setDatabaseId(database?.id || '');
    setSchemaWorkspaceId(workspace);
    setSnapshotId(snapshot);
    setSelectedId(chosen);
    setSearchObjectId(chosen || '');
    if (nextView === 'tables' || nextView === 'graph' || nextView === 'objects') setView(nextView);
    else if (objectId) setView(loaded.tables.some(table => table.id === chosen) ? 'tables' : 'objects');
  }

  async function loadDatabase(
    database: Database,
    options: {
      objectId?: string;
      view?: string;
      selectionNonce?: number;
      followCurrent?: boolean;
      snapshot?: string;
      signal?: AbortSignal;
    } = {},
  ) {
    const target = currentWorkspace.current;
    if (database.workspaceId !== target) return;
    const snapshot = options.snapshot || database.currentSnapshotId;
    if (!snapshot) return;
    const operation = scope.current.begin();
    setOpeningDatabaseId(database.id);
    setError('');
    try {
      const loaded = await request<Graph>('/api/schema/snapshots/' + encodeURIComponent(snapshot), { signal: operation.signal });
      if (!operation.current() || options.signal?.aborted || currentWorkspace.current !== target) return;
      apply(loaded, database, snapshot, options.objectId, options.view, options.followCurrent);
      if (options.selectionNonce) {
        applied.current = options.selectionNonce;
        setAppliedSelection(options.selectionNonce);
      }
    } catch (cause) {
      if (operation.current() && currentWorkspace.current === target)
        setError(cause instanceof Error ? cause.message : t('Не удалось открыть схему.'));
    } finally {
      if (operation.current()) setOpeningDatabaseId('');
    }
  }

  async function openObject(target: string, id: string, objectId: string, matchText?: string) {
    if (target !== currentWorkspace.current || !showPage()) return;
    intent.current++;
    const operation = scope.current.begin();
    manualLoading.current = true;
    setBusy(false);
    setOpeningDatabaseId(id);
    setError('');
    try {
      const catalog = await request<Catalog>('/api/workspaces/' + encodeURIComponent(target) + '/catalog', { signal: operation.signal });
      const database = catalog.databases.find(item => item.id === id && item.currentSnapshotId);
      if (!operation.current() || target !== currentWorkspace.current) return;
      if (!database) {
        clear();
        return;
      }
      const snapshot = database.currentSnapshotId!;
      const loaded = await request<Graph>('/api/schema/snapshots/' + encodeURIComponent(snapshot), { signal: operation.signal });
      if (!operation.current() || target !== currentWorkspace.current) return;
      apply(loaded, database, snapshot, objectId);
      const targetSelection = wanted.current;
      if (targetSelection?.workspaceId === target) {
        applied.current = targetSelection.nonce;
        setAppliedSelection(targetSelection.nonce);
      }
      setSearchCodeText(matchText || '');
      setQuery('');
    } catch (cause) {
      if (operation.current() && target === currentWorkspace.current)
        setError(cause instanceof Error ? cause.message : t('Не удалось открыть схему.'));
    } finally {
      if (operation.current()) {
        manualLoading.current = false;
        setOpeningDatabaseId('');
      }
    }
  }

  function show(loaded: Graph, database: Database, historicalSnapshot?: string) {
    if (database.workspaceId !== currentWorkspace.current || !showPage()) return;
    intent.current++;
    scope.current.cancel();
    manualLoading.current = false;
    setBusy(false);
    setOpeningDatabaseId('');
    apply(loaded, database, historicalSnapshot || database.currentSnapshotId || '', undefined, undefined, !historicalSnapshot);
    setQuery('');
  }

  function openWorkspaceDatabase(database: Database) {
    intent.current++;
    manualLoading.current = false;
    setBusy(false);
    if (wanted.current?.workspaceId === currentWorkspace.current) {
      applied.current = wanted.current.nonce;
      setAppliedSelection(wanted.current.nonce);
    }
    setQuery('');
    return loadDatabase(database);
  }

  useEffect(() => {
    if (previousWorkspace.current === workspaceId) return;
    previousWorkspace.current = workspaceId;
    intent.current++;
    scope.current.cancel();
    manualLoading.current = false;
    clear();
    setOpeningDatabaseId('');
    setBusy(false);
    setError('');
    setWorkspaceDatabases([]);
  }, [workspaceId]);
  useEffect(() => () => scope.current.cancel(), []);

  useEffect(() => {
    if (!active || !workspaceId || !sessionToken) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    let running = false;
    let first = true;
    async function reconcile() {
      if (running || controller.signal.aborted) return;
      running = true;
      const revision = intent.current;
      if (first) setLoadingCatalog(true);
      try {
        const catalog = await request<Pick<Catalog, 'databases'>>('/api/workspaces/' + encodeURIComponent(workspaceId) + '/schema-state', {
          signal: controller.signal,
        });
        if (controller.signal.aborted || workspaceId !== currentWorkspace.current) return;
        const ready = catalog.databases.filter(database => database.currentSnapshotId);
        setWorkspaceDatabases(ready);
        if (revision !== intent.current || manualLoading.current) return;
        setError('');
        const target = wanted.current;
        const pending = target && target.workspaceId === workspaceId && applied.current !== target.nonce ? target : undefined;
        const current = model.current;
        if (!pending?.databaseId && current.graph && (!current.followCurrent || !current.workspace)) {
          if (pending) {
            applied.current = pending.nonce;
            setAppliedSelection(pending.nonce);
          }
          return;
        }
        const database = ready.find(item => item.id === (pending?.databaseId || current.database)) || ready[0];
        if (!database) {
          scope.current.cancel();
          clear();
          if (pending) {
            applied.current = pending.nonce;
            setAppliedSelection(pending.nonce);
          }
        } else if (pending || current.database !== database.id || current.snapshot !== database.currentSnapshotId) {
          const pinned = pending?.snapshotId && pending.databaseId === database.id ? pending.snapshotId : undefined;
          await loadDatabase(database, {
            objectId: pending?.objectId,
            view: pending?.view,
            selectionNonce: pending?.nonce,
            snapshot: pinned,
            followCurrent: !pinned,
            signal: controller.signal,
          });
        }
      } catch (cause) {
        if (!controller.signal.aborted && workspaceId === currentWorkspace.current)
          setError(cause instanceof Error ? cause.message : t('Не удалось открыть схему.'));
      } finally {
        running = false;
        first = false;
        if (!controller.signal.aborted) {
          setLoadingCatalog(false);
          timer = setTimeout(() => void reconcile(), 5000);
        }
      }
    }
    const visible = () => {
      if (!document.hidden) {
        clearTimeout(timer);
        void reconcile();
      }
    };
    void reconcile();
    document.addEventListener('visibilitychange', visible);
    return () => {
      controller.abort();
      clearTimeout(timer);
      document.removeEventListener('visibilitychange', visible);
    };
  }, [active, workspaceId, selection?.nonce]);

  async function openSchema(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    if (
      !sourcePath.trim() ||
      busy ||
      !sessionToken ||
      (sourceMode === 'build' && !trustProject) ||
      (sourceMode === 'docker' && !allowDocker)
    )
      return;
    intent.current++;
    const target = currentWorkspace.current;
    const operation = scope.current.begin();
    manualLoading.current = true;
    setBusy(true);
    setError('');
    try {
      const loaded = await request<Graph>(`/api/schema/${sourceMode}`, {
        method: 'POST',
        signal: operation.signal,
        body: JSON.stringify({
          path: sourcePath.trim(),
          ...(sourceMode === 'build' ? { trustProject: true } : sourceMode === 'docker' ? { allowIsolatedDocker: true } : {}),
        }),
      });
      if (!operation.current() || target !== currentWorkspace.current) return;
      apply(loaded, null, '', undefined, undefined, false);
      setQuery('');
    } catch (cause) {
      if (operation.current() && target === currentWorkspace.current)
        setError(cause instanceof Error ? cause.message : t('Не удалось открыть схему.'));
    } finally {
      if (operation.current()) {
        manualLoading.current = false;
        setBusy(false);
      }
    }
  }

  return {
    sourceMode,
    setSourceMode,
    allowDocker,
    setAllowDocker,
    sourcePath,
    setSourcePath,
    trustProject,
    setTrustProject,
    graph: model.current.origin === workspaceId ? graph : null,
    setGraph,
    snapshotId,
    pinnedSnapshotId: model.current.followCurrent ? '' : snapshotId,
    databaseId,
    setDatabaseId,
    schemaWorkspaceId,
    setSchemaWorkspaceId,
    busy,
    setBusy,
    error,
    setError,
    query,
    setQuery,
    searchObjectId,
    setSearchObjectId,
    searchCodeText,
    setSearchCodeText,
    selectedId,
    setSelectedId,
    view,
    setView,
    workspaceDatabases,
    openingDatabaseId,
    loadingCatalog,
    routeReady:
      (!selection || appliedSelection === selection.nonce) &&
      model.current.origin === workspaceId &&
      (!schemaWorkspaceId || schemaWorkspaceId === workspaceId),
    openWorkspaceDatabase,
    openSchema,
    openObject,
    show,
  };
}
