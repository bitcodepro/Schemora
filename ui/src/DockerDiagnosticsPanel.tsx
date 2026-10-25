import { useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage, localeTag } from './i18n';
import { useLocale } from './i18nReact';
type Report = { id: string; at: string; stage: string; elapsedMs: number; code: string; exitCode?: number; output: string };
export default function DockerDiagnosticsPanel() {
  useLocale();
  const [reports, setReports] = useState<Report[] | null>(null),
    [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  return (
    <details className="professional-section">
      <summary>{t('Docker failure diagnostics')}</summary>
      <p>{t('The last 20 failures include the operation stage, duration and bounded output with passwords removed.')}</p>
      {error && <p role="alert">{tMessage(error)}</p>}
      <button
        className="secondary-button"
        disabled={busy}
        onClick={() => {
          setBusy(true);
          setError('');
          void request<Report[]>('/api/diagnostics/docker')
            .then(setReports)
            .catch(e => setError(errorMessage(e, t('Workspace operation failed.'))))
            .finally(() => setBusy(false));
        }}
      >
        {t('Load Docker diagnostics')}
      </button>
      {reports && <p role="status">{t('Recorded failures: {0}', reports.length)}</p>}
      {reports?.map(r => (
        <article key={r.id}>
          <h4>{r.code}</h4>
          <p>
            {new Date(r.at).toLocaleString(localeTag())} · {t('Operation stage')}: {r.stage} · {r.elapsedMs} ms
            {r.exitCode != null && (
              <>
                {' '}
                · {t('Exit code')}: {r.exitCode}
              </>
            )}
          </p>
          <pre className="search-excerpt">{r.output}</pre>
        </article>
      ))}
    </details>
  );
}
