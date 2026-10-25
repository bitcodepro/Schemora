import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import { defaultInput } from './recipeTypes';
import type { InputType, RecipeInput, SqlStrategy } from './recipeTypes';
const inputTypes: InputType[] = ['int', 'bigint', 'uniqueidentifier', 'nvarchar', 'varchar', 'date', 'datetime2', 'bit', 'decimal'];
const strategies: { value: SqlStrategy; label: string }[] = [
  { value: 'auto', label: 'Auto' },
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
          {t('Typed inputs')} <span>{inputs.length}</span>
        </h3>
        <button className="text-button" onClick={add}>
          {t('+ Add input')}
        </button>
      </div>
      {inputs.length === 0 && (
        <p className="muted-text">{t('This recipe has no runtime parameters. Add an input and use its name in the SQL template.')}</p>
      )}
      {inputs.map((input, index) => (
        <fieldset className="recipe-input-definition" key={index}>
          <legend>
            {t('Input')} {index + 1}
          </legend>
          <div className="recipe-input-main-row">
            <label>
              <span className="field-label">{t('Name')}</span>
              <input
                value={input.name}
                maxLength={64}
                spellCheck={false}
                aria-label={t('Input {0} name', index + 1)}
                onChange={event => change(index, { name: event.target.value })}
              />
            </label>
            <label>
              <span className="field-label">{t('SQL type')}</span>
              <select
                value={input.type}
                aria-label={t('Input {0} type', index + 1)}
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
                <span className="field-label">{t('Length · -1=MAX')}</span>
                <input
                  type="number"
                  min="-1"
                  max={input.type === 'nvarchar' ? 4000 : 8000}
                  value={input.maxLength ?? ''}
                  aria-label={t('Input {0} maximum length', index + 1)}
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
                <span className="field-label">{t('Encoding')}</span>
                <select
                  value={input.encoding || 'ascii'}
                  aria-label={t('Input {0} encoding', index + 1)}
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
              aria-label={t('Remove input {0}', index + 1)}
            >
              {t('Remove')}
            </button>
          </div>
          {(input.type === 'nvarchar' || input.type === 'varchar') && (
            <label className="input-collation">
              <span className="field-label">{t('Collation')}</span>
              <select
                value={input.collation || ''}
                onChange={event => change(index, { collation: event.target.value || undefined })}
                aria-label={t('Input {0} collation', index + 1)}
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
                  {t('Use nvarchar for Unicode. Non-ASCII VARCHAR requires matching encoding and collation; UTF-8 requires level 150+.')}
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
              {t('Remove duplicates')}
            </label>
            <label>
              <input
                type="checkbox"
                checked={input.preserveOrder}
                onChange={event => change(index, { preserveOrder: event.target.checked })}
              />
              {t('Preserve order')}
            </label>
            <label>
              {t('Strategy')}{' '}
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
            <code>{`{{ids:${input.name}}}`}</code> {t('in IN (…) ')} <span>·</span> <code>{`{{table:${input.name}}}`}</code>{' '}
            {t('as a rowset with Ordinal / Value')} <span>·</span> <code>{`{{value:${input.name}}}`}</code> {t('as one value, e.g. a date')}
          </div>
        </fieldset>
      ))}
      <p className="recipe-policy-note">
        {t(
          'Duplicate and order policies are saved in the recipe definition. Entered IDs are processed separately and are not stored in the recipe.',
        )}
      </p>
    </section>
  );
}
