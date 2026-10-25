import PortableConfigurationPanel from './PortableConfigurationPanel';
import StorageDiagnosticsPanel from './StorageDiagnosticsPanel';
import LanguageSelector from './LanguageSelector';
import type { Locale } from './i18n.ts';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useState } from 'react';
import { errorMessage, request, sessionToken } from './api';
import { useWorkspaces } from './workspaceContext';
import './settings.css';

type StorageInfo = { dataDirectory: string; catalogPath: string; catalogVersion: number; activeJobs: number; recoveryCommand: string };
function download(contents: Blob, name: string) {
  const url = URL.createObjectURL(contents);
  const a = document.createElement('a');
  a.href = url;
  a.download = name;
  a.click();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}

export default function SettingsPanel({
  theme,
  onThemeChange,
  onLanguageChange,
}: {
  onLanguageChange: (locale: Locale) => void;
  theme: 'dark' | 'light';
  onThemeChange: (theme: 'dark' | 'light') => void;
}) {
  useLocale();
  const [storage, setStorage] = useState<StorageInfo | null>(null);
  const { workspaceId } = useWorkspaces();
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [stopped, setStopped] = useState(false);
  useEffect(() => {
    if (!sessionToken) return;
    const controller = new AbortController();
    void request<StorageInfo>('/api/storage', { signal: controller.signal })
      .then(setStorage)
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Could not load settings.')));
      });
    return () => controller.abort();
  }, []);
  async function action(callback: () => Promise<void>) {
    if (busy || stopped) return;
    setBusy(true);
    setError('');
    setNotice('');
    try {
      await callback();
    } catch (cause) {
      setError(errorMessage(cause, t('Operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  async function backup() {
    await action(async () => {
      const response = await fetch('/api/storage/backup', { method: 'POST', headers: { 'X-Schemora-Token': sessionToken } });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message || t('Could not create a backup ({0}).', response.status));
      }
      download(await response.blob(), `schemora-backup-${new Date().toISOString().slice(0, 10)}.sqlite`);
      setNotice(t('The catalog backup is ready to save.'));
    });
  }
  return (
    <div className="settings-panel">
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      {notice && (
        <p className="settings-notice" role="status">
          {tMessage(notice)}
        </p>
      )}
      <section className="settings-card" id="settings-language">
        <h2>{t('Interface language')}</h2>
        <LanguageSelector onChange={onLanguageChange} />
        <p className="muted-text">{t('Choose English, Russian or Ukrainian. Your choice is saved for future launches.')}</p>
      </section>
      <section className="settings-card" id="settings-appearance">
        <h2>{t('Appearance and shortcuts')}</h2>
        <div className="settings-theme">
          <span>{t('Interface theme')}</span>
          <button
            className={theme === 'dark' ? 'primary-button' : 'secondary-button'}
            onClick={() => onThemeChange('dark')}
            aria-pressed={theme === 'dark'}
          >
            {t('Dark')}
          </button>
          <button
            className={theme === 'light' ? 'primary-button' : 'secondary-button'}
            onClick={() => onThemeChange('light')}
            aria-pressed={theme === 'light'}
          >
            {t('Light')}
          </button>
        </div>
        <p className="muted-text">
          {t('In the editor: Ctrl+S saves a recipe version; Ctrl+Enter generates SQL after validating the inputs.')}
        </p>
      </section>
      <PortableConfigurationPanel workspaceId={workspaceId} />
      <StorageDiagnosticsPanel />
      <section className="settings-card" id="settings-backup">
        <h2>{t('Storage and backup')}</h2>
        {storage ? (
          <>
            <dl className="storage-details">
              <div>
                <dt>{t('Data directory')}</dt>
                <dd>{storage.dataDirectory}</dd>
              </div>
              <div>
                <dt>
                  {t('SQLite · version')} {storage.catalogVersion}
                </dt>
                <dd>{storage.catalogPath}</dd>
              </div>
              <div>
                <dt>{t('Active jobs')}</dt>
                <dd>{storage.activeJobs}</dd>
              </div>
            </dl>
            <button className="secondary-button" disabled={busy || stopped} onClick={() => void backup()}>
              {t('Download SQLite backup')}
            </button>
            <p className="muted-text">
              {t(
                'The backup contains snapshots, recipes, versions, and settings. To restore it, stop the app, then run this command with your own paths:',
              )}
            </p>
            <code className="recovery-command">{storage.recoveryCommand}</code>
            <button
              className="secondary-button"
              onClick={() =>
                void navigator.clipboard
                  .writeText(storage.recoveryCommand)
                  .then(() => setNotice(t('Restore command copied.')))
                  .catch(cause => setError(errorMessage(cause, t('Copying is unavailable.'))))
              }
            >
              {t('Copy command')}
            </button>
          </>
        ) : (
          <p className="muted-text">{t('Storage details will be available in a local session.')}</p>
        )}
      </section>
      <section className="settings-card" id="settings-session">
        <h2>{t('End local session')}</h2>
        <p className="muted-text">{t('Stops the Schemora process. Save any open recipe changes before exiting.')}</p>
        <button
          className="secondary-button"
          disabled={!sessionToken || busy || stopped}
          onClick={() =>
            void action(async () => {
              await request('/api/application/shutdown', { method: 'POST' });
              setStopped(true);
              setNotice(t('Schemora has stopped. You can close this tab. Launch the app again to continue.'));
            })
          }
        >
          {stopped ? t('Application stopped') : t('Quit Schemora')}
        </button>
      </section>
    </div>
  );
}
