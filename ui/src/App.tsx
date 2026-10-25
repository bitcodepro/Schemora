import WorkspaceQuality from './WorkspaceQuality';
import WorkspaceNavigator from './WorkspaceNavigator';
import { t, tMessage, getLocaleRevision, isLocale, setLocale } from './i18n.ts';
import type { Locale } from './i18n.ts';
import LanguageSelector from './LanguageSelector';
import { useLocale } from './i18nReact';
import { lazy, Suspense, useEffect, useMemo, useRef, useState } from 'react';
import { request, sessionToken } from './api';
import type { Diagnostics } from './ToolReadiness';
import WorkspacePanel from './WorkspacePanel';
import Icon from './Icon';
import CommandPalette from './CommandPalette';
import ShortcutsHelp from './ShortcutsHelp';
import type { PaletteTarget } from './CommandPalette';
import { useWorkspaces, WorkspaceSelector } from './workspaceContext';
const GraphPanel = lazy(() => import('./GraphPanel'));
const RecipesPanel = lazy(() => import('./RecipesPanel'));
const ChangesPanel = lazy(() => import('./ChangesPanel'));
const SettingsPanel = lazy(() => import('./SettingsPanel'));
import SchemaCatalogPage, { useSchemaBrowser } from './SchemaCatalogPage';
import ToolReadiness from './ToolReadiness';
import type { RecipeSeed } from './recipeTypes';
import { useAppNavigation } from './useAppNavigation';

export default function App() {
  useLocale();
  const { page, navigation, navigate } = useAppNavigation();
  const previousPage = useRef(page);
  useEffect(() => {
    if (previousPage.current !== page) {
      document.getElementById('page-title')?.focus();
      previousPage.current = page;
    }
  }, [page]);
  const [theme, setTheme] = useState<'dark' | 'light'>('dark');
  const [themeError, setThemeError] = useState('');
  const [languageError, setLanguageError] = useState('');
  const languageSave = useRef<Promise<unknown>>(Promise.resolve());
  useEffect(() => {
    if (!sessionToken) return;
    const controller = new AbortController();
    const revision = getLocaleRevision();
    void request<{ value: unknown }>('/api/settings/language', { signal: controller.signal })
      .then(result => {
        if (!controller.signal.aborted && getLocaleRevision() === revision && result.value != null)
          setLocale(isLocale(result.value) ? result.value : 'en');
      })
      .catch(cause => {
        if (!controller.signal.aborted && getLocaleRevision() === revision)
          setLanguageError(cause instanceof Error ? cause.message : t('Could not load the language preference.'));
      });
    return () => controller.abort();
  }, []);
  async function changeLanguage(value: Locale) {
    setLocale(value);
    setLanguageError('');
    if (!sessionToken) return;
    const revision = getLocaleRevision();
    const save = languageSave.current
      .catch(() => undefined)
      .then(() => request('/api/settings/language', { method: 'PUT', body: JSON.stringify({ value }) }));
    languageSave.current = save;
    try {
      await save;
    } catch (cause) {
      if (getLocaleRevision() === revision)
        setLanguageError(cause instanceof Error ? cause.message : t('Could not save the language preference.'));
    }
  }
  useEffect(() => {
    if (!sessionToken) return;
    const controller = new AbortController();
    void request<{ value: unknown }>('/api/settings/theme', { signal: controller.signal })
      .then(result => {
        if (result.value === 'light' || result.value === 'dark') setTheme(result.value);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setThemeError(cause instanceof Error ? cause.message : t('Не удалось прочитать тему.'));
      });
    return () => controller.abort();
  }, []);
  useEffect(() => {
    document.documentElement.dataset.theme = theme;
    window.dispatchEvent(new Event('schemora-theme'));
  }, [theme]);
  async function changeTheme(value: 'dark' | 'light') {
    setTheme(value);
    setThemeError('');
    try {
      await request('/api/settings/theme', { method: 'PUT', body: JSON.stringify({ value }) });
    } catch (cause) {
      setThemeError(cause instanceof Error ? cause.message : t('Не удалось сохранить тему.'));
    }
  }
  const [recipeSeed, setRecipeSeed] = useState<RecipeSeed | null>(null);
  const [diagnostics, setDiagnostics] = useState<Diagnostics | null>(null);
  const [diagnosticsBusy, setDiagnosticsBusy] = useState(false);
  const [diagnosticsError, setDiagnosticsError] = useState('');
  const [dockerRecovery, setDockerRecovery] = useState<{ severity: string; code: string; message: string }[]>([]);
  const { workspaces, workspaceId } = useWorkspaces();
  const routeWorkspace = navigation.route.params.ws || workspaceId;
  const schemaSelection = useMemo(
    () =>
      page === 'schema'
        ? {
            nonce: navigation.nonce,
            workspaceId: routeWorkspace,
            databaseId: navigation.route.params.db,
            objectId: navigation.route.params.object,
            view: navigation.route.params.view,
            snapshotId: navigation.route.params.snap,
          }
        : undefined,
    [navigation, page, routeWorkspace],
  );
  const schema = useSchemaBrowser(page === 'schema', workspaceId, () => navigate('schema'), schemaSelection);
  const recipeTab = navigation.route.params.tab;
  const [appVersion, setAppVersion] = useState('');
  const [appBuild, setAppBuild] = useState('');
  useEffect(() => {
    if (!sessionToken) return;
    void request<{ version?: string; build?: string }>('/api/health')
      .then(health => {
        setAppVersion(health.version || '');
        setAppBuild(health.build || '');
      })
      .catch(() => undefined);
  }, []);

  // Search and navigation open a recipe to run it; the Changes page opens its history for comparison and rollback.
  function openRecipe(workspaceId: string, recipeId: string, tab: 'runtime' | 'history' = 'runtime') {
    if (navigate('recipes', { ws: workspaceId, recipe: recipeId, tab })) setRecipeSeed(null);
  }
  function openFromPalette(target: PaletteTarget) {
    if (target.kind === 'page') navigate(target.page as typeof page);
    else if (target.kind === 'recipe') openRecipe(workspaceId, target.recipeId);
    else schema.openObject(workspaceId, target.databaseId, target.objectId, target.matchText);
  }

  async function refreshDiagnostics() {
    if (!sessionToken) return;
    setDiagnosticsBusy(true);
    setDiagnosticsError('');
    try {
      const [environment, docker] = await Promise.all([
        request<Diagnostics>('/api/diagnostics'),
        request<{ recoveryDiagnostics: { severity: string; code: string; message: string }[] }>('/api/schema/docker/status'),
      ]);
      setDiagnostics(environment);
      setDockerRecovery(docker.recoveryDiagnostics);
    } catch (cause) {
      setDiagnosticsError(cause instanceof Error ? cause.message : t('Не удалось проверить инструменты.'));
    } finally {
      setDiagnosticsBusy(false);
    }
  }

  useEffect(() => {
    void refreshDiagnostics();
  }, []);

  return (
    <div className="app-shell">
      <a
        className="skip-link"
        href="#main-content"
        onClick={event => {
          event.preventDefault();
          document.getElementById('main-content')?.focus();
        }}
      >
        {t('Skip to main content')}
      </a>
      <aside className="sidebar">
        <a
          className="brand"
          href="#"
          onClick={event => {
            event.preventDefault();
            navigate('schema');
          }}
          aria-label={t('Schemora, главная')}
        >
          <span className="brand-icon">
            <Icon name="layers" size={23} />
          </span>
          <span>
            Schemora<small>{t('SQL SCHEMA STUDIO')}</small>
          </span>
        </a>
        <div className="workspace-label">
          {t('РАБОЧАЯ ОБЛАСТЬ')} <span>{t('LOCAL')}</span>
        </div>
        <WorkspaceSelector className="sidebar-workspace" />
        <nav aria-label={t('Основная навигация')}>
          <button
            aria-current={page === 'workspace' ? 'page' : undefined}
            aria-label={t('Рабочая область')}
            className={`nav-item ${page === 'workspace' ? 'active' : ''}`}
            onClick={() => navigate('workspace')}
          >
            <Icon name="folder" />
            {t('Рабочая область')}
          </button>
          <button
            aria-current={page === 'schema' ? 'page' : undefined}
            aria-label={t('Каталог схем')}
            className={`nav-item ${page === 'schema' ? 'active' : ''}`}
            onClick={() => navigate('schema')}
          >
            <Icon name="table" />
            {t('Каталог схем')}
            {schema.graph && <span className="nav-count">{schema.graph.tables.length}</span>}
          </button>
          <button
            aria-current={page === 'graph' ? 'page' : undefined}
            aria-label={t('Связи и пути')}
            className={`nav-item ${page === 'graph' ? 'active' : ''}`}
            onClick={() => navigate('graph')}
          >
            <Icon name="branch" />
            {t('Связи и пути')}
          </button>
          <button
            aria-current={page === 'recipes' ? 'page' : undefined}
            aria-label={t('SQL-рецепты')}
            className={`nav-item ${page === 'recipes' ? 'active' : ''}`}
            onClick={() => navigate('recipes')}
          >
            <Icon name="code" />
            {t('SQL-рецепты')}
          </button>
          <button
            aria-current={page === 'changes' ? 'page' : undefined}
            aria-label={t('Изменения')}
            className={`nav-item ${page === 'changes' ? 'active' : ''}`}
            onClick={() => navigate('changes')}
          >
            <Icon name="refresh" />
            {t('Изменения')}
          </button>
          <button
            aria-current={page === 'professional' ? 'page' : undefined}
            aria-label={t('Workspace quality')}
            className={`nav-item ${page === 'professional' ? 'active' : ''}`}
            onClick={() => navigate('professional')}
          >
            <Icon name="check" />
            {t('Workspace quality')}
          </button>
        </nav>
        <div className="sidebar-bottom">
          <div className="privacy-note">
            <Icon name="shield" size={19} />
            <div>
              {t('Локальная работа')}
              <small>{t('Схемы остаются на компьютере')}</small>
            </div>
          </div>
          <button
            aria-current={page === 'diagnostics' ? 'page' : undefined}
            aria-label={t('Настройки и инструменты')}
            className={`nav-item ${page === 'diagnostics' ? 'active' : ''}`}
            onClick={() => navigate('diagnostics')}
          >
            <Icon name="tool" />
            {t('Настройки и инструменты')}
          </button>
          <div className="sidebar-version">
            <span className="status-dot" />
            {t('Локальное приложение')}{' '}
            <span title={appBuild ? t('Build {0}', appBuild) : undefined} data-build={appBuild || undefined}>
              {appVersion}
            </span>
          </div>
        </div>
      </aside>
      <ShortcutsHelp />
      <CommandPalette
        pages={[
          { page: 'workspace', label: t('Рабочая область') },
          { page: 'schema', label: t('Каталог схем') },
          { page: 'graph', label: t('Связи и пути') },
          { page: 'recipes', label: t('SQL-рецепты') },
          { page: 'changes', label: t('Изменения') },
          { page: 'professional', label: t('Workspace quality') },
          { page: 'diagnostics', label: t('Настройки и инструменты') },
        ]}
        onOpen={openFromPalette}
      />
      <main id="main-content" tabIndex={-1}>
        <header className="topbar">
          <div className="breadcrumbs">
            {t('Рабочая область')} <span>/</span>{' '}
            <strong>
              {page === 'professional'
                ? t('Workspace quality and tools')
                : page === 'workspace'
                  ? t('Рабочая область')
                  : page === 'schema'
                    ? t('Каталог схем')
                    : page === 'graph'
                      ? t('Связи и пути')
                      : page === 'recipes'
                        ? t('SQL-рецепты')
                        : page === 'changes'
                          ? t('Изменения схемы')
                          : t('Инструменты и среда')}
            </strong>
          </div>
          <div className="topbar-actions">
            <button className="search-trigger" onClick={() => window.dispatchEvent(new Event('schemora-open-search'))}>
              <Icon name="search" size={15} />
              {t('Search')}
              <kbd>Ctrl K</kbd>
            </button>
            <button
              className="search-trigger shortcuts-trigger"
              aria-label={t('Keyboard shortcuts')}
              title={t('Keyboard shortcuts')}
              onClick={() => window.dispatchEvent(new Event('schemora-open-shortcuts'))}
            >
              <kbd>?</kbd>
            </button>
            <WorkspaceSelector className="topbar-workspace" />
            <LanguageSelector onChange={value => void changeLanguage(value)} />
            <span className="local-badge">
              <span className="status-dot" />
              127.0.0.1 <span className="badge-divider">|</span> {t('Локальный сеанс')}
            </span>
          </div>
        </header>
        <div className="page-content">
          <div className="page-heading">
            <div>
              <div className="eyebrow">{t('SQL SERVER · LOCAL WORKSPACE')}</div>
              <h1 id="page-title" tabIndex={-1}>
                {page === 'professional'
                  ? t('Workspace quality and tools')
                  : page === 'workspace'
                    ? t('Проекты, схемы и изменения')
                    : page === 'schema'
                      ? t('Исследуйте структуру данных')
                      : page === 'graph'
                        ? t('Связи с понятным основанием')
                        : page === 'recipes'
                          ? t('Повторяемый SQL, проверяемые входы')
                          : page === 'changes'
                            ? t('Изменения без неожиданных правок')
                            : t('Инструменты и среда')}
              </h1>
              <p>
                {page === 'professional'
                  ? t('Review schema quality, trace analysis inputs and maintain your local catalog.')
                  : page === 'workspace'
                    ? t('Добавьте корневые папки, обнаружьте проекты и поддерживайте локальные снимки схем.')
                    : page === 'schema'
                      ? t('Откройте DACPAC или соберите SQL-проект. Получите модель таблиц и физических связей.')
                      : page === 'graph'
                        ? t('Исследуйте FK, C# mappings и логические кандидаты. Подтверждайте связи и стройте проверяемые JOIN.')
                        : page === 'recipes'
                          ? t('Сохраните SQL-шаблон и типы. Вставьте ID и получите детерминированный скрипт с точным отчётом.')
                          : page === 'changes'
                            ? t('Сравните снимки, проверьте влияние на рецепты и просмотрите SQL diff перед подтверждением исправления.')
                            : t('Проверьте доступность локальных инструментов перед сборкой и анализом.')}
              </p>
            </div>
            <div className="heading-icon">
              <Icon
                name={
                  page === 'workspace'
                    ? 'folder'
                    : page === 'schema'
                      ? 'table'
                      : page === 'graph'
                        ? 'branch'
                        : page === 'recipes'
                          ? 'code'
                          : page === 'changes'
                            ? 'refresh'
                            : 'tool'
                }
                size={32}
              />
            </div>
          </div>
          {!sessionToken && (
            <div className="alert warning">
              <Icon name="shield" />
              <div>
                <strong>{t('Откройте приложение по ссылке запуска')}</strong>
                <p>{t('В этой вкладке нет токена локального сеанса. Используйте ссылку, которую Schemora создаёт при запуске EXE.')}</p>
              </div>
            </div>
          )}
          {languageError && (
            <div className="alert warning" role="alert">
              {tMessage(languageError)}
            </div>
          )}
          {themeError && (
            <div className="alert warning" role="alert">
              {tMessage(themeError)}
            </div>
          )}
          {page === 'diagnostics' && (
            <nav className="settings-jump" aria-label={t('Settings sections')}>
              {(
                [
                  ['settings-tools', 'Tools'],
                  ['settings-language', 'Interface language'],
                  ['settings-appearance', 'Оформление и команды'],
                  ['settings-configuration', 'Portable workspace configuration'],
                  ['settings-history', 'Memory and history'],
                  ['settings-diagnostics', 'Diagnostics and support report'],
                  ['settings-backup', 'Хранилище и резервная копия'],
                  ['settings-session', 'Завершить локальный сеанс'],
                ] as const
              ).map(([target, label]) => (
                <button key={target} onClick={() => document.getElementById(target)?.scrollIntoView({ block: 'start' })}>
                  {t(label)}
                </button>
              ))}
            </nav>
          )}
          <Suspense
            fallback={
              <div className="diagnostics-loading">
                <span className="spinner" />
                {t('Загрузка раздела…')}
              </div>
            }
          >
            {page === 'professional' ? (
              <WorkspaceQuality onRecipe={openRecipe} onObject={schema.openObject} />
            ) : page === 'workspace' ? (
              <>
                {!workspaces.length && (
                  <WorkspaceNavigator onObject={(loaded, database) => schema.show(loaded, database)} onRecipe={openRecipe} />
                )}
                <WorkspacePanel onOpenSchema={(loaded, database, snapshot) => schema.show(loaded, database, snapshot)} />
                {workspaces.length > 0 && (
                  <WorkspaceNavigator onObject={(loaded, database) => schema.show(loaded, database)} onRecipe={openRecipe} />
                )}
              </>
            ) : page === 'graph' ? (
              <GraphPanel
                key={navigation.nonce}
                initialDatabaseId={navigation.route.params.db || (schema.schemaWorkspaceId === workspaceId ? schema.databaseId : '')}
                onCreateRecipe={seed => {
                  if (navigate('recipes', { ws: seed.workspaceId })) setRecipeSeed(seed);
                }}
              />
            ) : page === 'recipes' ? (
              <RecipesPanel
                seed={recipeSeed}
                onSeedConsumed={() => setRecipeSeed(null)}
                initialWorkspaceId={routeWorkspace}
                initialRecipeId={navigation.route.params.recipe}
                openNonce={navigation.nonce}
                initialTab={recipeTab === 'editor' || recipeTab === 'history' || recipeTab === 'regression' ? recipeTab : 'runtime'}
              />
            ) : page === 'changes' ? (
              <ChangesPanel onOpenRecipe={(workspace, recipe) => openRecipe(workspace, recipe, 'history')} />
            ) : page === 'schema' ? (
              <SchemaCatalogPage browser={schema} />
            ) : (
              <ToolReadiness
                diagnostics={diagnostics}
                busy={diagnosticsBusy}
                error={diagnosticsError}
                dockerRecovery={dockerRecovery}
                onRefresh={() => void refreshDiagnostics()}
              />
            )}
            {page === 'diagnostics' && (
              <SettingsPanel
                onLanguageChange={value => void changeLanguage(value)}
                theme={theme}
                onThemeChange={value => void changeTheme(value)}
              />
            )}
          </Suspense>
          <footer className="page-footer">
            <span>
              <Icon name="shield" size={13} />
              {t('Анализ метаданных · запросы к рабочим БД не выполняются')}
            </span>
            <span>Schemora / Schema Studio</span>
          </footer>
        </div>
      </main>
    </div>
  );
}
