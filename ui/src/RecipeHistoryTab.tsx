import { lazy, Suspense, useEffect, useState } from 'react';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { errorMessage, request } from './api';
import { compareRecipe } from './recipeDrafts';
import { formatDate, statusLabels } from './recipeFormat';
import type { LineageItem, RecipeEntry, RecipeTestResult, RecipeVersion } from './recipeTypes';
const SqlEditor = lazy(() => import('./SqlEditor'));

/** Versions, lineage and synthetic checks of a saved recipe; mount it with the recipe id as key. */
export default function RecipeHistoryTab({
  entry,
  dirty,
  busy,
  onRestore,
}: {
  entry: RecipeEntry;
  dirty: boolean;
  busy: boolean;
  onRestore: (version: RecipeVersion) => void;
}) {
  useLocale();
  const recipeId = entry.definition.id;
  const [versions, setVersions] = useState<RecipeVersion[]>([]);
  const [versionPreview, setVersionPreview] = useState<RecipeVersion | null>(null);
  const [comparison, setComparison] = useState<{
    current: RecipeEntry['validation'];
    selected: RecipeEntry['validation'];
    lastRecorded?: RecipeEntry['validation'];
  } | null>(null);
  const [comparisonError, setComparisonError] = useState('');
  const [lineage, setLineage] = useState<LineageItem[]>([]);
  const [historyError, setHistoryError] = useState('');
  const [testResult, setTestResult] = useState<RecipeTestResult | null>(null);
  const [testBusy, setTestBusy] = useState(false);
  const [testError, setTestError] = useState('');
  useEffect(() => {
    setHistoryError('');
    const controller = new AbortController();
    Promise.all([
      request<RecipeVersion[]>(`/api/recipes/${encodeURIComponent(recipeId)}/versions`, { signal: controller.signal }),
      request<LineageItem[]>(`/api/recipes/${encodeURIComponent(recipeId)}/lineage`, { signal: controller.signal }),
    ])
      .then(([history, dependencies]) => {
        setVersions(history);
        setLineage(dependencies);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setHistoryError(errorMessage(cause, t('History or dependencies are unavailable.')));
      });
    return () => controller.abort();
  }, [recipeId, entry.definition.version]);
  useEffect(() => {
    setComparison(null);
    setComparisonError('');
    if (!versionPreview) return;
    const c = new AbortController();
    void request<NonNullable<typeof comparison>>(
      '/api/recipes/' + encodeURIComponent(recipeId) + '/versions/' + versionPreview.version + '/comparison',
      { signal: c.signal },
    )
      .then(setComparison)
      .catch(e => {
        if (!c.signal.aborted) setComparisonError(errorMessage(e, t('Could not compare version.')));
      });
    return () => c.abort();
  }, [recipeId, versionPreview, entry.definition.version]);
  async function runTests() {
    setTestBusy(true);
    setTestError('');
    try {
      setTestResult(await request<RecipeTestResult>(`/api/recipes/${encodeURIComponent(recipeId)}/tests`, { method: 'POST', body: '{}' }));
    } catch (cause) {
      setTestError(errorMessage(cause, t('Synthetic checks did not finish.')));
    } finally {
      setTestBusy(false);
    }
  }
  return (
    <div className="recipe-history">
      {historyError && <p className="job-error">{tMessage(historyError)}</p>}
      <section className="recipe-lineage">
        <h3>{t('Computed dependencies')}</h3>
        {lineage.length ? (
          <div>
            {lineage.map((item, index) => (
              <article key={index}>
                <strong>
                  {item.databaseName}.{item.schema}.{item.tableName}
                  {item.columnName ? `.${item.columnName}` : ''}
                </strong>
                {item.relationId && <code>Relation: {item.relationId}</code>}
              </article>
            ))}
          </div>
        ) : (
          <p className="muted-text">
            {t(
              'Dependencies have not been established yet, or the template does not reference schema objects. Check the compatibility diagnostics.',
            )}
          </p>
        )}
      </section>
      <section className="recipe-tests">
        <div className="section-heading">
          <h3>{t('Synthetic fixtures')}</h3>
          <button className="secondary-button" disabled={dirty || testBusy || busy} onClick={() => void runTests()}>
            {testBusy ? t('Checking…') : t('Run checks')}
          </button>
        </div>
        <p>
          {t('The server creates test values from the type definitions and checks the renderer. Pasted IDs are not used in these checks.')}
        </p>
        {testError && <p className="job-error">{tMessage(testError)}</p>}
        {testResult && (
          <div className="test-result">
            <strong>
              {testResult.success ? t('Checks passed') : t('Errors found')} ·{' '}
              {Array.isArray(testResult.checks) ? testResult.checks.length : testResult.checks} {t('checks')}
            </strong>
            <span>{formatDate(testResult.testedAt)}</span>
            {testResult.diagnostics.map((diagnostic, index) => (
              <p key={index}>
                {diagnostic.code}: {tMessage(diagnostic.message)}
              </p>
            ))}
          </div>
        )}
      </section>
      <section className="recipe-versions">
        <h3>{t('Version history')}</h3>
        {versions.map(version => (
          <div key={version.id} className="recipe-version-row">
            <div>
              <strong>v{version.version}</strong>
              <span>{formatDate(version.createdAt)}</span>
              <small>{tMessage(version.reason)}</small>
            </div>
            <button className="text-button" onClick={() => setVersionPreview(version)}>
              {t('View SQL')}
            </button>
            <button
              className="secondary-button"
              disabled={busy || version.version === entry.definition.version}
              onClick={() => setVersionPreview(version)}
            >
              {t('Restore')}
            </button>
          </div>
        ))}
        {versionPreview && (
          <div className="version-preview">
            <h4>{t('Compare with current version')}</h4>
            {comparisonError && <p role="alert">{comparisonError}</p>}
            {comparison && (
              <>
                <p>
                  {t('Current validation')}: {t(statusLabels[comparison.current.status])} · {t('Selected version')}:{' '}
                  {t(statusLabels[comparison.selected.status])} · {t('Last recorded validation')}:{' '}
                  {comparison.lastRecorded ? t(statusLabels[comparison.lastRecorded.status]) : t('Unavailable')}
                </p>
                <details>
                  <summary>{t('Dependency comparison')}</summary>
                  <div className="recipe-comparison">
                    <pre>{JSON.stringify(comparison.current.dependencies, null, 2)}</pre>
                    <pre>{JSON.stringify(comparison.selected.dependencies, null, 2)}</pre>
                  </div>
                </details>
                {comparison.selected.diagnostics.map((d, i) => (
                  <p key={i}>
                    <code>{d.code}</code> {tMessage(d.message)}
                  </p>
                ))}
              </>
            )}
            <p>
              {t('Current validation')}: {t(statusLabels[entry.validation.status])}.{' '}
              {t('Restoration is revalidated against the current schema.')}
            </p>
            {compareRecipe(entry.definition, versionPreview.definition).map(change => (
              <details key={change.key} open>
                <summary>{t('Field: ' + change.key)}</summary>
                <div className="recipe-comparison">
                  <div>
                    <strong>{t('Current')}</strong>
                    <pre>{change.before}</pre>
                  </div>
                  <div>
                    <strong>{t('Selected version')}</strong>
                    <pre>{change.after}</pre>
                  </div>
                </div>
              </details>
            ))}
            <button
              className="secondary-button"
              disabled={busy || !comparison || versionPreview.version === entry.definition.version}
              onClick={() => onRestore(versionPreview)}
            >
              {t('Restore as new version')}
            </button>
            <div className="section-heading">
              <h4>
                {t('SQL for version')} {versionPreview.version}
              </h4>
              <button className="text-button" onClick={() => setVersionPreview(null)}>
                {t('Close')}
              </button>
            </div>
            <Suspense fallback={<textarea readOnly rows={10} value={versionPreview.definition.sqlTemplate} />}>
              <SqlEditor
                label={t('SQL for version {0}', versionPreview.version)}
                value={versionPreview.definition.sqlTemplate}
                readOnly
                height={260}
              />
            </Suspense>
          </div>
        )}
      </section>
    </div>
  );
}
