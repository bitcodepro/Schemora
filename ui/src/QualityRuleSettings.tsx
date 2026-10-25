import { useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
type Policy = { rules: Record<string, string>; version: number };
type Rules = { policy: Policy; definitions: { id: string; defaultSeverity: string }[] };
export default function QualityRuleSettings({ workspaceId, onSaved }: { workspaceId: string; onSaved: () => Promise<void> }) {
  useLocale();
  const [data, setData] = useState<Rules | null>(null),
    [error, setError] = useState(''),
    [busy, setBusy] = useState(false),
    [saved, setSaved] = useState(false);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    setSaved(false);
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e, t('Workspace operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  return (
    <details className="professional-section">
      <summary>{t('Quality rule settings')}</summary>
      <p>{t('Rule severity applies only to this workspace. Turning a rule off does not change schema or relationship trust.')}</p>
      {error && <p role="alert">{tMessage(error)}</p>}
      <button
        className="secondary-button"
        disabled={busy}
        onClick={() => void run(async () => setData(await request<Rules>('/api/workspaces/' + workspaceId + '/quality/rules')))}
      >
        {t('Load quality rules')}
      </button>
      {data && (
        <>
          <div>
            {data.definitions.map(rule => (
              <label key={rule.id}>
                {rule.id}
                <select
                  value={data.policy.rules[rule.id] || rule.defaultSeverity}
                  onChange={e => {
                    const value = e.target.value;
                    setSaved(false);
                    setData(d => (d ? { ...d, policy: { ...d.policy, rules: { ...d.policy.rules, [rule.id]: value } } } : d));
                  }}
                >
                  {['off', 'info', 'warning', 'error'].map(s => (
                    <option key={s} value={s}>
                      {t(s === 'off' ? 'Rule disabled' : 'Severity: ' + s)}
                    </option>
                  ))}
                </select>
              </label>
            ))}
          </div>
          <button
            className="secondary-button"
            disabled={busy}
            onClick={() =>
              void run(async () => {
                const policy = await request<Policy>('/api/workspaces/' + workspaceId + '/quality/rules', {
                  method: 'PUT',
                  body: JSON.stringify(data.policy),
                });
                setData(d => (d ? { ...d, policy } : d));
                await onSaved();
                setSaved(true);
              })
            }
          >
            {t('Save quality rules')}
          </button>
        </>
      )}
      {saved && <p role="status">{t('Quality rules saved')}</p>}
    </details>
  );
}
