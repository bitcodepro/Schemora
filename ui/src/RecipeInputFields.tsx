import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import { defaultInput } from './recipeTypes';
import type { InputType, RecipeInput, SqlStrategy } from './recipeTypes';
const inputTypes: InputType[] = ['int', 'bigint', 'uniqueidentifier', 'nvarchar', 'varchar', 'date', 'datetime2', 'bit', 'decimal'];
const strategies: { value: SqlStrategy; label: string }[] = [
  { value: 'auto', label: 'Авто' },
  { value: 'values', label: 'VALUES' },
  { value: 'temp', label: '#temp + INSERT batches' },
  { value: 'openjson', label: 'OPENJSON · compatibility130+' },
  { value: 'list', label: 'IN list · up to 1000 values' },
];
const collations = [
  'Latin1_General_100_BIN2',
  'Latin1_General_100_CI_AS',
  'SQL_Latin1_General_CP1_CI_AS',
  'Cyrillic_General_100_BIN2',
  'Cyrillic_General_100_CI_AS',
  'Latin1_General_100_BIN2_UTF8',
  'Latin1_General_100_CI_AS_SC_UTF8',
];

export default function RecipeInputFields({
  inputs,
  valueInputs,
  onChange,
}: {
  inputs: RecipeInput[];
  valueInputs: ReadonlySet<string>;
  onChange: (inputs: RecipeInput[]) => void;
}) {
  useLocale();
  function change(index: number, updates: Partial<RecipeInput>) {
    onChange(inputs.map((input, position) => (position === index ? { ...input, ...updates } : input)));
  }
  function add() {
    let index = inputs.length + 1;
    while (inputs.some(input => input.name === `Ids${index}`)) index++;
    onChange([...inputs, defaultInput(`Ids${index}`)]);
  }
  return (
    <section className="recipe-input-definitions">
      <div className="section-heading">
        <h3>
          {t('Типизированные входы')} <span>{inputs.length}</span>
        </h3>
        <button className="text-button" onClick={add}>
          {t('+ Добавить вход')}
        </button>
      </div>
      {inputs.length === 0 && (
        <p className="muted-text">{t('У рецепта нет runtime-параметров. Добавьте вход и используйте его имя в SQL-шаблоне.')}</p>
      )}
      {inputs.map((input, index) => (
        <fieldset className="recipe-input-definition" key={index}>
          <legend>
            {t('Вход')} {index + 1}
          </legend>
          <div className="recipe-input-main-row">
            <label>
              <span className="field-label">{t('Имя')}</span>
              <input
                value={input.name}
                maxLength={64}
                spellCheck={false}
                aria-label={t('Имя входа {0}', index + 1)}
                onChange={event => change(index, { name: event.target.value })}
              />
            </label>
            <label>
              <span className="field-label">{t('SQL-тип')}</span>
              <select
                value={input.type}
                aria-label={t('Тип входа {0}', index + 1)}
                onChange={event => {
                  const typed = defaultInput(input.name, event.target.value as InputType);
                  onChange(
                    inputs.map((old, position) =>
                      position === index
                        ? {
                            ...typed,
                            deduplicate: old.deduplicate,
                            preserveOrder: old.preserveOrder,
                            strategy: old.strategy,
                            optional: old.optional,
                          }
                        : old,
                    ),
                  );
                }}
              >
                {inputTypes.map(type => (
                  <option key={type} value={type}>
                    {type}
                  </option>
                ))}
              </select>
            </label>
            {(input.type === 'nvarchar' || input.type === 'varchar') && (
              <label className="input-length-field">
                <span className="field-label">{t('Длина · -1=MAX')}</span>
                <input
                  type="number"
                  min="-1"
                  max={input.type === 'nvarchar' ? 4000 : 8000}
                  value={input.maxLength ?? ''}
                  aria-label={t('Максимальная длина входа {0}', index + 1)}
                  onChange={event => change(index, { maxLength: event.target.value ? Number(event.target.value) : undefined })}
                />
              </label>
            )}
            {input.type === 'decimal' && (
              <>
                <label className="input-length-field">
                  <span className="field-label">{t('Precision')}</span>
                  <input
                    type="number"
                    min="1"
                    max="38"
                    value={input.precision ?? ''}
                    aria-label={t('Precision of input {0}', index + 1)}
                    onChange={event => change(index, { precision: event.target.value ? Number(event.target.value) : undefined })}
                  />
                </label>
                <label className="input-length-field">
                  <span className="field-label">{t('Scale')}</span>
                  <input
                    type="number"
                    min="0"
                    max={input.precision ?? 38}
                    value={input.scale ?? ''}
                    aria-label={t('Scale of input {0}', index + 1)}
                    onChange={event => change(index, { scale: event.target.value ? Number(event.target.value) : undefined })}
                  />
                </label>
              </>
            )}
            {input.type === 'varchar' && (
              <label>
                <span className="field-label">{t('Кодировка')}</span>
                <select
                  value={input.encoding || 'ascii'}
                  aria-label={t('Кодировка входа {0}', index + 1)}
                  onChange={event => change(index, { encoding: event.target.value })}
                >
                  <option value="ascii">ASCII</option>
                  <option value="utf-8">UTF-8</option>
                  <option value="windows-1251">Windows-1251</option>
                  <option value="windows-1252">Windows-1252</option>
                </select>
              </label>
            )}
            <button
              className="text-button danger-text"
              onClick={() => onChange(inputs.filter((_, position) => position !== index))}
              aria-label={t('Удалить вход {0}', index + 1)}
            >
              {t('Удалить')}
            </button>
          </div>
          {(input.type === 'nvarchar' || input.type === 'varchar') && (
            <label className="input-collation">
              <span className="field-label">{t('Collation')}</span>
              <select
                value={input.collation || ''}
                onChange={event => change(index, { collation: event.target.value || undefined })}
                aria-label={t('Collation входа {0}', index + 1)}
              >
                <option value="">DATABASE_DEFAULT</option>
                {collations.map(collation => (
                  <option key={collation} value={collation}>
                    {collation}
                  </option>
                ))}
              </select>
              {input.type === 'varchar' && (
                <small>
                  {t('Для Unicode используйте nvarchar. Не-ASCII VARCHAR требует согласованных encoding и collation; UTF-8 — level150+.')}
                </small>
              )}
            </label>
          )}
          {(valueInputs.has(input.name) || input.optional) && (
            <div className="recipe-input-optional">
              <label>
                <input
                  type="checkbox"
                  checked={input.optional ?? false}
                  onChange={event => change(index, { optional: event.target.checked })}
                />
                {t('Optional value · empty means NULL')}
              </label>
              <small>
                {t('To skip a condition when the value is empty, write')} <code>{`({{value:${input.name}}} IS NULL OR …)`}</code>
              </small>
            </div>
          )}
          <div className="recipe-input-policies">
            <label>
              <input type="checkbox" checked={input.deduplicate} onChange={event => change(index, { deduplicate: event.target.checked })} />
              {t('Удалять дубликаты')}
            </label>
            <label>
              <input
                type="checkbox"
                checked={input.preserveOrder}
                onChange={event => change(index, { preserveOrder: event.target.checked })}
              />
              {t('Сохранять порядок')}
            </label>
            <label>
              {t('Стратегия')}{' '}
              <select value={input.strategy || 'auto'} onChange={event => change(index, { strategy: event.target.value as SqlStrategy })}>
                {strategies.map(strategy => (
                  <option key={strategy.value} value={strategy.value}>
                    {t(strategy.label)}
                  </option>
                ))}
              </select>
            </label>
          </div>
          <div className="recipe-placeholder-hint">
            <code>{`{{ids:${input.name}}}`}</code> {t('в IN (…)')} <span>·</span> <code>{`{{table:${input.name}}}`}</code>{' '}
            {t('как rowset с Ordinal / Value')} <span>·</span> <code>{`{{value:${input.name}}}`}</code> {t('as one value, e.g. a date')}
          </div>
        </fieldset>
      ))}
      <p className="recipe-policy-note">
        {t(
          'Политики дубликатов и порядка сохраняются в определении рецепта. Введённые ID обрабатываются отдельно и в рецепт не записываются.',
        )}
      </p>
    </section>
  );
}
