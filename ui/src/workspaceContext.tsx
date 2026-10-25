import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState } from 'react';
import type { ReactNode } from 'react';
import { errorMessage, request, sessionToken } from './api';
import { t } from './i18n.ts';
import type { Workspace } from './types';
import { relativeToRoots } from './paths.ts';

const storageKey = 'schemora.workspace';

type WorkspaceSelection = {
  workspaces: Workspace[];
  workspaceId: string;
  loading: boolean;
  error: string;
  select: (id: string, options?: { confirmed?: boolean }) => boolean;
  reload: (preferId?: string) => Promise<Workspace[]>;
  upsert: (workspace: Workspace) => void;
  /** A page with unsaved work can veto switching; return false to keep the current workspace. */
  setGuard: (guard: (() => boolean) | null) => void;
};

const WorkspaceContext = createContext<WorkspaceSelection | null>(null);

function remembered() {
  try {
    return localStorage.getItem(storageKey) || '';
  } catch {
    return '';
  }
}

function remember(id: string) {
  try {
    if (id) localStorage.setItem(storageKey, id);
  } catch {
    /* Selection still works for this tab without storage. */
  }
}

/** One workspace selection shared by every page; the sidebar selector is its only control. */
export function WorkspaceProvider({ children }: { children: ReactNode }) {
  const [workspaces, setWorkspaces] = useState<Workspace[]>([]);
  const [workspaceId, setWorkspaceId] = useState('');
  const [loading, setLoading] = useState(Boolean(sessionToken));
  const [error, setError] = useState('');
  const selectedRef = useRef(workspaceId);
  selectedRef.current = workspaceId;
  const guardRef = useRef<(() => boolean) | null>(null);

  const select = useCallback((id: string, options?: { confirmed?: boolean }) => {
    if (id !== selectedRef.current && !options?.confirmed && guardRef.current && !guardRef.current()) return false;
    selectedRef.current = id;
    setWorkspaceId(id);
    remember(id);
    return true;
  }, []);
  const setGuard = useCallback((guard: (() => boolean) | null) => {
    guardRef.current = guard;
  }, []);

  const reload = useCallback(
    async (preferId?: string) => {
      if (!sessionToken) {
        document.documentElement.dataset.appReady = 'true';
        return [];
      }
      setLoading(true);
      try {
        const loaded = await request<Workspace[]>('/api/workspaces');
        setWorkspaces(loaded);
        setError('');
        const wanted = [preferId, selectedRef.current, remembered()].find(id => id && loaded.some(item => item.id === id));
        select(wanted || loaded[0]?.id || '');
        return loaded;
      } catch (cause) {
        setError(errorMessage(cause, t('Could not load workspaces.')));
        return [];
      } finally {
        setLoading(false);
        // Browser tests wait for this marker instead of guessing when the first workspace list has arrived.
        document.documentElement.dataset.appReady = 'true';
      }
    },
    [select],
  );

  const upsert = useCallback((workspace: Workspace) => {
    setWorkspaces(previous => [...previous.filter(item => item.id !== workspace.id), workspace]);
  }, []);

  useEffect(() => {
    void reload();
  }, [reload]);

  const value = useMemo(
    () => ({ workspaces, workspaceId, loading, error, select, reload, upsert, setGuard }),
    [workspaces, workspaceId, loading, error, select, reload, upsert, setGuard],
  );
  return <WorkspaceContext.Provider value={value}>{children}</WorkspaceContext.Provider>;
}

export function useWorkspaces(): WorkspaceSelection {
  const value = useContext(WorkspaceContext);
  if (!value) throw new Error('useWorkspaces must be used inside WorkspaceProvider');
  return value;
}

export function useDisplayPath(): (path: string) => string {
  const { workspaces, workspaceId } = useWorkspaces();
  const roots = workspaces.find(item => item.id === workspaceId)?.roots;
  return useCallback((path: string) => (roots ? relativeToRoots(path, roots) : path), [roots]);
}

export function WorkspaceSelector({ className }: { className: string }) {
  const { workspaces, workspaceId, loading, select } = useWorkspaces();
  return (
    <label className={className}>
      <select
        aria-label={t('Workspace')}
        value={workspaceId}
        disabled={loading && !workspaces.length}
        onChange={event => select(event.target.value)}
      >
        {!workspaces.length && <option value="">{loading ? t('Loading…') : t('No workspaces')}</option>}
        {workspaces.map(item => (
          <option value={item.id} key={item.id}>
            {item.name}
          </option>
        ))}
      </select>
    </label>
  );
}
