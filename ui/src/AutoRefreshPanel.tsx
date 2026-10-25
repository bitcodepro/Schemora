import { localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useRef, useState } from 'react';
import { errorMessage, request } from './api';
import './enhancements.css';
type AutoSettings = { enabled: boolean; debounceSeconds: number; reconcileMinutes: number };
type AutoRefresh = {
  settings: AutoSettings;
  status: {
    state: string;
    watcherCount: number;
    pending: boolean;
    lastEventAt?: string;
    lastQueuedAt?: string;
    lastJobId?: string;
    diagnostics: (string | { severity?: string; code?: string; message: string })[];
  };
};
const states: Record<string, string> = {
  disabled: 'Off',
  watching: 'Watching source files',
  active: 'Watching source files',
  idle: 'Waiting for changes',
  pending: 'Changes awaiting analysis',
  running: 'Updating model',
  scanning: 'Updating model',
  debouncing: 'Waiting for edits to finish',
  waiting: 'Waiting for changes',
  error: 'Needs attention',
  unavailable: 'File watching unavailable',
  degraded: 'File watching has limitations',
};
const time = (value?: string) => (value ? new Date(value).toLocaleTimeString(localeTag()) : t('not yet'));
export default function AutoRefreshPanel({ workspaceId, onJob }: { workspaceId: string; onJob: (jobId: string) => void }) {
  useLocale();
  const [data, setData] = useState<AutoRefresh | null>(null);
  const [debounce, setDebounce] = useState(3);
  const [reconcile, setReconcile] = useState(5);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const callback = useRef(onJob);
  callback.current = onJob;
  const lastJob = useRef('');
  const current = useRef(workspaceId);
  current.current = workspaceId;
  const dirty = useRef(false);
  const base = `/api/workspaces/${workspaceId}/auto-refresh`;
  function apply(loaded: AutoRefresh) {
    setData(loaded);
    if (!dirty.current) {
      setDebounce(loaded.settings.debounceSeconds);
      setReconcile(loaded.settings.reconcileMinutes);
    }
    if (loaded.status.lastJobId && loaded.status.lastJobId !== lastJob.current) {
      lastJob.current = loaded.status.lastJobId;
      callback.current(loaded.status.lastJobId);
    }
  }
  useEffect(() => {
    setData(null);
    setBusy(false);
    setError('');
    dirty.current = false;
    lastJob.current = '';
    if (!workspaceId) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const loaded = await request<AutoRefresh>(base, { signal: controller.signal });
        if (controller.signal.aborted) return;
        apply(loaded);
        setError('');
        timer = setTimeout(() => void poll(), loaded.settings.enabled ? 2500 : 10000);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(errorMessage(cause, t('Auto-refresh is unavailable.')));
          timer = setTimeout(() => void poll(), 10000);
        }
      }
    }
    void poll();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [workspaceId, refresh]);
  async function save(enabled: boolean) {
    if (busy || !data) return;
    setBusy(true);
    setError('');
    const target = workspaceId;
    const previous = data;
    setData({ ...data, settings: { ...data.settings, enabled } });
    try {
      const loaded = await request<AutoRefresh>(base, {
        method: 'PUT',
        body: JSON.stringify({ enabled, debounceSeconds: debounce, reconcileMinutes: reconcile }),
      });
      if (current.current !== target) return;
      dirty.current = false;
      apply(loaded);
    } catch (cause) {
      if (current.current === target) {
        setData(previous);
        setError(errorMessage(cause, t('Auto-refresh settings were not saved.')));
      }
    } finally {
      if (current.current === target) setBusy(false);
    }
  }
  return (
    <section className="auto-refresh-panel" aria-label={t('Source auto-refresh')}>
      <div className="auto-refresh-heading">
        <div>
          <h3>{t('Auto-refresh')}</h3>
          <p>{t('Watches for changes in root folders and refreshes the schema without building projects.')}</p>
        </div>
        <div className="auto-refresh-controls">
          <label className="auto-refresh-toggle">
            <input
              type="checkbox"
              aria-label={t('Enable auto-refresh')}
              checked={data?.settings.enabled || false}
              disabled={!data || busy}
              onChange={event => void save(event.target.checked)}
            />
            <span>{data?.settings.enabled ? t('On') : t('Off')}</span>
          </label>
          <details>
            <summary className="text-button">{t('Options')}</summary>
            <div className="enhancement-grid">
              <label>
                <span className="field-label">{t('Delay after a change')}</span>
                <select
                  aria-label={t('Auto-refresh delay')}
                  value={debounce}
                  disabled={!data || busy}
                  onChange={event => {
                    dirty.current = true;
                    setDebounce(Number(event.target.value));
                  }}
                >
                  {[...new Set([2, 3, 5, 10, 30, 60, debounce])]
                    .sort((a, b) => a - b)
                    .map(value => (
                      <option value={value} key={value}>
                        {value} {t('sec.')}
                      </option>
                    ))}
                </select>
              </label>
              <label>
                <span className="field-label">{t('Recheck folders')}</span>
                <select
                  aria-label={t('Folder check interval')}
                  value={reconcile}
                  disabled={!data || busy}
                  onChange={event => {
                    dirty.current = true;
                    setReconcile(Number(event.target.value));
                  }}
                >
                  {[...new Set([1, 5, 10, 30, 60, reconcile])]
                    .sort((a, b) => a - b)
                    .map(value => (
                      <option value={value} key={value}>
                        {value} {t('min.')}
                      </option>
                    ))}
                </select>
              </label>
              <button className="secondary-button" disabled={!data || busy} onClick={() => void save(data!.settings.enabled)}>
                {t('Save options')}
              </button>
            </div>
          </details>
        </div>
      </div>
      {data && (
        <div className="auto-refresh-status" role="status">
          <span>{t(states[data.status.state]) || data.status.state}</span>
          <span>
            {t('Watched folders:')} {data.status.watcherCount}
          </span>
          <span>
            {t('Last change:')} {time(data.status.lastEventAt)}
          </span>
          <span>
            {t('Analysis:')} {time(data.status.lastQueuedAt)}
          </span>
          {data.status.pending && <span>{t('Changes are pending for the next analysis')}</span>}
        </div>
      )}
      {data?.status.diagnostics?.map((diagnostic, index) => (
        <p className="object-warning" key={index}>
          {typeof diagnostic === 'string' ? (
            tMessage(diagnostic)
          ) : (
            <>
              {diagnostic.code && `${diagnostic.code}: `}
              {tMessage(diagnostic.message)}
            </>
          )}
        </p>
      ))}
      {error && (
        <p className="job-error" role="alert">
          {tMessage(error)}{' '}
          <button className="text-button" onClick={() => setRefresh(value => value + 1)}>
            {t('Retry')}
          </button>
        </p>
      )}
    </section>
  );
}
