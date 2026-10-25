import { useEffect, useState } from 'react';
import { ApiError, errorMessage, request } from './api';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { formatDate } from './recipeFormat';

type ValueSetSummary = { id: string; name: string; inputs: string[]; savedAt: string };

/** Named runtime values kept by the host in an encrypted file; they are fetched only when the user loads them. */
export default function RecipeValueSets({
  recipeId,
  disabled,
  currentValues,
  onLoad,
}: {
  recipeId: string;
  disabled: boolean;
  currentValues: () => Record<string, string>;
  onLoad: (values: Record<string, string>) => void;
}) {
  useLocale();
  const base = '/api/recipes/' + encodeURIComponent(recipeId) + '/value-sets';
  const [sets, setSets] = useState<ValueSetSummary[]>([]);
  const [selected, setSelected] = useState('');
  const [name, setName] = useState('');
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [unreadable, setUnreadable] = useState(false);
  const [status, setStatus] = useState('');

  function fail(cause: unknown, fallback: string) {
    setError(errorMessage(cause, fallback));
    setUnreadable(cause instanceof ApiError && cause.code === 'VALUE_SETS_UNREADABLE');
  }
  useEffect(() => {
    const controller = new AbortController();
    setSets([]);
    setSelected('');
    setError('');
    setUnreadable(false);
    request<ValueSetSummary[]>(base, { signal: controller.signal })
      .then(setSets)
      .catch(cause => {
        if (!controller.signal.aborted) fail(cause, t('Could not read saved values.'));
      });
    return () => controller.abort();
  }, [base]);

  async function run(action: () => Promise<void>, fallback: string) {
    setBusy(true);
    setError('');
    setStatus('');
    try {
      await action();
    } catch (cause) {
      fail(cause, fallback);
    } finally {
      setBusy(false);
    }
  }
  const save = () =>
    run(async () => {
      const values = Object.fromEntries(Object.entries(currentValues()).filter(([, value]) => value.trim()));
      const saved = await request<ValueSetSummary>(base, { method: 'PUT', body: JSON.stringify({ name, values }) });
      setSets(previous => [...previous.filter(set => set.id !== saved.id), saved].sort((a, b) => a.name.localeCompare(b.name)));
      setSelected(saved.id);
      setName('');
      setStatus(t('Saved "{0}".', saved.name));
    }, t('Values were not saved.'));
  const load = () =>
    run(async () => {
      const set = await request<{ name: string; values: Record<string, string> }>(base + '/' + encodeURIComponent(selected));
      onLoad(set.values);
      setStatus(t('Loaded "{0}". Check the values before generating SQL.', set.name));
    }, t('Could not read saved values.'));
  const remove = () =>
    run(async () => {
      const set = sets.find(item => item.id === selected);
      if (!set || !window.confirm(t('Delete saved values "{0}"?', set.name))) return;
      await request(base + '/' + encodeURIComponent(set.id), { method: 'DELETE' });
      setSets(previous => previous.filter(item => item.id !== set.id));
      setSelected('');
    }, t('Saved values were not deleted.'));
  const clear = () =>
    run(async () => {
      if (!window.confirm(t('Delete all saved values of this recipe? They cannot be decrypted on this account anyway.'))) return;
      await request(base, { method: 'DELETE' });
      setSets([]);
      setSelected('');
      setUnreadable(false);
    }, t('Saved values were not deleted.'));

  return (
    <section className="runtime-value-sets" aria-label={t('Saved values')}>
      <div className="runtime-value-sets-row">
        <select
          aria-label={t('Saved values')}
          value={selected}
          disabled={busy || !sets.length}
          onChange={event => setSelected(event.target.value)}
        >
          <option value="">{sets.length ? t('Choose saved values…') : t('No saved values yet')}</option>
          {sets.map(set => (
            <option key={set.id} value={set.id}>
              {set.name} · {formatDate(set.savedAt)}
            </option>
          ))}
        </select>
        <button type="button" className="secondary-button" disabled={busy || disabled || !selected} onClick={() => void load()}>
          {t('Load')}
        </button>
        <button type="button" className="text-button danger-text" disabled={busy || !selected} onClick={() => void remove()}>
          {t('Remove')}
        </button>
        <input
          type="text"
          aria-label={t('Name for the current values')}
          placeholder={t('Name, e.g. Customer A, October')}
          value={name}
          maxLength={80}
          disabled={busy || disabled}
          onChange={event => setName(event.target.value)}
        />
        <button type="button" className="secondary-button" disabled={busy || disabled || !name.trim()} onClick={() => void save()}>
          {t('Save values')}
        </button>
      </div>
      <small>{t('Encrypted for your Windows account in the program data folder. Never part of exports, backups or diagnostics.')}</small>
      {status && (
        <p className="runtime-value-sets-status" role="status">
          {status}
        </p>
      )}
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
          {unreadable && (
            <button type="button" className="secondary-button" disabled={busy} onClick={() => void clear()}>
              {t('Delete unreadable saved values')}
            </button>
          )}
        </div>
      )}
    </section>
  );
}
