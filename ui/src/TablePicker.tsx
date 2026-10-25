import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useState } from 'react';
import { errorMessage, request } from './api';
import type { GraphTable, TablePage } from './graphModel';

export function qualifiedTableName(table: GraphTable): string {
  return `${table.databaseName}.${table.schema}.${table.name}`;
}

type Props = {
  workspaceId: string;
  databaseId?: string;
  schema?: string;
  label: string;
  value: GraphTable | null;
  onChange: (table: GraphTable | null) => void;
  emptyLabel?: string;
  disabled?: boolean;
};

export default function TablePicker({
  workspaceId,
  databaseId,
  schema,
  label,
  value,
  onChange,
  emptyLabel = t('Выберите таблицу'),
  disabled,
}: Props) {
  useLocale();
  const [query, setQuery] = useState('');
  const [page, setPage] = useState<TablePage | null>(null);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');

  useEffect(() => {
    if (!workspaceId) {
      setPage(null);
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const parameters = new URLSearchParams({ query, limit: '100', offset: '0' });
      if (databaseId) parameters.set('databaseId', databaseId);
      if (schema) parameters.set('schema', schema);
      setBusy(true);
      setError('');
      request<TablePage>(`/api/workspaces/${encodeURIComponent(workspaceId)}/tables?${parameters}`, { signal: controller.signal })
        .then(setPage)
        .catch(cause => {
          if (!controller.signal.aborted) setError(errorMessage(cause, t('Поиск таблиц недоступен.')));
        })
        .finally(() => {
          if (!controller.signal.aborted) setBusy(false);
        });
    }, 250);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [workspaceId, databaseId, schema, query]);

  const choices = page?.items || [];
  return (
    <div className="table-picker">
      <label className="field-label">
        {label}
        <input
          type="search"
          className="table-picker-search"
          aria-label={t('Поиск: {0}', label)}
          placeholder={t('Поиск по всем таблицам…')}
          value={query}
          onChange={event => setQuery(event.target.value)}
          disabled={disabled || !workspaceId}
        />
      </label>
      <select
        aria-label={label}
        value={value?.id || ''}
        disabled={disabled || !workspaceId}
        onChange={event =>
          onChange(choices.find(table => table.id === event.target.value) || (value?.id === event.target.value ? value : null))
        }
      >
        <option value="">{busy ? t('Поиск…') : emptyLabel}</option>
        {value && !choices.some(table => table.id === value.id) && <option value={value.id}>{qualifiedTableName(value)}</option>}
        {choices.map(table => (
          <option key={table.id} value={table.id}>
            {qualifiedTableName(table)}
          </option>
        ))}
      </select>
      <div className="table-picker-note">
        {error ? (
          <span role="alert">{tMessage(error)}</span>
        ) : page ? (
          t('{0} найдено{1}', page.total, page.total > choices.length ? t(' · показаны первые {0}; уточните поиск', choices.length) : '')
        ) : (
          t('Серверный поиск по каталогу')
        )}
      </div>
    </div>
  );
}
