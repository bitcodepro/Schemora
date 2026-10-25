import { lazy, Suspense, useMemo } from 'react';
import { localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { filename, saveDownload, statusLabels } from './recipeFormat';
import { datePresets, isoDate, periodPresets, valueFormatHint, valueInputNames } from './recipeTypes';
import type { InputStatistics, RecipeDefinition, RecipeEntry, SqlStrategy } from './recipeTypes';
import type { RecipeRuntime } from './useRecipeRuntime';
import RecipeValueSets from './RecipeValueSets';
const SqlEditor = lazy(() => import('./SqlEditor'));

function Statistics({ statistics }: { statistics: InputStatistics }) {
  useLocale();
  return (
    <div className="input-statistics">
      {[
        [t('Total'), statistics.total],
        [t('Valid'), statistics.valid],
        [t('Errors'), statistics.invalid],
        [t('Unique'), statistics.unique],
        [t('Duplicates'), statistics.duplicates],
        [t('In SQL'), statistics.outputCount],
      ].map(([label, value]) => (
        <div key={label} className={label === t('Errors') && Number(value) > 0 ? 'has-errors' : ''}>
          <strong>{Number(value).toLocaleString(localeTag())}</strong>
          <span>{label}</span>
        </div>
      ))}
    </div>
  );
}

export default function RecipeRuntimeTab({
  runtime,
  draft,
  entry,
  dirty,
  busy,
  onRefreshValidation,
}: {
  runtime: RecipeRuntime;
  draft: RecipeDefinition;
  entry: RecipeEntry | undefined;
  dirty: boolean;
  busy: boolean;
  onRefreshValidation: () => void;
}) {
  useLocale();
  const valueInputs = useMemo(() => valueInputNames(draft.sqlTemplate), [draft.sqlTemplate]);
  const {
    parsed,
    parseBusy,
    renderBusy,
    runtimeError,
    rendered,
    setRendered,
    mode,
    setMode,
    strategy,
    setStrategy,
    acknowledgeReview,
    setAcknowledgeReview,
    outputView,
    setOutputView,
    copyState,
    runtimeValue,
    changeRuntimeInput,
    importIds,
    parseInputs,
    invalidCount,
    canRender,
    renderRecipe,
    output,
    copyOutput,
  } = runtime;
  return (
    <div className="recipe-runtime">
      {dirty && (
        <div className="alert warning">
          <p>{t('Save the definition to process IDs and generate SQL with the current types and policies.')}</p>
        </div>
      )}
      {entry && (
        <div className="recipe-validation">
          <div className="section-heading">
            <h3>{t('Schema compatibility')}</h3>
            <span className={`recipe-state ${entry.validation.status.toLowerCase()}`}>{t(statusLabels[entry.validation.status])}</span>
            <button className="text-button" disabled={busy} onClick={onRefreshValidation}>
              {t('Check compatibility')}
            </button>
          </div>
          {entry.validation.diagnostics.map((diagnostic, index) => (
            <div key={`${diagnostic.code}-${index}`} className={`validation-diagnostic ${diagnostic.severity}`}>
              <code>{diagnostic.code}</code>
              <p>{tMessage(diagnostic.message)}</p>
            </div>
          ))}
          {entry.validation.status === 'NeedsReview' && (
            <label className="review-acknowledgment">
              <input
                type="checkbox"
                checked={acknowledgeReview}
                onChange={event => {
                  setAcknowledgeReview(event.target.checked);
                  setRendered(null);
                }}
              />
              <span>{t("I have reviewed the diagnostics and manually confirm the meaning of this recipe's SQL before generation.")}</span>
            </label>
          )}
        </div>
      )}
      {entry && !dirty && (
        <RecipeValueSets
          recipeId={entry.definition.id}
          disabled={parseBusy || renderBusy}
          currentValues={() => Object.fromEntries(draft.inputs.map(input => [input.name, runtimeValue(input.name)]))}
          onLoad={runtime.replaceRuntimeInputs}
        />
      )}
      {(() => {
        // Two single-value date inputs (e.g. From/To) get one-click whole periods.
        const pair = draft.inputs.filter(input => valueInputs.has(input.name) && (input.type === 'date' || input.type === 'datetime2'));
        if (pair.length < 2) return null;
        const [from, to] = pair;
        return (
          <div className="runtime-period-presets" role="group" aria-label={t('Period for {0} and {1}', from.name, to.name)}>
            <span>{t('Period for {0} and {1}', from.name, to.name)}</span>
            {periodPresets().map(period => (
              <button
                key={period.label}
                type="button"
                className="text-button"
                disabled={dirty}
                title={`${isoDate(period.from, from.type)} … ${isoDate(period.to, to.type)}`}
                onClick={() => {
                  changeRuntimeInput(from.name, isoDate(period.from, from.type));
                  changeRuntimeInput(to.name, isoDate(period.to, to.type));
                }}
              >
                {t(period.label)}
              </button>
            ))}
            <small>{t('The end date is exclusive.')}</small>
          </div>
        );
      })()}
      {draft.inputs.map(input => (
        <section className={'runtime-input-card' + (valueInputs.has(input.name) ? ' runtime-value-card' : '')} key={input.name}>
          <div className="section-heading">
            <h3>
              {input.name}{' '}
              <span>
                {input.type}
                {input.maxLength ? `(${input.maxLength === -1 ? 'max' : input.maxLength})` : ''}
              </span>
              {input.optional && valueInputs.has(input.name) && <em className="runtime-optional-tag">{t('optional')}</em>}
            </h3>
            {!valueInputs.has(input.name) && (
              <label className="file-button">
                CSV / TXT
                <input
                  type="file"
                  accept=".csv,.tsv,.txt,text/csv,text/plain"
                  hidden
                  disabled={dirty || parseBusy || renderBusy}
                  onChange={event => void importIds(event, input.name)}
                />
              </label>
            )}
          </div>
          {valueInputs.has(input.name) ? (
            <div className="runtime-value-row">
              <input
                type="text"
                className="runtime-value-input"
                aria-label={t('Value: {0}', input.name)}
                value={runtimeValue(input.name)}
                onChange={event => changeRuntimeInput(input.name, event.target.value)}
                spellCheck={false}
                disabled={dirty}
                placeholder={input.optional ? t('{0} · empty means NULL', valueFormatHint(input.type)) : valueFormatHint(input.type)}
              />
              {datePresets(input.type).length > 0 && (
                <div className="runtime-value-presets" role="group" aria-label={t('Quick dates for {0}', input.name)}>
                  {datePresets(input.type).map(preset => (
                    <button
                      key={preset.label}
                      type="button"
                      className="text-button"
                      disabled={dirty}
                      onClick={() => changeRuntimeInput(input.name, preset.value)}
                      title={preset.value}
                    >
                      {t(preset.label)}
                    </button>
                  ))}
                </div>
              )}
            </div>
          ) : (
            <>
              <textarea
                aria-label={t('ID list: {0}', input.name)}
                value={runtimeValue(input.name)}
                onChange={event => changeRuntimeInput(input.name, event.target.value)}
                rows={6}
                spellCheck={false}
                disabled={dirty}
                placeholder={t('Paste IDs from lines, CSV, TSV, a SQL list, or space-separated text…')}
              />
              <div className="runtime-policy-summary">
                <span>{input.deduplicate ? t('Duplicates are removed by an explicit policy') : t('Duplicates are kept')}</span>
                <span>{input.preserveOrder ? t('Order is preserved') : t('Preserving order is not required')}</span>
              </div>
            </>
          )}
          {parsed[input.name] && (
            <>
              {!valueInputs.has(input.name) && (
                <Statistics statistics={rendered?.statistics[input.name] || parsed[input.name].statistics} />
              )}
              {parsed[input.name].errors.length > 0 && (
                <div className="input-error-table">
                  <table>
                    <thead>
                      <tr>
                        <th>{t('Item')}</th>
                        <th>{t('Line / column')}</th>
                        <th>{t('Error')}</th>
                        <th>{t('Value')}</th>
                      </tr>
                    </thead>
                    <tbody>
                      {parsed[input.name].errors.slice(0, 150).map((issue, index) => (
                        <tr key={index}>
                          <td>{issue.index}</td>
                          <td>
                            {issue.line}:{issue.column}
                          </td>
                          <td>
                            <code>{issue.code}</code>
                            <p>{tMessage(issue.message)}</p>
                          </td>
                          <td>
                            <code>{issue.rawValue}</code>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                  {parsed[input.name].errors.length > 150 && (
                    <p>
                      {t('Showing the first 150 of')} {parsed[input.name].errors.length} {t('errors.')}
                    </p>
                  )}
                </div>
              )}
            </>
          )}
        </section>
      ))}
      <div className="runtime-parse-actions">
        <button
          className="secondary-button"
          disabled={dirty || parseBusy || renderBusy || !draft.inputs.length}
          onClick={() => void parseInputs()}
        >
          {parseBusy
            ? t('Checking…')
            : draft.inputs.every(input => valueInputs.has(input.name))
              ? t('Validate values')
              : draft.inputs.some(input => valueInputs.has(input.name))
                ? t('Validate IDs and values')
                : t('Validate all IDs')}
        </button>
        <span>
          {invalidCount > 0
            ? t('Errors: {0}. Generation is blocked until they are fixed.', invalidCount)
            : t('Validation runs locally; input values are not saved.')}
        </span>
      </div>
      <div className="runtime-output-controls">
        <label>
          <span className="field-label">{t('Output mode')}</span>
          <select value={mode} onChange={event => setMode(event.target.value as 'standalone' | 'parameterized')}>
            <option value="standalone">{t('Standalone SQL script')}</option>
            <option value="parameterized">{t('Parameters + ADO.NET example')}</option>
          </select>
        </label>
        <label>
          <span className="field-label">{t('SQL strategy')}</span>
          <select value={strategy} onChange={event => setStrategy(event.target.value as SqlStrategy)}>
            <option value="auto">{t('Auto · by size and type')}</option>
            <option value="values">VALUES</option>
            <option value="temp">{t('#temp + batch INSERT')}</option>
            <option value="openjson">{t('OPENJSON · compatibility130+')}</option>
            <option value="list">{t('IN list · up to 1000 values')}</option>
          </select>
        </label>
        <button className="primary-button" disabled={!canRender || parseBusy || renderBusy || busy} onClick={() => void renderRecipe()}>
          {renderBusy ? t('Generating…') : t('Generate SQL')}
        </button>
      </div>
      {runtimeError && (
        <div className="alert error" role="alert">
          <p>{tMessage(runtimeError)}</p>
        </div>
      )}
      {rendered && (
        <section className="recipe-output">
          <div className="section-heading">
            <h3>
              {rendered.mode === 'standalone' ? t('Standalone SQL') : t('Parameterized example')}{' '}
              <span>
                {rendered.strategy}
                {rendered.recipeVersion ? ` · v${rendered.recipeVersion}` : ''}
              </span>
            </h3>
            <div>
              <button className="secondary-button" onClick={() => void copyOutput()}>
                {t('Copy')}
              </button>
              <button
                className="secondary-button"
                title={t('The file will contain the entered values')}
                onClick={() =>
                  saveDownload(output, filename(draft.name) + (outputView !== 'sql' ? '.cs' : '.sql'), 'text/plain;charset=utf-8')
                }
              >
                {t('Download file')}
              </button>
            </div>
          </div>
          {rendered.parameterizedExample && (
            <div className="output-view-tabs">
              <button className={outputView === 'sql' ? 'selected' : ''} onClick={() => setOutputView('sql')}>
                SQL
              </button>
              <button className={outputView === 'example' ? 'selected' : ''} onClick={() => setOutputView('example')}>
                ADO.NET C#
              </button>
              {rendered.dapperExample && (
                <button className={outputView === 'dapper' ? 'selected' : ''} onClick={() => setOutputView('dapper')}>
                  Dapper C#
                </button>
              )}
            </div>
          )}
          {rendered.warnings.map((warning, index) => (
            <p className="join-warning" key={index}>
              {tMessage(warning)}
            </p>
          ))}
          <Suspense fallback={<textarea readOnly value={output} rows={14} aria-label={t('Generated SQL')} />}>
            <SqlEditor
              key={`output:${draft.id}`}
              label={t('Generated SQL or ADO.NET example')}
              value={output}
              language={outputView !== 'sql' ? 'csharp' : 'sql'}
              readOnly
              height={360}
            />
          </Suspense>
          {rendered.parameters.length > 0 && (
            <p className="parameter-count">
              {t('Parameters:')} {rendered.parameters.length}.{' '}
              {rendered.parameters
                .slice(0, 12)
                .map(parameter => `${parameter.name}: ${parameter.sqlType}`)
                .join(' · ')}
              {rendered.parameters.length > 12 && ' …'}
            </p>
          )}
          <p className="copy-state" role="status">
            {tMessage(copyState) || t("The result is kept in this tab's memory. Copying and downloading happen only when you choose.")}
          </p>
        </section>
      )}
    </div>
  );
}
