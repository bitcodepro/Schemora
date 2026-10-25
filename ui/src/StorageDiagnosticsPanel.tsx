import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { t, localeTag } from './i18n';
import { useLocale } from './i18nReact';
type Policy = { lowMemory: boolean; keepSnapshotsPerDatabase: number; keepHistoryDays: number };
type Plan = { revision: string; snapshots: number; historyRecords: number; estimatedBytes: number; protectedSnapshotIds: string[] };
type Event = { operationId: string; at: string; code: string; status: number; jobId?: string; progress?: number };
type Size = { catalogBytes: number; walBytes: number; tables: { table: string; records: number; bytes: number }[] };
export default function StorageDiagnosticsPanel() {
  useLocale();
  const [policy, setPolicy] = useState<Policy | null>(null);
  const [size, setSize] = useState<Size | null>(null);
  const [plan, setPlan] = useState<Plan | null>(null);
  const [events, setEvents] = useState<Event[]>([]);
  const [jobs, setJobs] = useState<{ id: string; status: string }[]>([]);
  const [jobId, setJobId] = useState('');
  const [includePaths, setIncludePaths] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  async function refresh() {
    const [p, s, j] = await Promise.all([
      request<Policy>('/api/storage/policy'),
      request<Size>('/api/storage/size'),
      request<{ id: string; status: string }[]>('/api/jobs'),
    ]);
    setPolicy(p);
    setSize(s);
    setJobs(j);
  }
  useEffect(() => {
    void refresh().catch(e => setError(errorMessage(e, t('Unable to load storage details.'))));
  }, []);
  useEffect(() => {
    const controller = new AbortController();
    void request<Event[]>(jobId ? `/api/jobs/${jobId}/log` : '/api/diagnostics/events', { signal: controller.signal })
      .then(setEvents)
      .catch(e => {
        if (!controller.signal.aborted) setError(errorMessage(e, t('Unable to load diagnostics.')));
      });
    return () => controller.abort();
  }, [jobId]);
  async function run(action: () => Promise<void>) {
    if (busy) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e, t('Operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  return (
    <>
      <section className="settings-card product-controls" id="settings-history">
        <h2>{t('Memory and history')}</h2>
        {error && (
          <p role="alert" className="job-error">
            {error}
          </p>
        )}
        {notice && <p role="status">{notice}</p>}
        {policy && (
          <>
            <label>
              <input
                type="checkbox"
                checked={policy.lowMemory}
                onChange={e => {
                  setPolicy({ ...policy, lowMemory: e.target.checked });
                  setPlan(null);
                }}
              />
              {t('Low memory mode: keep graph browsing in the indexed catalog')}
            </label>
            <label>
              {t('Minimum snapshots per database')}
              <input
                type="number"
                min="2"
                max="100"
                value={policy.keepSnapshotsPerDatabase}
                onChange={e => {
                  setPolicy({ ...policy, keepSnapshotsPerDatabase: Number(e.target.value) });
                  setPlan(null);
                }}
              />
            </label>
            <label>
              {t('Keep history for days')}
              <input
                type="number"
                min="1"
                max="3650"
                value={policy.keepHistoryDays}
                onChange={e => {
                  setPolicy({ ...policy, keepHistoryDays: Number(e.target.value) });
                  setPlan(null);
                }}
              />
            </label>
            <button
              className="secondary-button"
              disabled={busy}
              onClick={() =>
                void run(async () => {
                  await request('/api/storage/policy', { method: 'PUT', body: JSON.stringify(policy) });
                  setPlan(null);
                  setNotice(t('Retention policy saved. Cleanup only runs when explicitly requested.'));
                })
              }
            >
              {t('Save retention policy')}
            </button>
          </>
        )}
        {size && (
          <p>
            {t('Catalog size')}: {(size.catalogBytes / 1048576).toFixed(1)} MiB · WAL {(size.walBytes / 1048576).toFixed(1)} MiB
          </p>
        )}
        <p>
          {t(
            'Current snapshots, recipe compatibility snapshots, retained validation references and recipe versions are protected. Cleanup creates a backup first. SQLite may reuse freed pages without shrinking the file.',
          )}
        </p>
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() => void run(async () => setPlan(await request<Plan>('/api/storage/cleanup/preview', { method: 'POST' })))}
        >
          {t('Preview history cleanup')}
        </button>
        {plan && (
          <div>
            <p>
              {t(
                'Eligible snapshots: {0}; history records: {1}; estimated record data: {2} MiB.',
                plan.snapshots,
                plan.historyRecords,
                (plan.estimatedBytes / 1048576).toFixed(1),
              )}
            </p>
            <p>
              {t('Protected snapshots')}: {plan.protectedSnapshotIds.length}
            </p>
            <button
              className="secondary-button"
              disabled={busy || plan.snapshots + plan.historyRecords === 0}
              onClick={() =>
                void run(async () => {
                  await request('/api/storage/cleanup', { method: 'POST', body: JSON.stringify({ expectedRevision: plan.revision }) });
                  setPlan(null);
                  await refresh();
                  setNotice(t('History cleanup completed. A backup was created.'));
                })
              }
            >
              {t('Back up and clean eligible history')}
            </button>
          </div>
        )}
      </section>
      <section className="settings-card product-controls" id="settings-diagnostics">
        <h2>{t('Diagnostics and support report')}</h2>
        <p>
          {t(
            'The report includes operation codes and technical context. Tokens, SQL, pasted IDs, exception messages and file contents are excluded. Root paths are optional.',
          )}
        </p>
        <label>
          <input type="checkbox" checked={includePaths} onChange={e => setIncludePaths(e.target.checked)} />
          {t('Include workspace root paths')}
        </label>
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() =>
            void run(async () => {
              const report = await request('/api/diagnostics/report', { method: 'POST', body: JSON.stringify({ includePaths }) });
              const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }));
              const a = document.createElement('a');
              a.href = url;
              a.download = 'schemora-diagnostics.json';
              a.click();
              setTimeout(() => URL.revokeObjectURL(url), 1000);
            })
          }
        >
          {t('Download diagnostic report')}
        </button>
        <label>
          {t('Job log')}
          <select value={jobId} onChange={e => setJobId(e.target.value)}>
            <option value="">{t('Recent application events')}</option>
            {jobs.map(j => (
              <option key={j.id} value={j.id}>
                {j.id} · {j.status}
              </option>
            ))}
          </select>
        </label>
        <button
          className="secondary-button"
          disabled={busy}
          onClick={() =>
            void run(async () => {
              setEvents(await request<Event[]>(jobId ? `/api/jobs/${jobId}/log` : '/api/diagnostics/events'));
            })
          }
        >
          {t('Refresh log')}
        </button>
        <div className="workspace-table-scroll">
          <table className="column-table">
            <thead>
              <tr>
                <th>{t('Time')}</th>
                <th>{t('Code')}</th>
                <th>{t('Operation')}</th>
                <th>{t('Progress')}</th>
              </tr>
            </thead>
            <tbody>
              {events.slice(0, 100).map((event, index) => (
                <tr key={event.operationId + index}>
                  <td>{new Date(event.at).toLocaleString(localeTag())}</td>
                  <td>{event.code}</td>
                  <td>
                    <code>{event.operationId}</code>
                  </td>
                  <td>{event.progress ?? '—'}</td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      </section>
    </>
  );
}
