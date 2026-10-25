import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
type Diagnostic = { code: string; message: string };
type Coverage = {
  projects: {
    id: string;
    path: string;
    configuration: string;
    targetFramework?: string;
    isComplete: boolean;
    includedFiles: string[];
    excludedFiles: string[];
    diagnostics: Diagnostic[];
  }[];
  diagnostics: Diagnostic[];
  dapperEvidence: number;
  efEvidence: number;
};
export default function AnalysisCoverage({ workspaceId }: { workspaceId: string }) {
  useLocale();
  const [data, setData] = useState<Coverage | null>(null),
    [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  useEffect(() => {
    setData(null);
    setError('');
  }, [workspaceId]);
  async function load() {
    const target = workspaceId;
    setBusy(true);
    setError('');
    try {
      const result = await request<Coverage>('/api/workspaces/' + target + '/analysis-coverage');
      setData(result);
    } catch (e) {
      setError(errorMessage(e, t('Unable to load analysis coverage.')));
    } finally {
      setBusy(false);
    }
  }
  return (
    <details className="ef-mappings product-controls analysis-coverage">
      <summary>{t('EF / Dapper analysis coverage')}</summary>
      <p>
        {t(
          'Static SQL and resolved mappings are evidence. Dynamic SQL and unresolved build context require review; Dapper evidence never establishes a foreign key automatically.',
        )}
      </p>
      <button className="secondary-button" disabled={busy} onClick={() => void load()}>
        {t('Refresh analysis coverage')}
      </button>
      {error && <p role="alert">{error}</p>}
      {data && (
        <>
          <p>
            {t('EF evidence')}: {data.efEvidence} · {t('Dapper evidence')}: {data.dapperEvidence}
          </p>
          {data.projects.map(p => (
            <article key={p.id}>
              <h4>
                <code>{p.path}</code>
              </h4>
              <p>
                {p.configuration} · {p.targetFramework || t('Default framework')} · {p.isComplete ? t('Complete') : t('Partial')}
              </p>
              <details>
                <summary>
                  {t('Included source files')}: {p.includedFiles.length}
                </summary>
                <ul>
                  {p.includedFiles.map(f => (
                    <li key={f}>{f}</li>
                  ))}
                </ul>
              </details>
              <details>
                <summary>
                  {t('Excluded discovered files')}: {p.excludedFiles.length}
                </summary>
                <ul>
                  {p.excludedFiles.map(f => (
                    <li key={f}>{f}</li>
                  ))}
                </ul>
              </details>
              {p.diagnostics.map((d, i) => (
                <p key={i}>
                  <code>{d.code}</code> {tMessage(d.message)}
                </p>
              ))}
            </article>
          ))}
          <h4>{t('Analysis gaps and next actions')}</h4>
          {data.diagnostics.length ? (
            data.diagnostics.map((d, i) => (
              <p key={i}>
                <code>{d.code}</code> {tMessage(d.message)}
                <br />
                {t(
                  d.code === 'DAPPER_SQL_DYNAMIC'
                    ? 'Review the source or move the query into a static SQL template.'
                    : d.code.includes('MAPPING')
                      ? 'Select a database assignment for the project.'
                      : 'Review project configuration, references and the reported source location.',
                )}
              </p>
            ))
          ) : (
            <p>{t('No reported analysis gaps. This does not prove complete coverage of runtime SQL.')}</p>
          )}
        </>
      )}
    </details>
  );
}
