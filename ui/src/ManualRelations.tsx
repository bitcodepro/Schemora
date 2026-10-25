import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useRef, useState } from 'react';
import { errorMessage, request } from './api';
import type { GraphTable } from './graphModel';
import TablePicker from './TablePicker';
import OrderedColumnPairs from './OrderedColumnPairs';
import { pairColumns, pairIssues } from './relationEditorModel';
import type { ColumnPair } from './relationEditorModel';
import './enhancements.css';

type ManualRelation = {
  id: string;
  workspaceId: string;
  version: number;
  name: string;
  fromTableId: string;
  toTableId: string;
  fromColumns: string[];
  toColumns: string[];
  reason: string;
  createdAt: string;
  updatedAt: string;
  relationId: string;
  status: string;
  canUseForJoin: boolean;
};
const states: Record<string, string> = {
  confirmed: 'Подтверждена',
  candidate: 'Проверьте конфликт',
  stale: 'Схема изменилась',
  conflict: 'Конфликт',
  rejected: 'Отклонена',
};
export default function ManualRelations({ workspaceId, onChanged }: { workspaceId: string; onChanged: () => void }) {
  useLocale();
  const [records, setRecords] = useState<ManualRelation[]>([]);
  const [editing, setEditing] = useState(false);
  const [selected, setSelected] = useState<ManualRelation | null>(null);
  const [fromTable, setFromTable] = useState<GraphTable | null>(null);
  const [toTable, setToTable] = useState<GraphTable | null>(null);
  const [pairs, setPairs] = useState<ColumnPair[]>([{ fromColumn: '', toColumn: '' }]);
  const [name, setName] = useState('');
  const [reason, setReason] = useState('');
  const [busy, setBusy] = useState(false);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [notice, setNotice] = useState('');
  const [deleteId, setDeleteId] = useState('');
  const currentWorkspace = useRef(workspaceId);
  currentWorkspace.current = workspaceId;
  const revision = useRef(0);
  const base = `/api/workspaces/${encodeURIComponent(workspaceId)}/manual-relations`;
  useEffect(() => {
    setRecords([]);
    setBusy(false);
    setEditing(false);
    setSelected(null);
    setDeleteId('');
    setError('');
    setNotice('');
    revision.current++;
    if (!workspaceId) return;
    const controller = new AbortController();
    setLoading(true);
    void request<ManualRelation[]>(base, { signal: controller.signal })
      .then(setRecords)
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Ручные связи недоступны.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setLoading(false);
      });
    return () => controller.abort();
  }, [workspaceId]);
  function create() {
    revision.current++;
    setSelected(null);
    setFromTable(null);
    setToTable(null);
    setPairs([{ fromColumn: '', toColumn: '' }]);
    setName('');
    setReason('');
    setEditing(true);
    setDeleteId('');
    setError('');
    setNotice('');
  }
  async function edit(record: ManualRelation) {
    const requestRevision = ++revision.current;
    setLoading(true);
    setError('');
    setDeleteId('');
    try {
      const [from, to] = await Promise.all([
        request<GraphTable>(`/api/workspaces/${workspaceId}/tables/${encodeURIComponent(record.fromTableId)}`),
        request<GraphTable>(`/api/workspaces/${workspaceId}/tables/${encodeURIComponent(record.toTableId)}`),
      ]);
      if (revision.current !== requestRevision) return;
      setSelected(record);
      setFromTable(from);
      setToTable(to);
      setPairs(record.fromColumns.map((column, index) => ({ fromColumn: column, toColumn: record.toColumns[index] || '' })));
      setName(record.name);
      setReason(record.reason);
      setEditing(true);
      setNotice('');
    } catch (cause) {
      if (revision.current === requestRevision)
        setError(errorMessage(cause, t('Не удалось открыть связь. Возможно, одна из таблиц удалена из текущей схемы.')));
    } finally {
      if (revision.current === requestRevision) setLoading(false);
    }
  }
  const issues = pairIssues(pairs, fromTable, toTable);
  async function save() {
    if (issues.length || !name.trim() || !reason.trim() || busy) return;
    const target = workspaceId;
    setBusy(true);
    setError('');
    try {
      const saved = await request<ManualRelation>(selected ? `${base}/${selected.id}` : base, {
        method: selected ? 'PUT' : 'POST',
        body: JSON.stringify({
          name: name.trim(),
          fromTableId: fromTable!.id,
          toTableId: toTable!.id,
          ...pairColumns(pairs),
          reason: reason.trim(),
          ...(selected ? { expectedVersion: selected.version } : {}),
        }),
      });
      if (currentWorkspace.current !== target) return;
      setRecords(previous => [saved, ...previous.filter(record => record.id !== saved.id)]);
      setSelected(saved);
      setEditing(false);
      setNotice(
        saved.canUseForJoin
          ? t('Связь сохранена. Её можно использовать в маршрутах и рецептах.')
          : t('Связь сохранена, но не разрешена для JOIN. Откройте доказательства в графе и проверьте конфликт.'),
      );
      onChanged();
    } catch (cause) {
      if (currentWorkspace.current === target) setError(errorMessage(cause, t('Связь не сохранена.')));
    } finally {
      if (currentWorkspace.current === target) setBusy(false);
    }
  }
  async function remove(record: ManualRelation) {
    const target = workspaceId;
    setBusy(true);
    setError('');
    try {
      await request(`${base}/${record.id}?expectedVersion=${record.version}`, { method: 'DELETE' });
      if (currentWorkspace.current !== target) return;
      setRecords(previous => previous.filter(item => item.id !== record.id));
      setDeleteId('');
      if (selected?.id === record.id) {
        setSelected(null);
        setEditing(false);
      }
      setNotice(t('Ручная связь удалена. Маршруты будут проверены заново.'));
      onChanged();
    } catch (cause) {
      if (currentWorkspace.current === target) setError(errorMessage(cause, t('Связь не удалена.')));
    } finally {
      if (currentWorkspace.current === target) setBusy(false);
    }
  }
  return (
    <section className="enhancement-card manual-relations">
      <div className="section-heading">
        <div>
          <h2>{t('Ручные связи')}</h2>
          <p className="muted-text">{t('Укажите известное соответствие колонок, даже если в схеме нет внешнего ключа.')}</p>
        </div>
        <button className="secondary-button" disabled={busy || loading || !workspaceId} onClick={create}>
          {t('+ Новая ручная связь')}
        </button>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      {notice && (
        <p className="recipe-notice" role="status">
          {tMessage(notice)}
        </p>
      )}
      {loading && <p className="muted-text">{t('Загрузка связей…')}</p>}
      {records.length > 0 && (
        <div className="manual-relation-list">
          {records.map(record => (
            <article className="manual-relation-item" key={record.id}>
              <div>
                <strong>{record.name}</strong>
                <small>
                  {t(states[record.status]) || record.status} · v{record.version} ·{' '}
                  {record.canUseForJoin ? t('Доступна для JOIN') : t('Не используется в JOIN')}
                </small>
                <code>
                  {record.fromColumns.join(', ')} → {record.toColumns.join(', ')}
                </code>
                <p className="muted-text">{record.reason}</p>
              </div>
              <button className="text-button" disabled={busy || loading} onClick={() => void edit(record)}>
                {t('Изменить')}
              </button>
              <button className="text-button danger-text" disabled={busy || loading} onClick={() => setDeleteId(record.id)}>
                {t('Удалить связь')}
              </button>
              {deleteId === record.id && (
                <div className="manual-delete-confirm">
                  <p>
                    {t('Удалить «')}
                    {record.name}
                    {t('»? Рецепты с этой связью могут потребовать проверки.')}
                  </p>
                  <div className="enhancement-actions">
                    <button className="secondary-button" disabled={busy} onClick={() => void remove(record)}>
                      {t('Удалить окончательно')}
                    </button>
                    <button className="text-button" disabled={busy} onClick={() => setDeleteId('')}>
                      {t('Оставить связь')}
                    </button>
                  </div>
                </div>
              )}
            </article>
          ))}
        </div>
      )}
      {!records.length && !editing && !loading && (
        <p className="muted-text">{t('Ручных связей пока нет. Начните с таблиц и колонок, соответствие которых вам известно.')}</p>
      )}
      {editing && (
        <div className="manual-relation-form">
          <div className="enhancement-grid">
            <TablePicker
              workspaceId={workspaceId}
              label={t('Ручная связь: исходная таблица')}
              value={fromTable}
              disabled={busy}
              onChange={table => {
                setFromTable(table);
                setPairs(previous => previous.map(pair => ({ ...pair, fromColumn: '' })));
              }}
            />
            <TablePicker
              workspaceId={workspaceId}
              label={t('Ручная связь: целевая таблица')}
              value={toTable}
              disabled={busy}
              onChange={table => {
                setToTable(table);
                setPairs(previous => previous.map(pair => ({ ...pair, toColumn: '' })));
              }}
            />
          </div>
          <label className="manual-relation-name">
            <span className="field-label">{t('Название связи')}</span>
            <input
              aria-label={t('Название ручной связи')}
              value={name}
              maxLength={160}
              disabled={busy}
              onChange={event => setName(event.target.value)}
              placeholder={t('Например, документы → заказы')}
            />
          </label>
          <OrderedColumnPairs fromTable={fromTable} toTable={toTable} value={pairs} onChange={setPairs} disabled={busy} />
          <label className="repair-reason">
            <span className="field-label">{t('Почему эти колонки соответствуют друг другу')}</span>
            <textarea
              aria-label={t('Основание ручной связи')}
              value={reason}
              maxLength={2000}
              rows={3}
              disabled={busy}
              onChange={event => setReason(event.target.value)}
              placeholder={t('Укажите источник знания о связи и важные условия…')}
            />
          </label>
          {issues.length > 0 && (
            <div className="manual-relation-diagnostics">
              {issues.map(issue => (
                <p key={issue}>{issue}</p>
              ))}
            </div>
          )}
          <div className="enhancement-actions">
            <button
              className="primary-button"
              disabled={busy || issues.length > 0 || !name.trim() || !reason.trim()}
              onClick={() => void save()}
            >
              {busy ? t('Сохранение…') : selected ? t('Сохранить изменения связи') : t('Создать ручную связь')}
            </button>
            <button
              className="text-button"
              disabled={busy}
              onClick={() => {
                revision.current++;
                setEditing(false);
              }}
            >
              {t('Закрыть редактор')}
            </button>
            <p className="muted-text">{t('Ручная связь не меняет SQL-проект. Сервер проверяет типы, порядок колонок и конфликты.')}</p>
          </div>
        </div>
      )}
    </section>
  );
}
