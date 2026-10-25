import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useState } from 'react';
import { errorMessage, request } from './api';
import type { Database } from './types';

export default function DatabaseAliases({ databases, onSaved }: { databases: Database[]; onSaved: () => void }) {
  useLocale();
  const [databaseId, setDatabaseId] = useState('');
  const [name, setName] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [saved, setSaved] = useState('');
  const selected = databases.find(database => database.id === databaseId);

  useEffect(() => {
    if (!databases.some(database => database.id === databaseId)) setDatabaseId(databases[0]?.id || '');
  }, [databases, databaseId]);
  useEffect(() => {
    setName(selected?.name || '');
    setReason('');
    setError('');
    setSaved('');
  }, [selected?.id]);
  useEffect(() => {
    setName(selected?.name || '');
  }, [selected?.name]);

  async function save() {
    if (!selected || !name.trim() || !reason.trim()) return;
    setBusy(true);
    setError('');
    setSaved('');
    try {
      await request<unknown>(`/api/databases/${encodeURIComponent(selected.id)}/name`, {
        method: 'PUT',
        body: JSON.stringify({ name: name.trim(), reason: reason.trim() }),
      });
      setSaved(t('Соответствие сохранено. Новые JOIN используют указанное имя БД.'));
      setReason('');
      onSaved();
    } catch (cause) {
      setError(errorMessage(cause, t('Не удалось сохранить SQL-имя БД.')));
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="ef-mappings database-aliases">
      <summary>{t('SQL-имена баз данных · для межбазовых JOIN')}</summary>
      <div className="ef-mapping-body">
        <p>
          {t(
            'Укажите фактическое имя БД в SQL Server, если оно отличается от имени SQL-проекта. Сопоставление сохраняется с основанием и используется при генерации трёхчастных имён.',
          )}
        </p>
        <div className="alias-form">
          <label>
            <span className="field-label">{t('База в каталоге')}</span>
            <select
              aria-label={t('База для сопоставления SQL-имени')}
              value={databaseId}
              disabled={busy || !databases.length}
              onChange={event => setDatabaseId(event.target.value)}
            >
              <option value="">{t('Выберите БД')}</option>
              {databases.map(database => (
                <option value={database.id} key={database.id}>
                  {database.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span className="field-label">{t('Имя в SQL Server')}</span>
            <input
              aria-label={t('SQL-имя базы данных')}
              value={name}
              maxLength={128}
              disabled={busy || !selected}
              onChange={event => setName(event.target.value)}
              placeholder={t('Например, Billing_Archive')}
            />
          </label>
          <label className="alias-reason">
            <span className="field-label">{t('Основание сопоставления')}</span>
            <textarea
              aria-label={t('Основание сопоставления имени БД')}
              value={reason}
              maxLength={1000}
              rows={2}
              disabled={busy || !selected}
              onChange={event => setReason(event.target.value)}
              placeholder={t('Источник фактического имени базы…')}
            />
          </label>
        </div>
        <button className="secondary-button" disabled={busy || !selected || !name.trim() || !reason.trim()} onClick={() => void save()}>
          {busy ? t('Сохранение…') : t('Сохранить SQL-имя')}
        </button>
        {error && (
          <p className="job-error" role="alert">
            {tMessage(error)}
          </p>
        )}
        {saved && (
          <p className="copy-state" role="status">
            {tMessage(saved)}
          </p>
        )}
      </div>
    </details>
  );
}
