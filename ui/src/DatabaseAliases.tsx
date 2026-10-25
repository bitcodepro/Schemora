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
      setSaved(t('Mapping saved. New JOINs will use the specified database name.'));
      setReason('');
      onSaved();
    } catch (cause) {
      setError(errorMessage(cause, t('Could not save the SQL database name.')));
    } finally {
      setBusy(false);
    }
  }

  return (
    <details className="ef-mappings database-aliases">
      <summary>{t('SQL database names · for cross-database JOINs')}</summary>
      <div className="ef-mapping-body">
        <p>
          {t(
            'Enter the actual SQL Server database name if it differs from the SQL project name. The mapping is saved with a reason and used to generate three-part names.',
          )}
        </p>
        <div className="alias-form">
          <label>
            <span className="field-label">{t('Database in catalog')}</span>
            <select
              aria-label={t('Database for SQL name mapping')}
              value={databaseId}
              disabled={busy || !databases.length}
              onChange={event => setDatabaseId(event.target.value)}
            >
              <option value="">{t('Select a database')}</option>
              {databases.map(database => (
                <option value={database.id} key={database.id}>
                  {database.name}
                </option>
              ))}
            </select>
          </label>
          <label>
            <span className="field-label">{t('Name in SQL Server')}</span>
            <input
              aria-label={t('SQL database name')}
              value={name}
              maxLength={128}
              disabled={busy || !selected}
              onChange={event => setName(event.target.value)}
              placeholder={t('For example, Billing_Archive')}
            />
          </label>
          <label className="alias-reason">
            <span className="field-label">{t('Mapping reason')}</span>
            <textarea
              aria-label={t('Database name mapping reason')}
              value={reason}
              maxLength={1000}
              rows={2}
              disabled={busy || !selected}
              onChange={event => setReason(event.target.value)}
              placeholder={t('Source of the actual database name…')}
            />
          </label>
        </div>
        <button className="secondary-button" disabled={busy || !selected || !name.trim() || !reason.trim()} onClick={() => void save()}>
          {busy ? t('Saving…') : t('Save SQL name')}
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
