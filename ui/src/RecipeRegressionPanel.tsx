import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
import type { RecipeDefinition } from './recipeTypes';
type Fixture = {
  id: string;
  name: string;
  mode: string;
  strategy: string;
  artificialInputs: Record<string, string>;
  expected: unknown;
  expectedStrategy: string;
  sqlContains: string[];
  sqlExcludes: string[];
};
type Run = { id: string; fixtureId: string; recipeVersion: number; success: boolean; failures: string[]; testedAt: string };
export default function RecipeRegressionPanel({ recipe, disabled }: { recipe: RecipeDefinition; disabled: boolean }) {
  useLocale();
  const [fixtures, setFixtures] = useState<Fixture[]>([]),
    [runs, setRuns] = useState<Run[]>([]);
  const [name, setName] = useState(''),
    [values, setValues] = useState<Record<string, string>>({});
  const [strategy, setStrategy] = useState('auto'),
    [mode, setMode] = useState('standalone');
  const [contains, setContains] = useState(''),
    [excludes, setExcludes] = useState(''),
    [confirmed, setConfirmed] = useState(false);
  const [busy, setBusy] = useState(false),
    [error, setError] = useState('');
  const base = `/api/recipes/${encodeURIComponent(recipe.id)}`;
  useEffect(() => {
    const controller = new AbortController();
    setFixtures([]);
    setRuns([]);
    setValues({});
    setConfirmed(false);
    setError('');
    if (recipe.id)
      Promise.all([
        request<Fixture[]>(`${base}/regression-fixtures`, { signal: controller.signal }),
        request<Run[]>(`${base}/regression-runs`, { signal: controller.signal }),
      ])
        .then(([f, r]) => {
          setFixtures(f);
          setRuns(r);
        })
        .catch(e => {
          if (!controller.signal.aborted) setError(errorMessage(e, t('Could not load regression fixtures.')));
        });
    return () => controller.abort();
  }, [base, recipe.version, recipe.id]);
  async function action(work: () => Promise<unknown>) {
    setBusy(true);
    setError('');
    try {
      await work();
      const [f, r] = await Promise.all([request<Fixture[]>(`${base}/regression-fixtures`), request<Run[]>(`${base}/regression-runs`)]);
      setFixtures(f);
      setRuns(r);
    } catch (e) {
      setError(errorMessage(e, t('Regression operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  return (
    <section className="recipe-history product-controls recipe-regressions">
      <h3>{t('Saved regression fixtures')}</h3>
      <p>
        {t(
          'Only explicitly saved artificial test data is stored here. Runtime ID values are never copied into fixtures. Saved fixtures rerun after recipe and schema checks.',
        )}
      </p>
      {error && (
        <p role="alert" className="job-error">
          {error}
        </p>
      )}
      <fieldset disabled={disabled || busy || !recipe.id}>
        <legend>{t('Create an artificial baseline')}</legend>
        <label>
          {t('Fixture name')}
          <input value={name} onChange={e => setName(e.target.value)} maxLength={160} />
        </label>
        {recipe.inputs.map(input => (
          <label key={input.name}>
            {input.name}
            <textarea
              rows={3}
              value={values[input.name] || ''}
              onChange={e => setValues({ ...values, [input.name]: e.target.value })}
              placeholder={t('Artificial values only')}
            />
          </label>
        ))}
        <label>
          {t('Output mode')}
          <select value={mode} onChange={e => setMode(e.target.value)}>
            <option value="standalone">{t('Standalone')}</option>
            <option value="parameterized">{t('Parameterized')}</option>
          </select>
        </label>
        <label>
          {t('Strategy')}
          <select value={strategy} onChange={e => setStrategy(e.target.value)}>
            {['auto', 'values', 'temp', 'openjson', 'list'].map(s => (
              <option key={s}>{s}</option>
            ))}
          </select>
        </label>
        <label>
          {t('Required SQL fragments, one per line')}
          <textarea value={contains} onChange={e => setContains(e.target.value)} />
        </label>
        <label>
          {t('Forbidden SQL fragments, one per line')}
          <textarea value={excludes} onChange={e => setExcludes(e.target.value)} />
        </label>
        <label>
          <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
          {t('I confirm these are artificial test values and explicitly approve saving them.')}
        </label>
        <p>
          {t(
            'The baseline records expected counts, duplicate and ordering policies, value order, ordinals, selected SQL strategy and a SQL contract hash. Review its saved expectations below.',
          )}
        </p>
        <button
          className="primary-button"
          disabled={!confirmed || !name.trim()}
          onClick={() =>
            void action(() =>
              request(`${base}/regression-fixtures`, {
                method: 'POST',
                body: JSON.stringify({
                  name,
                  expectedVersion: recipe.version,
                  artificialDataConfirmed: confirmed,
                  artificialInputs: values,
                  mode,
                  strategy,
                  sqlContains: contains.split('\n').filter(Boolean),
                  sqlExcludes: excludes.split('\n').filter(Boolean),
                }),
              }),
            )
          }
        >
          {t('Save artificial baseline')}
        </button>
      </fieldset>
      <button
        className="secondary-button"
        disabled={disabled || busy || !fixtures.length}
        onClick={() => void action(() => request(`${base}/regression-runs`, { method: 'POST' }))}
      >
        {t('Run saved regressions')}
      </button>
      {fixtures.map(f => {
        const run = runs.find(r => r.fixtureId === f.id);
        return (
          <article key={f.id}>
            <h4>
              {f.name} · {run ? (run.success ? t('Passed') : t('Failed')) : t('Not run')}
            </h4>
            <p>
              {f.mode} · {f.expectedStrategy} {run && `· v${run.recipeVersion}`}
            </p>
            {run?.failures.map((failure, i) => (
              <p key={i} role="alert">
                {tMessage(failure)}
              </p>
            ))}
            <details>
              <summary>{t('Review saved artificial data and expectations')}</summary>
              <pre>{JSON.stringify(f, null, 2)}</pre>
            </details>
            <button
              className="text-button"
              disabled={disabled || busy}
              onClick={() =>
                void action(() =>
                  request(`${base}/regression-fixtures/${encodeURIComponent(f.id)}?expectedVersion=${recipe.version}`, {
                    method: 'DELETE',
                  }),
                )
              }
            >
              {t('Delete fixture')}
            </button>
          </article>
        );
      })}
    </section>
  );
}
