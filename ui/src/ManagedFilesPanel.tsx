import { useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
type Policy = { keepDays: number; keepBackups: number };
type Plan = {
  revision: string;
  policy: Policy;
  totalBytes: number;
  reclaimableBytes: number;
  files: { path: string; category: string; bytes: number; eligible: boolean; reason: string }[];
};
export default function ManagedFilesPanel() {
  useLocale();
  const [plan, setPlan] = useState<Plan | null>(null),
    [policy, setPolicy] = useState<Policy>({ keepDays: 30, keepBackups: 3 }),
    [busy, setBusy] = useState(false),
    [error, setError] = useState(''),
    [confirmed, setConfirmed] = useState(false),
    [notice, setNotice] = useState(''),
    [offset, setOffset] = useState(0);
  async function load() {
    const result = await request<Plan>('/api/storage/files');
    setPlan(result);
    setPolicy(result.policy);
    setConfirmed(false);
    setOffset(0);
  }
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    setNotice('');
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e, t('Workspace operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  const mib = (bytes: number) => (bytes / 1048576).toFixed(2) + ' MiB';
  return (
    <details className="professional-section">
      <summary>{t('Managed files and disk space')}</summary>
      <p>{t('Preview app-owned files before deleting them. Recent backups, referenced manifests and package caches are protected.')}</p>
      {error && <p role="alert">{tMessage(error)}</p>}
      <button className="secondary-button" disabled={busy} onClick={() => void run(load)}>
        {t('Inspect managed files')}
      </button>
      {plan && (
        <>
          <label>
            {t('Keep files for days')}
            <input
              type="number"
              min={1}
              max={3650}
              value={policy.keepDays}
              onChange={e => {
                setPolicy(p => ({ ...p, keepDays: Number(e.target.value) }));
                setConfirmed(false);
              }}
            />
          </label>
          <label>
            {t('Minimum backups to keep')}
            <input
              type="number"
              min={2}
              max={100}
              value={policy.keepBackups}
              onChange={e => {
                setPolicy(p => ({ ...p, keepBackups: Number(e.target.value) }));
                setConfirmed(false);
              }}
            />
          </label>
          <button
            className="secondary-button"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                await request('/api/storage/files/policy', { method: 'PUT', body: JSON.stringify(policy) });
                await load();
              })
            }
          >
            {t('Save retention and preview')}
          </button>
          <p role="status">
            {t('Managed size')}: {mib(plan.totalBytes)} · {t('Eligible for cleanup')}: {mib(plan.reclaimableBytes)}
          </p>
          <ul>
            {plan.files.slice(offset, offset + 50).map(f => (
              <li key={f.path}>
                <code>{f.path}</code> · {mib(f.bytes)} · {t(f.reason)}
              </li>
            ))}
          </ul>
          <button className="text-button" disabled={busy || !offset} onClick={() => setOffset(Math.max(0, offset - 50))}>
            {t('Previous page')}
          </button>
          <button className="text-button" disabled={busy || offset + 50 >= plan.files.length} onClick={() => setOffset(offset + 50)}>
            {t('Next page')}
          </button>
          <label>
            <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
            {t('I reviewed the file cleanup preview and approve deleting eligible files.')}
          </label>
          <button
            className="secondary-button"
            disabled={busy || !confirmed || !plan.files.some(f => f.eligible) || JSON.stringify(policy) !== JSON.stringify(plan.policy)}
            onClick={() =>
              void run(async () => {
                const result = await request<{ deleted: number; deletedBytes: number }>('/api/storage/files/cleanup', {
                  method: 'POST',
                  body: JSON.stringify({ expectedRevision: plan.revision }),
                });
                await load();
                setNotice(t('Removed files: {0}', result.deleted) + ' · ' + mib(result.deletedBytes));
              })
            }
          >
            {t('Delete eligible files')}
          </button>
        </>
      )}
      {notice && <p role="status">{notice}</p>}
    </details>
  );
}
