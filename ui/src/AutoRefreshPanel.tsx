import { localeTag, t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useRef, useState } from 'react';
import { errorMessage, request } from './api';
import './enhancements.css';
type AutoSettings = { enabled: boolean; debounceSeconds: number; reconcileMinutes: number };
type AutoRefresh = {
  settings: AutoSettings;
  status: {
    state: string;
    watcherCount: number;
    pending: boolean;
    lastEventAt?: string;
    lastQueuedAt?: string;
    lastJobId?: string;
    diagnostics: (string | { severity?: string; code?: string; message: string })[];
  };
};
const states: Record<string, string> = {
  disabled: 'Выключено',
  watching: 'Следим за исходниками',
  active: 'Следим за исходниками',
  idle: 'Ожидаем изменений',
  pending: 'Изменения ожидают анализа',
  running: 'Обновление модели',
  scanning: 'Обновление модели',
  debouncing: 'Ожидаем завершения правок',
  waiting: 'Ожидаем изменений',
  error: 'Требует внимания',
  unavailable: 'Наблюдение недоступно',
  degraded: 'Наблюдение с ограничениями',
};
const time = (value?: string) => (value ? new Date(value).toLocaleTimeString(localeTag()) : t('ещё не было'));
export default function AutoRefreshPanel({ workspaceId, onJob }: { workspaceId: string; onJob: (jobId: string) => void }) {
  useLocale();
  const [data, setData] = useState<AutoRefresh | null>(null);
  const [debounce, setDebounce] = useState(3);
  const [reconcile, setReconcile] = useState(5);
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const callback = useRef(onJob);
  callback.current = onJob;
  const lastJob = useRef('');
  const current = useRef(workspaceId);
  current.current = workspaceId;
  const dirty = useRef(false);
  const base = `/api/workspaces/${workspaceId}/auto-refresh`;
  function apply(loaded: AutoRefresh) {
    setData(loaded);
    if (!dirty.current) {
      setDebounce(loaded.settings.debounceSeconds);
      setReconcile(loaded.settings.reconcileMinutes);
    }
    if (loaded.status.lastJobId && loaded.status.lastJobId !== lastJob.current) {
      lastJob.current = loaded.status.lastJobId;
      callback.current(loaded.status.lastJobId);
    }
  }
  useEffect(() => {
    setData(null);
    setBusy(false);
    setError('');
    dirty.current = false;
    lastJob.current = '';
    if (!workspaceId) return;
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout>;
    async function poll() {
      try {
        const loaded = await request<AutoRefresh>(base, { signal: controller.signal });
        if (controller.signal.aborted) return;
        apply(loaded);
        setError('');
        timer = setTimeout(() => void poll(), loaded.settings.enabled ? 2500 : 10000);
      } catch (cause) {
        if (!controller.signal.aborted) {
          setError(errorMessage(cause, t('Автообновление недоступно.')));
          timer = setTimeout(() => void poll(), 10000);
        }
      }
    }
    void poll();
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [workspaceId, refresh]);
  async function save(enabled: boolean) {
    if (busy || !data) return;
    setBusy(true);
    setError('');
    const target = workspaceId;
    const previous = data;
    setData({ ...data, settings: { ...data.settings, enabled } });
    try {
      const loaded = await request<AutoRefresh>(base, {
        method: 'PUT',
        body: JSON.stringify({ enabled, debounceSeconds: debounce, reconcileMinutes: reconcile }),
      });
      if (current.current !== target) return;
      dirty.current = false;
      apply(loaded);
    } catch (cause) {
      if (current.current === target) {
        setData(previous);
        setError(errorMessage(cause, t('Настройки автообновления не сохранены.')));
      }
    } finally {
      if (current.current === target) setBusy(false);
    }
  }
  return (
    <section className="auto-refresh-panel" aria-label={t('Автообновление исходников')}>
      <div className="auto-refresh-heading">
        <div>
          <h3>{t('Автообновление')}</h3>
          <p>{t('Отслеживает изменения в корневых папках и обновляет схему без сборки проектов.')}</p>
        </div>
        <div className="auto-refresh-controls">
          <label className="auto-refresh-toggle">
            <input
              type="checkbox"
              aria-label={t('Включить автообновление')}
              checked={data?.settings.enabled || false}
              disabled={!data || busy}
              onChange={event => void save(event.target.checked)}
            />
            <span>{data?.settings.enabled ? t('Включено') : t('Выключено')}</span>
          </label>
          <details>
            <summary className="text-button">{t('Параметры')}</summary>
            <div className="enhancement-grid">
              <label>
                <span className="field-label">{t('Пауза после изменения')}</span>
                <select
                  aria-label={t('Пауза автообновления')}
                  value={debounce}
                  disabled={!data || busy}
                  onChange={event => {
                    dirty.current = true;
                    setDebounce(Number(event.target.value));
                  }}
                >
                  {[...new Set([2, 3, 5, 10, 30, 60, debounce])]
                    .sort((a, b) => a - b)
                    .map(value => (
                      <option value={value} key={value}>
                        {value} {t('сек.')}
                      </option>
                    ))}
                </select>
              </label>
              <label>
                <span className="field-label">{t('Повторная проверка папок')}</span>
                <select
                  aria-label={t('Период проверки папок')}
                  value={reconcile}
                  disabled={!data || busy}
                  onChange={event => {
                    dirty.current = true;
                    setReconcile(Number(event.target.value));
                  }}
                >
                  {[...new Set([1, 5, 10, 30, 60, reconcile])]
                    .sort((a, b) => a - b)
                    .map(value => (
                      <option value={value} key={value}>
                        {value} {t('мин.')}
                      </option>
                    ))}
                </select>
              </label>
              <button className="secondary-button" disabled={!data || busy} onClick={() => void save(data!.settings.enabled)}>
                {t('Сохранить параметры')}
              </button>
            </div>
          </details>
        </div>
      </div>
      {data && (
        <div className="auto-refresh-status" role="status">
          <span>{t(states[data.status.state]) || data.status.state}</span>
          <span>
            {t('Наблюдаемых папок:')} {data.status.watcherCount}
          </span>
          <span>
            {t('Изменение:')} {time(data.status.lastEventAt)}
          </span>
          <span>
            {t('Анализ:')} {time(data.status.lastQueuedAt)}
          </span>
          {data.status.pending && <span>{t('Есть изменения для следующего анализа')}</span>}
        </div>
      )}
      {data?.status.diagnostics?.map((diagnostic, index) => (
        <p className="object-warning" key={index}>
          {typeof diagnostic === 'string' ? (
            tMessage(diagnostic)
          ) : (
            <>
              {diagnostic.code && `${diagnostic.code}: `}
              {tMessage(diagnostic.message)}
            </>
          )}
        </p>
      ))}
      {error && (
        <p className="job-error" role="alert">
          {tMessage(error)}{' '}
          <button className="text-button" onClick={() => setRefresh(value => value + 1)}>
            {t('Повторить')}
          </button>
        </p>
      )}
    </section>
  );
}
