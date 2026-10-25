import { lazy, Suspense, useMemo, useState } from 'react';
import { errorMessage, request } from './api';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { filename, saveDownload } from './recipeFormat';
import { valueFormatHint, valueInputNames } from './recipeTypes';
import type { InputType, RecipeEntry, SqlStrategy } from './recipeTypes';
const SqlEditor = lazy(() => import('./SqlEditor'));

type RecipeResult = {
  recipeId: string;
  name: string;
  version: number;
  skipped: boolean;
  code?: string | null;
  message?: string | null;
  warnings: string[];
};
type CollectionSql = { sql: string | null; recipes: RecipeResult[] };

/** One standalone script for every recipe of a saved collection; inputs with the same name are entered once. */
export default function CollectionSqlPanel({
  workspaceId,
  collectionId,
  collectionName,
  recipes,
}: {
  workspaceId: string;
  collectionId: string;
  collectionName: string;
  recipes: RecipeEntry[];
}) {
  useLocale();
  const inputs = useMemo(() => {
    const byName = new Map<string, { name: string; types: Set<InputType>; single: boolean; optional: boolean; users: string[] }>();
    for (const entry of recipes) {
      const values = valueInputNames(entry.definition.sqlTemplate);
      for (const input of entry.definition.inputs) {
        const known = byName.get(input.name) || { name: input.name, types: new Set(), single: true, optional: true, users: [] };
        known.types.add(input.type);
        known.single &&= values.has(input.name);
        known.optional &&= Boolean(input.optional) && values.has(input.name);
        known.users.push(entry.definition.name);
        byName.set(input.name, known);
      }
    }
    return [...byName.values()].sort((a, b) => a.name.localeCompare(b.name));
  }, [recipes]);
  const [text, setText] = useState<Record<string, string>>({});
  const [strategy, setStrategy] = useState<SqlStrategy>('auto');
  const [acknowledge, setAcknowledge] = useState(false);
  const [result, setResult] = useState<CollectionSql | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [copied, setCopied] = useState('');
  function change(name: string, value: string) {
    setText(previous => ({ ...previous, [name]: value }));
    setResult(null);
  }
  async function generate() {
    setBusy(true);
    setError('');
    setResult(null);
    setCopied('');
    try {
      setResult(
        await request<CollectionSql>(
          `/api/workspaces/${encodeURIComponent(workspaceId)}/recipe-collections/${encodeURIComponent(collectionId)}/render`,
          {
            method: 'POST',
            body: JSON.stringify({
              inputText: Object.fromEntries(inputs.filter(input => text[input.name]?.trim()).map(input => [input.name, text[input.name]])),
              strategy,
              acknowledgeReview: acknowledge,
            }),
          },
        ),
      );
    } catch (cause) {
      setError(errorMessage(cause, t('SQL не сформирован.')));
    } finally {
      setBusy(false);
    }
  }
  async function copy() {
    if (!result?.sql) return;
    try {
      await navigator.clipboard.writeText(result.sql);
      setCopied(t('Скопировано в буфер обмена.'));
    } catch {
      setCopied(t('Копирование недоступно. Выделите текст и скопируйте вручную.'));
    }
  }
  return (
    <section className="collection-sql" aria-label={t('SQL for the whole collection')}>
      <h3>{t('SQL for the whole collection')}</h3>
      <p className="muted-text">
        {t('Each input is entered once and goes to every recipe that uses its name. Every recipe becomes its own batch (GO).')}
      </p>
      {inputs.map(input => (
        <label key={input.name} className="collection-sql-input">
          <span className="field-label">
            {input.name} · {[...input.types].join(' / ')} · {t('used by {0}', input.users.length)}
          </span>
          {input.single ? (
            <input
              aria-label={t('Collection input {0}', input.name)}
              value={text[input.name] || ''}
              spellCheck={false}
              placeholder={
                input.optional ? t('{0} · empty means NULL', valueFormatHint([...input.types][0])) : valueFormatHint([...input.types][0])
              }
              onChange={event => change(input.name, event.target.value)}
            />
          ) : (
            <textarea
              aria-label={t('Collection input {0}', input.name)}
              value={text[input.name] || ''}
              rows={3}
              spellCheck={false}
              placeholder={t('Вставьте ID из строк, CSV, TSV, SQL-списка или через пробел…')}
              onChange={event => change(input.name, event.target.value)}
            />
          )}
        </label>
      ))}
      <div className="collection-sql-actions">
        <label>
          <span className="field-label">{t('SQL-стратегия')}</span>
          <select value={strategy} onChange={event => setStrategy(event.target.value as SqlStrategy)}>
            <option value="auto">{t('Auto · по размеру и типу')}</option>
            <option value="values">VALUES</option>
            <option value="temp">{t('#temp + batch INSERT')}</option>
            <option value="openjson">{t('OPENJSON · compatibility130+')}</option>
            <option value="list">{t('IN list · up to 1000 values')}</option>
          </select>
        </label>
        <label className="collection-sql-review">
          <input type="checkbox" checked={acknowledge} onChange={event => setAcknowledge(event.target.checked)} />
          {t('I reviewed recipes that need a manual check')}
        </label>
        <button className="primary-button" disabled={busy || !recipes.length} onClick={() => void generate()}>
          {busy ? t('Генерация…') : t('Generate collection SQL')}
        </button>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      {result && (
        <>
          <ul className="collection-sql-results">
            {result.recipes.map(item => (
              <li key={item.recipeId} className={item.skipped ? 'skipped' : item.code ? 'failed' : 'rendered'}>
                <strong>
                  {item.name} · v{item.version}
                </strong>{' '}
                {item.skipped ? (
                  t('Archived, not included')
                ) : item.code ? (
                  <>
                    <code>{item.code}</code> {tMessage(item.message)}
                  </>
                ) : (
                  t('Rendered') + (item.warnings.length ? ' · ' + t('warnings: {0}', item.warnings.length) : '')
                )}
              </li>
            ))}
          </ul>
          {result.sql ? (
            <>
              <div className="collection-sql-actions">
                <button className="secondary-button" onClick={() => void copy()}>
                  {t('Скопировать')}
                </button>
                <button
                  className="secondary-button"
                  title={t('Файл будет содержать введённые значения')}
                  onClick={() => saveDownload(result.sql!, filename(collectionName) + '.sql', 'text/plain;charset=utf-8')}
                >
                  {t('Скачать файл')}
                </button>
                <span role="status">{copied}</span>
              </div>
              <Suspense fallback={<textarea readOnly value={result.sql} rows={14} aria-label={t('Сгенерированный SQL')} />}>
                <SqlEditor label={t('Сгенерированный SQL')} value={result.sql} readOnly height={360} />
              </Suspense>
            </>
          ) : (
            <p className="job-error">{t('No script was produced: fix the recipes marked above and generate again.')}</p>
          )}
        </>
      )}
    </section>
  );
}
