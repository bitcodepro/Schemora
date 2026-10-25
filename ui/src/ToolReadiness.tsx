import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import Icon from './Icon';
import { sessionToken } from './api';

type Capability = { name: string; available: boolean; path?: string; version?: string; detail?: string };
export type Diagnostics = { runtime: string; os: string; architecture: string; dataDirectory: string; capabilities: Capability[] };
type Recovery = { severity: string; code: string; message: string };

export default function ToolReadiness({
  diagnostics,
  busy: diagnosticsBusy,
  error: diagnosticsError,
  dockerRecovery,
  onRefresh,
}: {
  diagnostics: Diagnostics | null;
  busy: boolean;
  error: string;
  dockerRecovery: Recovery[];
  onRefresh: () => void;
}) {
  useLocale();
  return (
    <section className="diagnostics-section" id="settings-tools">
      {dockerRecovery.map((diagnostic, index) => (
        <div className="alert warning" key={`${diagnostic.code}-${index}`} role="alert">
          <Icon name="info" />
          <div>
            <strong>{diagnostic.code}</strong>
            <p>{tMessage(diagnostic.message)}</p>
          </div>
        </div>
      ))}
      <div className="section-heading">
        <h2>{t('Tool readiness')}</h2>
        <button className="secondary-button" disabled={diagnosticsBusy || !sessionToken} onClick={() => onRefresh()}>
          <Icon name="refresh" size={15} />
          {diagnosticsBusy ? t('Checking…') : t('Check again')}
        </button>
      </div>
      {diagnosticsError && (
        <div className="alert error" role="alert">
          <Icon name="info" />
          <div>
            <strong>{t('Check unavailable')}</strong>
            <p>{tMessage(diagnosticsError)}</p>
          </div>
        </div>
      )}
      {diagnostics ? (
        <>
          <div className="capabilities">
            {diagnostics.capabilities.map(capability => (
              <article className="capability-card" key={capability.name}>
                <div className={`capability-icon ${capability.available ? 'available' : ''}`}>
                  <Icon name={capability.available ? 'check' : 'tool'} size={21} />
                </div>
                <div className="capability-info">
                  <h3>{capability.name}</h3>
                  {capability.version && <code className="capability-version">{capability.version}</code>}
                  {capability.detail && <p>{tMessage(capability.detail)}</p>}
                  {capability.path && <code className="capability-path">{capability.path}</code>}
                </div>
                <span className={capability.available ? 'confirmed-tag' : 'neutral-tag'}>
                  {capability.available ? t('Available') : t('Unavailable #2')}
                </span>
              </article>
            ))}
          </div>
          <div className="environment-card">
            <h3>{t('Local application')}</h3>
            <dl>
              <div>
                <dt>{t('Runtime')}</dt>
                <dd>{diagnostics.runtime}</dd>
              </div>
              <div>
                <dt>{t('System')}</dt>
                <dd>
                  {diagnostics.os} · {diagnostics.architecture}
                </dd>
              </div>
              <div>
                <dt>{t('Application data')}</dt>
                <dd className="code-text">{diagnostics.dataDirectory}</dd>
              </div>
            </dl>
          </div>
          <p className="diagnostics-footnote">
            <Icon name="info" size={15} />
            {t('Reading an existing DACPAC does not require Docker or a SQL project build SDK.')}
          </p>
        </>
      ) : diagnosticsBusy ? (
        <div className="diagnostics-loading">
          <span className="spinner" />
          {t('Checking the local environment…')}
        </div>
      ) : (
        !diagnosticsError && (
          <div className="detail-empty">
            <Icon name="tool" size={32} />
            <p>{t('Tool checks will be available once a local session is open.')}</p>
          </div>
        )
      )}
    </section>
  );
}
