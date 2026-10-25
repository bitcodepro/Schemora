import { lazy, Suspense, useMemo, useRef } from 'react';
import type { ChangeEvent } from 'react';
import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import type { Catalog } from './types';
import { valueInputNames } from './recipeTypes';
import type { RecipeDefinition } from './recipeTypes';
import RecipeInputFields from './RecipeInputFields';
const SqlEditor = lazy(() => import('./SqlEditor'));

export default function RecipeEditorTab({
  draft,
  catalog,
  editorKey,
  tagText,
  onTagText,
  onEdit,
  onImportSql,
  inputShapeValid,
  busy,
  existing,
  imported,
  onSave,
  onClone,
  onExport,
}: {
  draft: RecipeDefinition;
  catalog: Catalog | null;
  editorKey: string;
  tagText: string;
  onTagText: (value: string) => void;
  onEdit: (updates: Partial<RecipeDefinition>) => void;
  onImportSql: (event: ChangeEvent<HTMLInputElement>) => void;
  inputShapeValid: boolean;
  busy: boolean;
  existing: boolean;
  imported: boolean;
  onSave: () => void;
  onClone: () => void;
  onExport: () => void;
}) {
  useLocale();
  const importSqlInput = useRef<HTMLInputElement | null>(null);
  const valueInputs = useMemo(() => valueInputNames(draft.sqlTemplate), [draft.sqlTemplate]);
  return (
    <div className="recipe-authoring">
      <div className="recipe-metadata-row">
        <label>
          <span className="field-label">{t('Name #2')}</span>
          <input
            value={draft.name}
            maxLength={160}
            onChange={event => onEdit({ name: event.target.value })}
            aria-label={t('Recipe name')}
          />
        </label>
        <label>
          <span className="field-label">{t('Tags · comma-separated')}</span>
          <input
            value={tagText}
            onChange={event => {
              onTagText(event.target.value);
              onEdit({
                tags: [
                  ...new Set(
                    event.target.value
                      .split(',')
                      .map(value => value.trim())
                      .filter(Boolean),
                  ),
                ],
              });
            }}
            aria-label={t('Recipe tags')}
          />
        </label>
      </div>
      <label>
        <span className="field-label">{t('Description')}</span>
        <textarea
          value={draft.description}
          rows={2}
          maxLength={8000}
          onChange={event => onEdit({ description: event.target.value })}
          aria-label={t('Recipe description')}
        />
      </label>
      <div className="recipe-db-options">
        <span className="field-label">{t('Databases available to the template')}</span>
        <div>
          {catalog?.databases.map(database => (
            <label key={database.id}>
              <input
                type="checkbox"
                checked={draft.databaseIds.includes(database.id)}
                onChange={event =>
                  onEdit({
                    databaseIds: event.target.checked
                      ? [...draft.databaseIds, database.id]
                      : draft.databaseIds.filter(id => id !== database.id),
                  })
                }
              />
              {database.name}
            </label>
          ))}
        </div>
      </div>
      <div className="recipe-sql-heading">
        <h3>{t('SQL template')}</h3>
        <button className="text-button" onClick={() => importSqlInput.current?.click()}>
          {t('Import .sql into editor')}
        </button>
        <input type="file" accept=".sql,text/plain" ref={importSqlInput} hidden onChange={onImportSql} />
      </div>
      <Suspense fallback={<div className="monaco-loading">{t('Loading the local SQL editor…')}</div>}>
        <SqlEditor
          key={editorKey}
          label={t('Recipe SQL template')}
          value={draft.sqlTemplate}
          onChange={value => onEdit({ sqlTemplate: value })}
        />
      </Suspense>
      <p className="template-help">
        {t('Allowed expressions:')} <code>IN (&#123;&#123;ids:Ids&#125;&#125;)</code> {t('and')}{' '}
        <code>JOIN &#123;&#123;table:Ids&#125;&#125; AS i ON i.Value = t.Id</code>
        {t('. Table names and conditions remain readable SQL.')}
      </p>
      <RecipeInputFields inputs={draft.inputs} valueInputs={valueInputs} onChange={inputs => onEdit({ inputs })} />
      <div className="recipe-compatibility">
        <label>
          <span className="field-label">{t('SQL Server compatibility level')}</span>
          <select value={draft.compatibilityLevel} onChange={event => onEdit({ compatibilityLevel: Number(event.target.value) })}>
            {[110, 120, 130, 140, 150, 160, 170].map(level => (
              <option key={level} value={level}>
                {level}
                {level === 130 ? t(' · minimum for OPENJSON') : ''}
              </option>
            ))}
          </select>
        </label>
        <p>{t('The level determines which SQL strategies are available. In Auto mode, the server chooses a suitable list format.')}</p>
      </div>
      {!inputShapeValid && (
        <p className="job-error">{t('Input names must be unique: Latin letters, digits, and _, starting with a letter.')}</p>
      )}
      <div className="recipe-save-actions">
        <button
          className="primary-button"
          disabled={busy || !draft.name.trim() || !draft.sqlTemplate.trim() || !draft.databaseIds.length || !inputShapeValid}
          onClick={onSave}
        >
          {busy ? t('Saving…') : existing ? t('Save new version') : imported ? t('Save import') : t('Save recipe')}
        </button>
        <button className="secondary-button" disabled={!existing || busy} onClick={onClone}>
          {t('Create copy')}
        </button>
        <button className="secondary-button" disabled={!existing || busy} onClick={onExport}>
          {t('Export JSON')}
        </button>
      </div>
    </div>
  );
}
