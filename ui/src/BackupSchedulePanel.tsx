import { useEffect, useState } from 'react';
import { errorMessage, request } from './api';
import { localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';

type Schedule = { enabled: boolean; intervalHours: number; keep: number };
type Status = {
  schedule: Schedule;
  backups: { name: string; path: string; bytes: number; createdAt: string }[];
  nextAt?: string | null;
  lastError?: string | null;
  lastErrorAt?: string | null;
};
const intervals = [6, 12, 24, 72, 168];

/** Catalog copies on a schedule; the host keeps the newest N automatic copies and never touches other backups. */
export default function BackupSchedulePanel() {
  useLocale();
  const [status, setStatus] = useState<Status | null>(null);
  const [draft, setDraft] = useState<Schedule | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  useEffect(() => {
    const controller = new AbortController();
    request<Status>('/api/storage/backup-schedule', { signal: controller.signal })
      .then(loaded => {
        setStatus(loaded);
        setDraft(loaded.schedule);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Automatic backups are unavailable.')));
      });
    return () => controller.abort();
  }, []);
  async function run(action: () => Promise<Status>) {
    setBusy(true);
    setError('');
    try {
      const next = await action();
      setStatus(next);
      setDraft(next.schedule);
    } catch (cause) {
      setError(errorMessage(cause, t('Automatic backups are unavailable.')));
    } finally {
      setBusy(false);
    }
  }
  const date = (value: string) => new Date(value).toLocaleString(localeTag());
  const changed = draft && status && JSON.stringify(draft) !== JSON.stringify(status.schedule);
  return (
    <section className="backup-schedule" aria-label={t('Automatic backups')}>
      <h3>{t('Automatic backups')}</h3>
      {draft && (
        <div className="backup-schedule-form">
          <label className="backup-schedule-toggle">
            <input type="checkbox" checked={draft.enabled} onChange={event => setDraft({ ...draft, enabled: event.target.checked })} />
            {t('Copy the catalog on a schedule')}
          </label>
          <label>
            <span className="field-label">{t('Every')}</span>
            <select
              value={draft.intervalHours}
              disabled={!draft.enabled}
              onChange={event => setDraft({ ...draft, intervalHours: Number(event.target.value) })}
            >
              {[...new Set([...intervals, draft.intervalHours])]
                .sort((a, b) => a - b)
                .map(hours => (
                  <option key={hours} value={hours}>
                    {hours % 24 === 0 ? t('{0} d', hours / 24) : t('{0} h', hours)}
                  </option>
                ))}
            </select>
          </label>
          <label>
            <span className="field-label">{t('Keep the newest')}</span>
            <input
              type="number"
              min={1}
              max={100}
              value={draft.keep}
              onChange={event => setDraft({ ...draft, keep: Math.max(1, Math.min(100, Number(event.target.value) || 1)) })}
            />
          </label>
          <button
            className="secondary-button"
            disabled={busy || !changed}
            onClick={() => void run(() => request<Status>('/api/storage/backup-schedule', { method: 'PUT', body: JSON.stringify(draft) }))}
          >
            {t('Save schedule')}
          </button>
          <button
            className="secondary-button"
            disabled={busy}
            onClick={() => void run(() => request<Status>('/api/storage/backup-schedule/run', { method: 'POST' }))}
          >
            {t('Back up now')}
          </button>
        </div>
      )}
      {status && (
        <p className="muted-text" role="status">
          {status.schedule.enabled && status.nextAt ? t('Next copy: {0}', date(status.nextAt)) : t('Automatic copies are off.')}{' '}
          {t('Copies are kept in the backups folder of the data directory; older automatic copies beyond the limit are deleted.')}
        </p>
      )}
      {status && status.backups.length > 0 && (
        <ul className="backup-schedule-list">
          {status.backups.map(backup => (
            <li key={backup.name}>
              <code>{backup.name}</code> · {date(backup.createdAt)} ·{' '}
              {(backup.bytes / 1024).toLocaleString(localeTag(), { maximumFractionDigits: 0 })} KiB
            </li>
          ))}
        </ul>
      )}
      {status?.lastError && (
        <div className="alert error" role="alert">
          <p>
            {t('The last automatic copy failed')} · {status.lastErrorAt ? date(status.lastErrorAt) : ''}: {status.lastError}
          </p>
        </div>
      )}
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
    </section>
  );
}
