import { draftKey, encodeDraft, decodeDraft } from './recipeDrafts';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useMemo, useRef, useState } from 'react';
import type { ChangeEvent } from 'react';
import { errorMessage, request } from './api';
import { useWorkspaces } from './workspaceContext';
import type { Catalog } from './types';
import { decodeRecipeImport, emptyRecipe, recipeForSave } from './recipeTypes';
import type { RecipeDefinition, RecipeEntry, RecipeSeed, RecipeVersion } from './recipeTypes';
import RecipeRegressionPanel from './RecipeRegressionPanel';
import RecipeCatalogList from './RecipeCatalogList';
import RecipeEditorTab from './RecipeEditorTab';
import RecipeRuntimeTab from './RecipeRuntimeTab';
import RecipeHistoryTab from './RecipeHistoryTab';
import { useRecipeRuntime } from './useRecipeRuntime';
import { useRecipePicks } from './useRecipePicks';
import { updateRouteParams } from './route';
import { filename, formatDate, saveDownload } from './recipeFormat';
import TableRecipeWizard from './TableRecipeWizard';
import ChainRecipeWizard from './ChainRecipeWizard';
import './recipes.css';
export default function RecipesPanel({
  seed,
  onSeedConsumed,
  initialWorkspaceId,
  initialRecipeId,
  openNonce,
  initialTab = 'editor',
}: {
  seed?: RecipeSeed | null;
  onSeedConsumed?: () => void;
  initialWorkspaceId?: string;
  initialRecipeId?: string;
  openNonce?: number;
  initialTab?: 'editor' | 'runtime' | 'history' | 'regression';
}) {
  useLocale();
  const { workspaceId, select: setWorkspaceId, setGuard } = useWorkspaces();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [entries, setEntries] = useState<RecipeEntry[]>([]);
  const [selectedId, setSelectedId] = useState('');
  const [draft, setDraft] = useState<RecipeDefinition | null>(null);
  const [recoverable, setRecoverable] = useState<ReturnType<typeof decodeDraft> | null>(null);
  const [draftStorageError, setDraftStorageError] = useState(false);
  const [importedDraft, setImportedDraft] = useState(false);
  const [tagText, setTagText] = useState('');
  const [loading, setLoading] = useState(false);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [tab, setTab] = useState<'editor' | 'runtime' | 'history' | 'regression'>('editor');
  const workspaceRef = useRef(workspaceId);
  workspaceRef.current = workspaceId;
  const selectedRef = useRef(selectedId);
  selectedRef.current = selectedId;
  // A save, copy or rollback answers after the user may have moved on; its result only applies to the place it started.
  function scopeOf() {
    const workspace = workspaceRef.current;
    const recipe = selectedRef.current;
    return () => workspaceRef.current === workspace && selectedRef.current === recipe;
  }
  const pendingSeed = useRef(seed);
  const appliedOpen = useRef(0);
  const draftWorkspace = useRef('');
  const draftWrites = useRef<Promise<unknown>>(Promise.resolve());
  const importJsonInput = useRef<HTMLInputElement | null>(null);

  const selectedEntry = entries.find(entry => entry.definition.id === selectedId);
  const dirty = Boolean(
    draft && (!selectedEntry || JSON.stringify(recipeForSave(draft)) !== JSON.stringify(recipeForSave(selectedEntry.definition))),
  );
  const runtime = useRecipeRuntime(draft, selectedEntry, dirty);
  const recipePicks = useRecipePicks(workspaceId);
  useEffect(() => {
    if (!workspaceId || loading) return;
    try {
      const text = localStorage.getItem(draftKey(workspaceId));
      setRecoverable(text ? decodeDraft(text) : null);
    } catch {
      setDraftStorageError(true);
    }
    const c = new AbortController();
    void request<{ draft: { definition: RecipeDefinition; imported: boolean; savedAt: string } | null }>(
      '/api/workspaces/' + workspaceId + '/authoring-draft',
      { signal: c.signal },
    )
      .then(result => {
        if (!result.draft) return;
        setRecoverable(previous => (!previous || result.draft!.savedAt > previous.savedAt ? result.draft : previous));
      })
      .catch(() => {
        if (!c.signal.aborted) setDraftStorageError(true);
      });
    return () => c.abort();
  }, [workspaceId, loading]);
  useEffect(() => {
    if (!workspaceId || draftWorkspace.current !== workspaceId || !draft || !dirty || loading || busy) return;
    try {
      localStorage.setItem(draftKey(workspaceId), encodeDraft(draft, importedDraft));
      setDraftStorageError(false);
    } catch {
      setDraftStorageError(true);
    }
    const target = workspaceId;
    const payload = { definition: recipeForSave(draft), imported: importedDraft, savedAt: new Date().toISOString() };
    const timer = setTimeout(() => {
      draftWrites.current = draftWrites.current
        .catch(() => undefined)
        .then(() => request('/api/workspaces/' + target + '/authoring-draft', { method: 'PUT', body: JSON.stringify(payload) }))
        .catch(() => {
          setDraftStorageError(true);
        });
    }, 400);
    return () => clearTimeout(timer);
  }, [workspaceId, draft, dirty, importedDraft, loading, busy]);
  useEffect(() => {
    const guard = (event: BeforeUnloadEvent) => {
      if (dirty) {
        event.preventDefault();
        event.returnValue = '';
      }
    };
    const leave = (event: Event) => {
      if (dirty && !window.confirm(t('Leave the editor? Your authoring draft is retained locally.'))) event.preventDefault();
    };
    window.addEventListener('beforeunload', guard);
    window.addEventListener('schemora-leave-editor', leave);
    return () => {
      window.removeEventListener('beforeunload', guard);
      window.removeEventListener('schemora-leave-editor', leave);
    };
  }, [dirty]);
  async function discardRecovery() {
    try {
      localStorage.removeItem(draftKey(workspaceId));
    } catch {
      setDraftStorageError(true);
    }
    setRecoverable(null);
    const target = workspaceId;
    draftWrites.current = draftWrites.current
      .catch(() => undefined)
      .then(() => request('/api/workspaces/' + target + '/authoring-draft', { method: 'DELETE' }));
    try {
      await draftWrites.current;
    } catch {
      setDraftStorageError(true);
    }
  }
  function recoverDraft() {
    if (!recoverable) return;
    draftWorkspace.current = workspaceId;
    runtime.invalidateRuntime(true);
    setSelectedId(entries.some(e => e.definition.id === recoverable.definition.id) ? recoverable.definition.id : '');
    setDraft(recoverable.definition);
    setImportedDraft(recoverable.imported);
    setTagText(recoverable.definition.tags.join(', '));
    setTab('editor');
    setRecoverable(null);
  }
  function leaveDraft() {
    return !dirty || window.confirm(t('Leave the editor? Your authoring draft is retained locally.'));
  }
  // Saved recipes open on ID input: running them is the everyday action, editing the definition is not.
  function openEntry(entry: RecipeEntry, nextTab: 'editor' | 'runtime' = 'runtime') {
    draftWorkspace.current = workspaceRef.current;
    runtime.invalidateRuntime(true);
    setImportedDraft(false);
    setSelectedId(entry.definition.id);
    setDraft(structuredClone(entry.definition));
    setTagText(entry.definition.tags.join(', '));
    setError('');
    setNotice('');
    setTab(nextTab);
  }
  function openSeed(value: RecipeSeed) {
    draftWorkspace.current = workspaceRef.current;
    runtime.invalidateRuntime(true);
    setImportedDraft(false);
    setSelectedId('');
    setDraft({
      ...emptyRecipe(value.databaseIds),
      ...(value.definition ? recipeForSave(value.definition) : {}),
      id: '',
      version: 0,
      name: value.name,
      sqlTemplate: value.sqlTemplate,
      inputs: value.inputs || [],
    });
    setTagText('');
    setTab('editor');
    setNotice(t('Черновик создан из выбранной схемы. Проверьте SQL и сохраните рецепт.'));
    setError('');
  }
  function newRecipe() {
    draftWorkspace.current = workspaceRef.current;
    runtime.invalidateRuntime(true);
    setImportedDraft(false);
    setSelectedId('');
    setDraft(emptyRecipe(catalog?.databases.length === 1 ? [catalog.databases[0].id] : []));
    setTagText('');
    setTab('editor');
    setError('');
    setNotice('');
  }
  const leaveDraftRef = useRef(leaveDraft);
  leaveDraftRef.current = leaveDraft;
  useEffect(() => {
    setGuard(() => leaveDraftRef.current());
    return () => setGuard(null);
  }, []);
  useEffect(() => {
    if (seed) {
      pendingSeed.current = seed;
      if (workspaceId !== seed.workspaceId) setWorkspaceId(seed.workspaceId);
      else {
        openSeed(seed);
        pendingSeed.current = null;
        onSeedConsumed?.();
      }
    }
  }, [seed]);
  useEffect(() => {
    runtime.invalidateRuntime(true);
    setCatalog(null);
    setEntries([]);
    setSelectedId('');
    setDraft(null);
    setNotice('');
    setError('');
    if (!workspaceId) return;
    const controller = new AbortController();
    setLoading(true);
    Promise.all([
      request<Catalog>(`/api/workspaces/${encodeURIComponent(workspaceId)}/catalog`, { signal: controller.signal }),
      request<RecipeEntry[]>(`/api/workspaces/${encodeURIComponent(workspaceId)}/recipes`, { signal: controller.signal }),
    ])
      .then(([loadedCatalog, loadedEntries]) => {
        setCatalog(loadedCatalog);
        setEntries(loadedEntries);
        const supplied = pendingSeed.current;
        if (supplied && supplied.workspaceId === workspaceId) {
          openSeed(supplied);
          pendingSeed.current = null;
          onSeedConsumed?.();
        } else if (loadedEntries.length) {
          const requested =
            initialWorkspaceId === workspaceId ? loadedEntries.find(entry => entry.definition.id === initialRecipeId) : undefined;
          openEntry(requested || loadedEntries[0]);
          if (requested) {
            setTab(initialTab);
            recipePicks.recordOpened(requested.definition.id);
          }
        }
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Не удалось загрузить каталог рецептов.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [workspaceId]);
  useEffect(() => {
    if (workspaceId && !loading && draftWorkspace.current === workspaceId && (!openNonce || appliedOpen.current === openNonce))
      updateRouteParams('recipes', { ws: workspaceId, recipe: selectedId, tab });
  }, [workspaceId, selectedId, tab, loading]);
  // The app approved the route transition. Wait for this workspace's entries before consuming it once.
  useEffect(() => {
    if (!openNonce || appliedOpen.current === openNonce || initialWorkspaceId !== workspaceId || loading || !catalog) return;
    const entry = entries.find(item => item.definition.id === initialRecipeId);
    appliedOpen.current = openNonce;
    if (entry) {
      if (entry.definition.id !== selectedId) openEntry(entry);
      setTab(initialTab);
      recipePicks.recordOpened(entry.definition.id);
    } else if (!initialRecipeId && selectedId) setTab(initialTab);
  }, [openNonce, initialRecipeId, initialWorkspaceId, workspaceId, loading, catalog, entries]);
  function changeDraft(updates: Partial<RecipeDefinition>) {
    runtime.invalidateRuntime();
    setDraft(previous => (previous ? { ...previous, ...updates } : previous));
    setNotice('');
  }
  async function refreshCatalog() {
    if (!workspaceId) return;
    setLoading(true);
    setError('');
    const target = workspaceId;
    try {
      const loaded = await request<RecipeEntry[]>(`/api/workspaces/${encodeURIComponent(target)}/recipes`);
      if (workspaceRef.current === target) setEntries(loaded);
    } catch (cause) {
      setError(errorMessage(cause, t('Каталог не обновлён.')));
    } finally {
      setLoading(false);
    }
  }
  async function saveRecipe() {
    if (!draft) return;
    setBusy(true);
    setError('');
    const current = scopeOf();
    try {
      const isExisting = Boolean(selectedEntry);
      const url = isExisting
        ? `/api/recipes/${encodeURIComponent(selectedId)}`
        : `/api/workspaces/${encodeURIComponent(workspaceId)}/recipes${importedDraft ? '/import' : ''}`;
      const definition = recipeForSave(draft);
      const saved = await request<RecipeEntry>(url, {
        method: isExisting ? 'PUT' : 'POST',
        body: JSON.stringify(importedDraft && !isExisting ? { recipe: definition } : definition),
      });
      if (!current()) return;
      await discardRecovery();
      if (!current()) return;
      setEntries(previous => [...previous.filter(entry => entry.definition.id !== saved.definition.id), saved]);
      openEntry(saved, 'editor');
      setNotice(t('Сохранена версия {0}.', saved.definition.version));
    } catch (cause) {
      if (current()) setError(errorMessage(cause, t('Рецепт не сохранён.')));
    } finally {
      setBusy(false);
    }
  }
  async function cloneRecipe() {
    if (!selectedEntry) return;
    setBusy(true);
    setError('');
    const current = scopeOf();
    try {
      const copied = await request<RecipeEntry>(`/api/recipes/${encodeURIComponent(selectedId)}/clone`, {
        method: 'POST',
        body: JSON.stringify({ name: t('{0} · копия', selectedEntry.definition.name) }),
      });
      if (!current()) return;
      setEntries(previous => [...previous, copied]);
      openEntry(copied, 'editor');
      setNotice(t('Копия рецепта создана.'));
    } catch (cause) {
      if (current()) setError(errorMessage(cause, t('Не удалось скопировать рецепт.')));
    } finally {
      setBusy(false);
    }
  }
  async function importSql(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (file.size > 512 * 1024) {
      setError(t('SQL-файл превышает 512 КиБ. Выберите отдельный запрос для рецепта.'));
      return;
    }
    setBusy(true);
    try {
      const text = await file.text();
      if (draft) changeDraft({ sqlTemplate: text, ...(draft.name.trim() ? {} : { name: file.name.replace(/\.sql$/i, '') }) });
      setTab('editor');
      setNotice(t('SQL-файл загружен в черновик. Сохраните рецепт после проверки.'));
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось прочитать SQL-файл.')));
    } finally {
      setBusy(false);
    }
  }
  async function importJson(event: ChangeEvent<HTMLInputElement>) {
    const file = event.target.files?.[0];
    event.target.value = '';
    if (!file) return;
    if (file.size > 1024 * 1024) {
      setError(t('JSON-рецепт превышает1 МиБ.'));
      return;
    }
    setBusy(true);
    setError('');
    try {
      const definition = decodeRecipeImport(JSON.parse(await file.text()));
      const known = new Set(catalog?.databases.map(database => database.id) || []);
      draftWorkspace.current = workspaceId;
      runtime.invalidateRuntime(true);
      setSelectedId('');
      setImportedDraft(true);
      setDraft({ ...definition, id: '', version: 0, databaseIds: definition.databaseIds.filter(id => known.has(id)) });
      setTagText(definition.tags.join(', '));
      setTab('editor');
      setNotice(t('JSON открыт для проверки. Выберите БД этой рабочей области и сохраните импортированный рецепт.'));
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось импортировать определение.')));
    } finally {
      setBusy(false);
    }
  }
  async function exportRecipe() {
    if (!selectedEntry) return;
    try {
      const envelope = await request<unknown>(`/api/recipes/${encodeURIComponent(selectedId)}/export`);
      saveDownload(JSON.stringify(envelope, null, 2), filename(selectedEntry.definition.name) + '.recipe.json', 'application/json');
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось экспортировать рецепт.')));
    }
  }
  async function rollback(version: RecipeVersion) {
    if (!selectedEntry) return;
    setBusy(true);
    setError('');
    const current = scopeOf();
    try {
      const restored = await request<RecipeEntry>(`/api/recipes/${encodeURIComponent(selectedId)}/rollback`, {
        method: 'POST',
        body: JSON.stringify({ version: version.version, expectedVersion: selectedEntry.definition.version }),
      });
      if (!current()) return;
      setEntries(previous => previous.map(entry => (entry.definition.id === restored.definition.id ? restored : entry)));
      openEntry(restored, 'editor');
      setNotice(t('Версия {0} восстановлена как новая версия {1}.', version.version, restored.definition.version));
    } catch (cause) {
      if (current()) setError(errorMessage(cause, t('Не удалось восстановить версию.')));
    } finally {
      setBusy(false);
    }
  }
  async function refreshValidation() {
    if (!selectedEntry) return;
    setBusy(true);
    runtime.setRuntimeError('');
    try {
      await request<unknown>(`/api/recipes/${encodeURIComponent(selectedId)}/validate`, { method: 'POST', body: '{}' });
      const updated = await request<RecipeEntry>(`/api/recipes/${encodeURIComponent(selectedId)}`);
      setEntries(previous => previous.map(entry => (entry.definition.id === updated.definition.id ? updated : entry)));
      runtime.setAcknowledgeReview(false);
      runtime.setRendered(null);
    } catch (cause) {
      runtime.setRuntimeError(errorMessage(cause, t('Совместимость не проверена.')));
    } finally {
      setBusy(false);
    }
  }
  const inputShapeValid = useMemo(
    () =>
      draft
        ? draft.inputs.every(input => /^[A-Za-z][A-Za-z0-9_]{0,63}$/.test(input.name)) &&
          new Set(draft.inputs.map(input => input.name.toLowerCase())).size === draft.inputs.length
        : false,
    [draft?.inputs],
  );

  const commands = useRef({ save: () => {}, render: () => {} });
  commands.current = {
    save: () => {
      if (draft && !busy && draft.name.trim() && draft.sqlTemplate.trim() && draft.databaseIds.length && inputShapeValid) void saveRecipe();
    },
    render: () => {
      setTab('runtime');
      if (runtime.canRender && !runtime.parseBusy && !runtime.renderBusy && !busy) void runtime.renderRecipe();
      else setNotice(t('Сначала сохраните рецепт и проверьте входы. Ошибки ID блокируют генерацию.'));
    },
  };
  useEffect(() => {
    const listener = (event: KeyboardEvent) => {
      if (!(event.ctrlKey || event.metaKey) || event.repeat) return;
      if (event.key.toLowerCase() === 's') {
        event.preventDefault();
        event.stopPropagation();
        commands.current.save();
      } else if (event.key === 'Enter') {
        event.preventDefault();
        event.stopPropagation();
        commands.current.render();
      }
    };
    window.addEventListener('keydown', listener, true);
    return () => window.removeEventListener('keydown', listener, true);
  }, []);
  return (
    <div className="recipes-panel">
      {recoverable && (
        <div role="status" className="alert">
          <p>
            {t('A local authoring draft is available. Runtime IDs are never saved.')} · {formatDate(recoverable.savedAt)}
          </p>
          <button className="secondary-button" onClick={recoverDraft}>
            {t('Recover draft')}
          </button>
          <button className="secondary-button" onClick={discardRecovery}>
            {t('Discard draft')}
          </button>
        </div>
      )}
      {draftStorageError && <p role="alert">{t('Local draft storage is unavailable. Keep the editor open or export your recipe.')}</p>}
      <div className="recipes-workspace-row">
        <div>
          <button
            className="secondary-button"
            disabled={!workspaceId || busy}
            onClick={() => {
              if (leaveDraft()) newRecipe();
            }}
          >
            {t('+ Новый рецепт')}
          </button>
          <button className="secondary-button" disabled={!workspaceId || busy} onClick={() => importJsonInput.current?.click()}>
            {t('Импорт JSON')}
          </button>
          <button className="secondary-button" disabled={!workspaceId || busy || loading} onClick={() => void refreshCatalog()}>
            {t('Обновить')}
          </button>
          <input type="file" accept=".json,application/json" ref={importJsonInput} onChange={event => void importJson(event)} hidden />
        </div>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
          <button className="icon-button" aria-label={t('Скрыть ошибку')} onClick={() => setError('')}>
            ×
          </button>
        </div>
      )}
      {notice && (
        <div className="recipe-notice" role="status">
          {tMessage(notice)}
        </div>
      )}
      {!workspaceId ? (
        <div className="panel-placeholder">
          <strong>{t('Выберите рабочую область')}</strong>
          <p>{t('Рецепты связываются со снимками схем и остаются в локальном хранилище.')}</p>
        </div>
      ) : (
        <>
          <TableRecipeWizard
            workspaceId={workspaceId}
            onCreate={value => {
              if (leaveDraft()) openSeed(value);
            }}
          />
          <ChainRecipeWizard
            workspaceId={workspaceId}
            onCreate={value => {
              if (leaveDraft()) openSeed(value);
            }}
          />
          <div className="recipe-layout">
            <RecipeCatalogList
              entries={entries}
              loading={loading}
              busy={busy}
              selectedId={selectedId}
              favorites={recipePicks.picks.favorites}
              recent={recipePicks.picks.recent}
              onToggleFavorite={recipePicks.toggleFavorite}
              onOpen={entry => {
                if (!leaveDraft()) return;
                openEntry(entry);
                recipePicks.recordOpened(entry.definition.id);
              }}
            />
            <section className="recipe-editor-pane">
              {draft ? (
                <>
                  <div className="recipe-editor-heading">
                    <div>
                      <span className="object-kind">SQL SERVER RECIPE {selectedEntry && `· v${selectedEntry.definition.version}`}</span>
                      <h2>{draft.name || t('Новый рецепт')}</h2>
                    </div>
                    <span className={dirty ? 'neutral-tag' : 'confirmed-tag'}>{dirty ? t('Черновик · не сохранён') : t('Сохранено')}</span>
                  </div>
                  <div className="recipe-main-tabs">
                    <button className={tab === 'editor' ? 'selected' : ''} onClick={() => setTab('editor')}>
                      {t('Определение')}
                    </button>
                    <button className={tab === 'runtime' ? 'selected' : ''} onClick={() => setTab('runtime')}>
                      {t('Входы и SQL')}
                    </button>
                    <button className={tab === 'regression' ? 'selected' : ''} onClick={() => setTab('regression')}>
                      {t('Regression fixtures')}
                    </button>
                    <button className={tab === 'history' ? 'selected' : ''} onClick={() => setTab('history')}>
                      {t('Версии и lineage')}
                    </button>
                  </div>
                  {tab === 'editor' ? (
                    <RecipeEditorTab
                      draft={draft}
                      catalog={catalog}
                      editorKey={`${workspaceId}:${selectedId || 'draft'}`}
                      tagText={tagText}
                      onTagText={setTagText}
                      onEdit={changeDraft}
                      onImportSql={event => void importSql(event)}
                      inputShapeValid={inputShapeValid}
                      busy={busy}
                      existing={Boolean(selectedEntry)}
                      imported={importedDraft}
                      onSave={() => void saveRecipe()}
                      onClone={() => void cloneRecipe()}
                      onExport={() => void exportRecipe()}
                    />
                  ) : tab === 'runtime' ? (
                    <RecipeRuntimeTab
                      runtime={runtime}
                      draft={draft}
                      entry={selectedEntry}
                      dirty={dirty}
                      busy={busy}
                      onRefreshValidation={() => void refreshValidation()}
                    />
                  ) : tab === 'regression' ? (
                    <RecipeRegressionPanel key={selectedId} recipe={draft} disabled={dirty || busy} />
                  ) : selectedEntry ? (
                    <RecipeHistoryTab
                      key={selectedEntry.definition.id}
                      entry={selectedEntry}
                      dirty={dirty}
                      busy={busy}
                      onRestore={version => {
                        if (leaveDraft()) void rollback(version);
                      }}
                    />
                  ) : (
                    <div className="recipe-history">
                      <p className="muted-text">{t('Сохраните рецепт, чтобы увидеть версии, lineage и результаты проверок.')}</p>
                    </div>
                  )}
                </>
              ) : (
                <div className="recipe-editor-empty">
                  <strong>{t('Один рецепт — много списков ID')}</strong>
                  <p>
                    {t(
                      'Выберите сохранённый рецепт или создайте определение из реальной таблицы и колонки. SQL-шаблон и типы сохраняются; runtime-значения обрабатываются отдельно.',
                    )}
                  </p>
                  <button className="primary-button" onClick={newRecipe}>
                    {t('Создать рецепт')}
                  </button>
                </div>
              )}
            </section>
          </div>
        </>
      )}
    </div>
  );
}
