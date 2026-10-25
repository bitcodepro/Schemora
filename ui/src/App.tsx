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
        if (!controller.signal.aborted) setThemeError(cause instanceof Error ? cause.message : t('Could not load the theme.'));
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
      setThemeError(cause instanceof Error ? cause.message : t('Could not save the theme.'));
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
      setDiagnosticsError(cause instanceof Error ? cause.message : t('Could not check the tools.'));
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
          aria-label={t('Schemora, home')}
        >
          <span className="brand-icon">
            <Icon name="layers" size={23} />
          </span>
          <span>
            Schemora<small>{t('SQL SCHEMA STUDIO')}</small>
          </span>
        </a>
        <div className="workspace-label">
          {t('WORKSPACE')} <span>{t('LOCAL')}</span>
        </div>
        <WorkspaceSelector className="sidebar-workspace" />
        <nav aria-label={t('Main navigation')}>
          <button
            aria-current={page === 'workspace' ? 'page' : undefined}
            aria-label={t('Workspace')}
            className={`nav-item ${page === 'workspace' ? 'active' : ''}`}
            onClick={() => navigate('workspace')}
          >
            <Icon name="folder" />
            {t('Workspace')}
          </button>
          <button
            aria-current={page === 'schema' ? 'page' : undefined}
            aria-label={t('Schema catalog')}
            className={`nav-item ${page === 'schema' ? 'active' : ''}`}
            onClick={() => navigate('schema')}
          >
            <Icon name="table" />
            {t('Schema catalog')}
            {schema.graph && <span className="nav-count">{schema.graph.tables.length}</span>}
          </button>
          <button
            aria-current={page === 'graph' ? 'page' : undefined}
            aria-label={t('Relationships and paths')}
            className={`nav-item ${page === 'graph' ? 'active' : ''}`}
            onClick={() => navigate('graph')}
          >
            <Icon name="branch" />
            {t('Relationships and paths')}
          </button>
          <button
            aria-current={page === 'recipes' ? 'page' : undefined}
            aria-label={t('SQL recipes')}
            className={`nav-item ${page === 'recipes' ? 'active' : ''}`}
            onClick={() => navigate('recipes')}
          >
            <Icon name="code" />
            {t('SQL recipes')}
          </button>
          <button
            aria-current={page === 'changes' ? 'page' : undefined}
            aria-label={t('Changes')}
            className={`nav-item ${page === 'changes' ? 'active' : ''}`}
            onClick={() => navigate('changes')}
          >
            <Icon name="refresh" />
            {t('Changes')}
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
              {t('Working locally')}
              <small>{t('Schemas stay on your computer')}</small>
            </div>
          </div>
          <button
            aria-current={page === 'diagnostics' ? 'page' : undefined}
            aria-label={t('Settings and tools')}
            className={`nav-item ${page === 'diagnostics' ? 'active' : ''}`}
            onClick={() => navigate('diagnostics')}
          >
            <Icon name="tool" />
            {t('Settings and tools')}
          </button>
          <div className="sidebar-version">
            <span className="status-dot" />
            {t('Local application')}{' '}
            <span title={appBuild ? t('Build {0}', appBuild) : undefined} data-build={appBuild || undefined}>
              {appVersion}
            </span>
          </div>
        </div>
      </aside>
      <ShortcutsHelp />
      <CommandPalette
        pages={[
          { page: 'workspace', label: t('Workspace') },
          { page: 'schema', label: t('Schema catalog') },
          { page: 'graph', label: t('Relationships and paths') },
          { page: 'recipes', label: t('SQL recipes') },
          { page: 'changes', label: t('Changes') },
          { page: 'professional', label: t('Workspace quality') },
          { page: 'diagnostics', label: t('Settings and tools') },
        ]}
        onOpen={openFromPalette}
      />
      <main id="main-content" tabIndex={-1}>
        <header className="topbar">
          <div className="breadcrumbs">
            {t('Workspace')} <span>/</span>{' '}
            <strong>
              {page === 'professional'
                ? t('Workspace quality and tools')
                : page === 'workspace'
                  ? t('Workspace')
                  : page === 'schema'
                    ? t('Schema catalog')
                    : page === 'graph'
                      ? t('Relationships and paths')
                      : page === 'recipes'
                        ? t('SQL recipes')
                        : page === 'changes'
                          ? t('Schema changes')
                          : t('Tools and environment')}
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
              127.0.0.1 <span className="badge-divider">|</span> {t('Local session')}
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
                    ? t('Projects, schemas, and changes')
                    : page === 'schema'
                      ? t('Explore your data structure')
                      : page === 'graph'
                        ? t('Relationships with clear evidence')
                        : page === 'recipes'
                          ? t('Repeatable SQL, validated inputs')
                          : page === 'changes'
                            ? t('Changes without unexpected edits')
                            : t('Tools and environment')}
              </h1>
              <p>
                {page === 'professional'
                  ? t('Review schema quality, trace analysis inputs and maintain your local catalog.')
                  : page === 'workspace'
                    ? t('Add root folders, discover projects, and maintain local schema snapshots.')
                    : page === 'schema'
                      ? t('Open a DACPAC or build a SQL project to get a model of tables and physical relationships.')
                      : page === 'graph'
                        ? t(
                            'Explore foreign keys, C# mappings, and candidate relationships. Confirm relationships and build verifiable JOINs.',
                          )
                        : page === 'recipes'
                          ? t('Save a SQL template and input types. Paste IDs to generate a deterministic script with a detailed report.')
                          : page === 'changes'
                            ? t('Compare snapshots, check the impact on recipes, and review the SQL diff before approving a repair.')
                            : t('Check that local tools are available before building and analyzing.')}
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
                <strong>{t('Open the app using its launch link')}</strong>
                <p>{t('This tab has no local session token. Use the link Schemora creates when you launch the EXE.')}</p>
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
                  ['settings-appearance', 'Appearance and shortcuts'],
                  ['settings-configuration', 'Portable workspace configuration'],
                  ['settings-history', 'Memory and history'],
                  ['settings-diagnostics', 'Diagnostics and support report'],
                  ['settings-backup', 'Storage and backup'],
                  ['settings-session', 'End local session'],
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
                {t('Loading section…')}
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
              {t('Metadata analysis · no queries are run against production databases')}
            </span>
            <span>Schemora / Schema Studio</span>
          </footer>
        </div>
      </main>
    </div>
  );
}
