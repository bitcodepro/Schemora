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
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Не удалось прочитать настройки.')));
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
      setError(errorMessage(cause, t('Операция не выполнена.')));
    } finally {
      setBusy(false);
    }
  }
  async function backup() {
    await action(async () => {
      const response = await fetch('/api/storage/backup', { method: 'POST', headers: { 'X-Schemora-Token': sessionToken } });
      if (!response.ok) {
        const body = await response.json().catch(() => null);
        throw new Error(body?.message || t('Не удалось создать копию ({0}).', response.status));
      }
      download(await response.blob(), `schemora-backup-${new Date().toISOString().slice(0, 10)}.sqlite`);
      setNotice(t('Резервная копия каталога готова к сохранению.'));
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
        <h2>{t('Оформление и команды')}</h2>
        <div className="settings-theme">
          <span>{t('Тема интерфейса')}</span>
          <button
            className={theme === 'dark' ? 'primary-button' : 'secondary-button'}
            onClick={() => onThemeChange('dark')}
            aria-pressed={theme === 'dark'}
          >
            {t('Тёмная')}
          </button>
          <button
            className={theme === 'light' ? 'primary-button' : 'secondary-button'}
            onClick={() => onThemeChange('light')}
            aria-pressed={theme === 'light'}
          >
            {t('Светлая')}
          </button>
        </div>
        <p className="muted-text">
          {t('В редакторе: Ctrl+S — сохранить версию рецепта, Ctrl+Enter — сформировать SQL после проверки входов.')}
        </p>
      </section>
      <PortableConfigurationPanel workspaceId={workspaceId} />
      <StorageDiagnosticsPanel />
      <section className="settings-card" id="settings-backup">
        <h2>{t('Хранилище и резервная копия')}</h2>
        {storage ? (
          <>
            <dl className="storage-details">
              <div>
                <dt>{t('Каталог данных')}</dt>
                <dd>{storage.dataDirectory}</dd>
              </div>
              <div>
                <dt>
                  {t('SQLite · версия')} {storage.catalogVersion}
                </dt>
                <dd>{storage.catalogPath}</dd>
              </div>
              <div>
                <dt>{t('Активных заданий')}</dt>
                <dd>{storage.activeJobs}</dd>
              </div>
            </dl>
            <button className="secondary-button" disabled={busy || stopped} onClick={() => void backup()}>
              {t('Скачать резервную копию SQLite')}
            </button>
            <p className="muted-text">
              {t(
                'Копия содержит снимки, рецепты, версии и настройки. Для восстановления остановите приложение, затем запустите команду, подставив свои пути:',
              )}
            </p>
            <code className="recovery-command">{storage.recoveryCommand}</code>
            <button
              className="secondary-button"
              onClick={() =>
                void navigator.clipboard
                  .writeText(storage.recoveryCommand)
                  .then(() => setNotice(t('Команда восстановления скопирована.')))
                  .catch(cause => setError(errorMessage(cause, t('Копирование недоступно.'))))
              }
            >
              {t('Копировать команду')}
            </button>
          </>
        ) : (
          <p className="muted-text">{t('Сведения о хранилище появятся в локальном сеансе.')}</p>
        )}
      </section>
      <section className="settings-card" id="settings-session">
        <h2>{t('Завершить локальный сеанс')}</h2>
        <p className="muted-text">{t('Останавливает процесс Schemora. Сохраните открытые изменения рецепта перед завершением.')}</p>
        <button
          className="secondary-button"
          disabled={!sessionToken || busy || stopped}
          onClick={() =>
            void action(async () => {
              await request('/api/application/shutdown', { method: 'POST' });
              setStopped(true);
              setNotice(t('Schemora остановлена. Эту вкладку можно закрыть. Для продолжения запустите приложение снова.'));
            })
          }
        >
          {stopped ? t('Приложение остановлено') : t('Завершить Schemora')}
        </button>
      </section>
    </div>
  );
}
