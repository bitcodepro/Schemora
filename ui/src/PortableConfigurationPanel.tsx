import { useEffect, useState } from 'react';
import { errorMessage, request } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
import type { Catalog } from './types';

type Bundle = {
  formatVersion: number;
  name: string;
  databases: { id: string; name: string }[];
  projects?: { id: string; name: string }[];
  recipes: unknown[];
};
type Preview = { revision: string; canImport: boolean; items: { kind: string; id: string; status: string; message: string }[] };
type Pending = {
  id: string;
  assertion: { name: string; kind: string; decision?: string; reason: string; fromColumns: string[]; toColumns: string[] };
  fromTableId: string;
  toTableId: string;
};
export default function PortableConfigurationPanel({ workspaceId }: { workspaceId: string }) {
  useLocale();
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [projects, setProjects] = useState<{ projectId: string; path: string }[]>([]);
  const [manifest, setManifest] = useState<Bundle | null>(null);
  const [databaseMappings, setDatabases] = useState<Record<string, string>>({});
  const [projectMappings, setProjectsMap] = useState<Record<string, string>>({});
  const [preview, setPreview] = useState<Preview | null>(null);
  const [pending, setPending] = useState<Pending[]>([]);
  const [error, setError] = useState('');
  const [result, setResult] = useState('');
  const [busy, setBusy] = useState(false);
  const base = `/api/workspaces/${workspaceId}/configuration`;
  useEffect(() => {
    setManifest(null);
    setPreview(null);
    setCatalog(null);
    setPending([]);
    setDatabases({});
    setProjectsMap({});
    if (!workspaceId) return;
    const controller = new AbortController();
    void Promise.all([
      request<Catalog>(`/api/workspaces/${workspaceId}/catalog`, { signal: controller.signal }),
      request<{ projects: { projectId: string; path: string }[] }>(`/api/workspaces/${workspaceId}/ef-mappings`, {
        signal: controller.signal,
      }),
      request<Pending[]>(`${base}/pending`, { signal: controller.signal }),
    ])
      .then(([c, p, list]) => {
        setCatalog(c);
        setProjects(p.projects);
        setPending(list);
      })
      .catch(e => {
        if (!controller.signal.aborted) setError(errorMessage(e, t('Could not load portable configuration.')));
      });
    return () => controller.abort();
  }, [workspaceId, base]);
  async function run(action: () => Promise<void>) {
    setBusy(true);
    setError('');
    try {
      await action();
    } catch (e) {
      setError(errorMessage(e, t('Configuration operation failed.')));
    } finally {
      setBusy(false);
    }
  }
  const body = () => JSON.stringify({ manifest, databaseMappings, projectMappings, previewRevision: preview?.revision });
  return (
    <section className="settings-card product-controls" id="settings-configuration">
      <h2>{t('Portable workspace configuration')}</h2>
      <p>
        {t(
          'Export recipes, manual composite relationships, decisions, project mappings and database aliases. Imported relationships and decisions require individual review before they can affect joins.',
        )}
      </p>
      {error && (
        <p role="alert" className="job-error">
          {error}
        </p>
      )}
      {result && <p role="status">{result}</p>}
      <button
        className="secondary-button"
        disabled={!workspaceId || busy}
        onClick={() =>
          void run(async () => {
            const bundle = await request(`${base}/v2`);
            const url = URL.createObjectURL(new Blob([JSON.stringify(bundle, null, 2)], { type: 'application/json' }));
            const a = document.createElement('a');
            a.href = url;
            a.download = 'schemora-configuration-v2.json';
            a.click();
            setTimeout(() => URL.revokeObjectURL(url), 1000);
          })
        }
      >
        {t('Export configuration v2')}
      </button>
      <label className="secondary-button file-button">
        {t('Choose configuration JSON')}
        <input
          type="file"
          accept=".json,application/json"
          disabled={!workspaceId || busy}
          onChange={e => {
            const file = e.target.files?.[0];
            e.target.value = '';
            setPreview(null);
            setManifest(null);
            setResult('');
            setDatabases({});
            setProjectsMap({});
            if (file)
              void run(async () => {
                if (file.size > 8 * 1024 * 1024) throw new Error(t('Configuration exceeds 8 MiB.'));
                const value = JSON.parse(await file.text()) as Bundle;
                if (![1, 2].includes(value.formatVersion) || !Array.isArray(value.databases) || !Array.isArray(value.recipes))
                  throw new Error(t('Expected a Schemora v1 or v2 configuration.'));
                setManifest(value);
              });
          }}
        />
      </label>
      {manifest && (
        <div className="configuration-preview">
          <h3>{manifest.name}</h3>
          <p>
            {t(
              'Map each source explicitly. Existing roots and excludes are preserved. Conflicting aliases and project mappings must be resolved before import.',
            )}
          </p>
          {manifest.databases.map(db => (
            <label key={db.id}>
              {db.name}
              <select
                disabled={busy}
                value={databaseMappings[db.id] || ''}
                onChange={e => {
                  setDatabases({ ...databaseMappings, [db.id]: e.target.value });
                  setPreview(null);
                }}
              >
                <option value="">{t('Choose target database')}</option>
                {catalog?.databases.map(target => (
                  <option key={target.id} value={target.id}>
                    {target.name}
                  </option>
                ))}
              </select>
            </label>
          ))}
          {manifest.projects?.map(p => (
            <label key={p.id}>
              {p.name}
              <select
                disabled={busy}
                value={projectMappings[p.id] || ''}
                onChange={e => {
                  setProjectsMap({ ...projectMappings, [p.id]: e.target.value });
                  setPreview(null);
                }}
              >
                <option value="">{t('Choose target project')}</option>
                {projects.map(target => (
                  <option key={target.projectId} value={target.projectId}>
                    {target.path}
                  </option>
                ))}
              </select>
            </label>
          ))}
          <button
            className="secondary-button"
            disabled={busy}
            onClick={() => void run(async () => setPreview(await request<Preview>(`${base}/preview`, { method: 'POST', body: body() })))}
          >
            {t('Preview import')}
          </button>
          {preview && (
            <>
              <ul>
                {preview.items.map((item, i) => (
                  <li key={i}>
                    {item.kind}: {item.id} — {t(item.status)}: {tMessage(item.message)}
                  </li>
                ))}
              </ul>
              <button
                className="secondary-button"
                disabled={busy || !preview.canImport}
                onClick={() =>
                  void run(async () => {
                    const response = await request<{ results: unknown[]; pendingReview: number }>(`${base}/import-v2`, {
                      method: 'POST',
                      body: body(),
                    });
                    setResult(JSON.stringify(response, null, 2));
                    setPreview(null);
                    setManifest(null);
                    setPending(await request<Pending[]>(`${base}/pending`));
                  })
                }
              >
                {t('Import reviewed configuration')}
              </button>
            </>
          )}
        </div>
      )}
      {pending.length > 0 && (
        <div>
          <h3>{t('Relationships awaiting individual review')}</h3>
          {pending.map(item => (
            <article key={item.id}>
              <strong>
                {item.assertion.name} · {item.assertion.kind} {item.assertion.decision}
              </strong>
              <p>
                {item.fromTableId} → {item.toTableId}
              </p>
              <p>{item.assertion.fromColumns.map((c, i) => `${c} → ${item.assertion.toColumns[i]}`).join(', ')}</p>
              <p>{item.assertion.reason}</p>
              {[true, false].map(apply => (
                <button
                  key={String(apply)}
                  disabled={busy}
                  onClick={() =>
                    void run(async () => {
                      await request(`${base}/pending/${item.id}`, { method: 'POST', body: JSON.stringify({ apply }) });
                      setPending(await request<Pending[]>(`${base}/pending`));
                    })
                  }
                >
                  {apply ? t('Confirm against current schema') : t('Discard imported assertion')}
                </button>
              ))}
            </article>
          ))}
        </div>
      )}
    </section>
  );
}
