import QualityRuleSettings from './QualityRuleSettings';
import WorkspaceTools from './WorkspaceTools';
import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { useWorkspaces } from './workspaceContext';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
import SourceLocation from './SourceLocation';
import './productImprovements.css';
type Finding = {
  id: string;
  rule: string;
  severity: string;
  databaseId: string;
  objectId?: string;
  title: string;
  explanation: string;
  sourcePath?: string;
  line?: number;
  fingerprint: string;
  suppressionReason?: string;
};
export default function WorkspaceQuality({
  onRecipe,
  onObject,
}: {
  onRecipe: (workspace: string, recipe: string) => void;
  onObject: (workspace: string, database: string, object: string, matchText?: string) => void;
}) {
  useLocale();
  const { workspaceId: id } = useWorkspaces();
  const [findings, setFindings] = useState<Finding[]>([]),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false),
    [showHidden, setShowHidden] = useState(false),
    [reasons, setReasons] = useState<Record<string, string>>({}),
    [severity, setSeverity] = useState(''),
    [offset, setOffset] = useState(0),
    [section, setSection] = useState<'quality' | 'tools'>('quality');
  useEffect(() => {
    setFindings([]);
    setOffset(0);
    if (!id) return;
    const c = new AbortController();
    setBusy(true);
    void request<Finding[]>('/api/workspaces/' + id + '/quality', { signal: c.signal })
      .then(setFindings)
      .catch(e => {
        if (!c.signal.aborted) setError(errorMessage(e, t('Unable to load quality findings.')));
      })
      .finally(() => {
        if (!c.signal.aborted) setBusy(false);
      });
    return () => c.abort();
  }, [id]);
  async function refresh() {
    setBusy(true);
    setError('');
    try {
      setFindings(await request<Finding[]>('/api/workspaces/' + id + '/quality'));
    } catch (e) {
      setError(errorMessage(e, t('Unable to load quality findings.')));
    } finally {
      setBusy(false);
    }
  }
  async function review(f: Finding) {
    setBusy(true);
    setError('');
    try {
      await request('/api/workspaces/' + id + '/quality/review', {
        method: 'POST',
        body: JSON.stringify({ findingId: f.id, fingerprint: f.fingerprint, reason: reasons[f.id] || '', suppress: !f.suppressionReason }),
      });
      await refresh();
    } catch (e) {
      setError(errorMessage(e, t('Quality review failed.')));
    } finally {
      setBusy(false);
    }
  }
  const visible = findings.filter(f => (showHidden || !f.suppressionReason) && (!severity || f.severity === severity));
  return (
    <section className="settings-card product-controls professional-tools" aria-label={t('Workspace quality')}>
      <h2>{t('Workspace quality')}</h2>
      {error && <p role="alert">{tMessage(error)}</p>}
      {id && (
        <>
          <div className="recipe-main-tabs">
            <button
              aria-pressed={section === 'quality'}
              className={'secondary-button ' + (section === 'quality' ? 'selected' : '')}
              onClick={() => setSection('quality')}
            >
              {t('Quality findings')}
            </button>
            <button
              aria-pressed={section === 'tools'}
              className={'secondary-button ' + (section === 'tools' ? 'selected' : '')}
              onClick={() => setSection('tools')}
            >
              {t('Analysis, search and recovery')}
            </button>
          </div>
          {section === 'quality' ? (
            <>
              <QualityRuleSettings key={id} workspaceId={id} onSaved={refresh} />
              <p>{t('Findings are review guidance, not automatic schema changes. A table without a primary key can be intentional.')}</p>
              <div className="quality-toolbar">
                <label>
                  {t('Severity')}
                  <select
                    value={severity}
                    onChange={e => {
                      setSeverity(e.target.value);
                      setOffset(0);
                    }}
                  >
                    <option value="">{t('All severities')}</option>
                    {['error', 'warning', 'info'].map(s => (
                      <option key={s} value={s}>
                        {t('Severity: ' + s)}
                      </option>
                    ))}
                  </select>
                </label>
                <label className="quality-toggle">
                  <input
                    type="checkbox"
                    checked={showHidden}
                    onChange={e => {
                      setShowHidden(e.target.checked);
                      setOffset(0);
                    }}
                  />
                  {t('Show reviewed findings')}
                </label>
                <span role="status">{t('Findings shown: {0} / {1}', visible.length, findings.length)}</span>
                <button className="secondary-button" disabled={busy} onClick={() => void refresh()}>
                  {t('Refresh quality findings')}
                </button>
              </div>
              {visible.slice(offset, offset + 50).map(f => (
                <article key={f.id} className={'quality-finding severity-' + f.severity}>
                  <div className="quality-finding-head">
                    <span className="severity-badge">{t('Severity: ' + f.severity)}</span>
                    <h3>{f.title}</h3>
                    {!f.rule.endsWith(f.title) && <code>{f.rule}</code>}
                  </div>
                  <p>{tMessage(f.explanation)}</p>
                  {f.sourcePath && <SourceLocation path={f.sourcePath} line={f.line} />}
                  {f.suppressionReason ? (
                    <div className="quality-review">
                      <p className="quality-reviewed">{f.suppressionReason}</p>
                      <button className="secondary-button" disabled={busy} onClick={() => void review(f)}>
                        {t('Reopen finding')}
                      </button>
                    </div>
                  ) : (
                    <div className="quality-review">
                      <input
                        type="text"
                        aria-label={t('Review reason')}
                        placeholder={t('Review reason')}
                        value={reasons[f.id] || ''}
                        maxLength={2000}
                        onChange={e => setReasons(v => ({ ...v, [f.id]: e.target.value }))}
                      />
                      <button
                        className="secondary-button"
                        disabled={busy || (reasons[f.id] || '').trim().length < 10}
                        onClick={() => void review(f)}
                      >
                        {t('Mark as reviewed')}
                      </button>
                    </div>
                  )}
                </article>
              ))}
              {visible.length > 50 && (
                <div className="quality-pages">
                  <button className="secondary-button" disabled={!offset} onClick={() => setOffset(Math.max(0, offset - 50))}>
                    {t('Previous page')}
                  </button>
                  <button className="secondary-button" disabled={offset + 50 >= visible.length} onClick={() => setOffset(offset + 50)}>
                    {t('Next page')}
                  </button>
                </div>
              )}
            </>
          ) : (
            <WorkspaceTools key={id} workspaceId={id} onRecipe={onRecipe} onObject={onObject} />
          )}
        </>
      )}
    </section>
  );
}
