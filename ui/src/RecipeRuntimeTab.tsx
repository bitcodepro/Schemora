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
        [t('Всего'), statistics.total],
        [t('Валидных'), statistics.valid],
        [t('Ошибок'), statistics.invalid],
        [t('Уникальных'), statistics.unique],
        [t('Дубликатов'), statistics.duplicates],
        [t('В SQL'), statistics.outputCount],
      ].map(([label, value]) => (
        <div key={label} className={label === t('Ошибок') && Number(value) > 0 ? 'has-errors' : ''}>
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
          <p>{t('Сохраните определение, чтобы обрабатывать ID и формировать SQL с актуальными типами и политиками.')}</p>
        </div>
      )}
      {entry && (
        <div className="recipe-validation">
          <div className="section-heading">
            <h3>{t('Совместимость со схемой')}</h3>
            <span className={`recipe-state ${entry.validation.status.toLowerCase()}`}>{t(statusLabels[entry.validation.status])}</span>
            <button className="text-button" disabled={busy} onClick={onRefreshValidation}>
              {t('Проверить совместимость')}
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
              <span>{t('Я проверил диагностику и вручную подтверждаю смысл SQL этого рецепта перед генерацией.')}</span>
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
                aria-label={t('Значение: {0}', input.name)}
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
                aria-label={t('Список ID: {0}', input.name)}
                value={runtimeValue(input.name)}
                onChange={event => changeRuntimeInput(input.name, event.target.value)}
                rows={6}
                spellCheck={false}
                disabled={dirty}
                placeholder={t('Вставьте ID из строк, CSV, TSV, SQL-списка или через пробел…')}
              />
              <div className="runtime-policy-summary">
                <span>{input.deduplicate ? t('Дубликаты удаляются по явной политике') : t('Дубликаты сохраняются')}</span>
                <span>{input.preserveOrder ? t('Порядок сохраняется') : t('Сохранение порядка не требуется')}</span>
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
                        <th>{t('Элемент')}</th>
                        <th>{t('Строка / колонка')}</th>
                        <th>{t('Ошибка')}</th>
                        <th>{t('Значение')}</th>
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
                      {t('Показаны первые150 из')} {parsed[input.name].errors.length} {t('ошибок.')}
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
            ? t('Проверка…')
            : draft.inputs.every(input => valueInputs.has(input.name))
              ? t('Проверить значения')
              : draft.inputs.some(input => valueInputs.has(input.name))
                ? t('Проверить ID и значения')
                : t('Проверить все ID')}
        </button>
        <span>
          {invalidCount > 0
            ? t('Ошибок: {0}. Генерация заблокирована до исправления.', invalidCount)
            : t('Проверка выполняется локально; входные значения не сохраняются.')}
        </span>
      </div>
      <div className="runtime-output-controls">
        <label>
          <span className="field-label">{t('Режим вывода')}</span>
          <select value={mode} onChange={event => setMode(event.target.value as 'standalone' | 'parameterized')}>
            <option value="standalone">{t('Самодостаточный SQL-скрипт')}</option>
            <option value="parameterized">{t('Параметры + пример ADO.NET')}</option>
          </select>
        </label>
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
        <button className="primary-button" disabled={!canRender || parseBusy || renderBusy || busy} onClick={() => void renderRecipe()}>
          {renderBusy ? t('Генерация…') : t('Сформировать SQL')}
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
                {t('Скопировать')}
              </button>
              <button
                className="secondary-button"
                title={t('Файл будет содержать введённые значения')}
                onClick={() =>
                  saveDownload(output, filename(draft.name) + (outputView !== 'sql' ? '.cs' : '.sql'), 'text/plain;charset=utf-8')
                }
              >
                {t('Скачать файл')}
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
          <Suspense fallback={<textarea readOnly value={output} rows={14} aria-label={t('Сгенерированный SQL')} />}>
            <SqlEditor
              key={`output:${draft.id}`}
              label={t('Сгенерированный SQL или ADO.NET example')}
              value={output}
              language={outputView !== 'sql' ? 'csharp' : 'sql'}
              readOnly
              height={360}
            />
          </Suspense>
          {rendered.parameters.length > 0 && (
            <p className="parameter-count">
              {t('Параметров:')} {rendered.parameters.length}.{' '}
              {rendered.parameters
                .slice(0, 12)
                .map(parameter => `${parameter.name}: ${parameter.sqlType}`)
                .join(' · ')}
              {rendered.parameters.length > 12 && ' …'}
            </p>
          )}
          <p className="copy-state" role="status">
            {tMessage(copyState) ||
              t('Результат хранится в памяти вкладки. Копирование и скачивание выполняются только по вашему действию.')}
          </p>
        </section>
      )}
    </div>
  );
}
