import { useEffect, useState } from 'react';
import { request, errorMessage } from './api';
import { t, tMessage } from './i18n';
import { useLocale } from './i18nReact';
type Settings = { projectId: string; configuration: string; targetFramework: string | null; trustedEvaluation: boolean };
type Project = { projectId: string; path: string; settings: Settings };
type Preview = {
  files: unknown[];
  referencePaths: string[];
  isComplete: boolean;
  fingerprint: string;
  diagnostics: { code: string; message: string; detail?: string }[];
};
export default function ProjectAnalysisPanel({
  workspaceId,
  onSaved,
  refreshKey = 0,
}: {
  workspaceId: string;
  onSaved: () => void;
  refreshKey?: number;
}) {
  useLocale();
  const [projects, setProjects] = useState<Project[]>([]);
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);
  const [previews, setPreviews] = useState<Record<string, Preview>>({});
  useEffect(() => {
    const controller = new AbortController();
    setPreviews({});
    request<Project[]>(`/api/workspaces/${encodeURIComponent(workspaceId)}/csharp-context`, { signal: controller.signal })
      .then(setProjects)
      .catch(e => {
        if (!controller.signal.aborted) setError(errorMessage(e, t('Unable to load C# project settings.')));
      });
    return () => controller.abort();
  }, [workspaceId, refreshKey]);
  function update(id: string, changes: Partial<Settings>) {
    setProjects(rows => rows.map(p => (p.projectId === id ? { ...p, settings: { ...p.settings, ...changes } } : p)));
    setPreviews(previous => Object.fromEntries(Object.entries(previous).filter(([key]) => key !== id)));
  }
  async function run(project: Project, save: boolean) {
    setBusy(true);
    setError('');
    try {
      const url = `/api/workspaces/${encodeURIComponent(workspaceId)}/csharp-context`;
      if (save) {
        await request(url, { method: 'PUT', body: JSON.stringify(project.settings) });
        onSaved();
      } else {
        const preview = await request<Preview>(url + '/preview', { method: 'POST', body: JSON.stringify(project.settings) });
        setPreviews(previous => ({ ...previous, [project.projectId]: preview }));
      }
    } catch (e) {
      setError(errorMessage(e, t('C# project evaluation failed.')));
    } finally {
      setBusy(false);
    }
  }
  return (
    <details className="ef-mappings product-controls">
      <summary>{t('C# project compilation context')}</summary>
      <div className="ef-mapping-body">
        <p>
          {t(
            'Select the configuration and target framework used for EF and Dapper analysis. Static evaluation never runs project code. Unsupported build logic is reported as partial.',
          )}
        </p>
        {error && <p role="alert">{tMessage(error)}</p>}
        {projects.map(project => (
          <div className="ef-project" key={project.projectId}>
            <code>{project.path}</code>
            <label>
              {t('Configuration')}
              <input
                value={project.settings.configuration}
                disabled={busy}
                onChange={e => update(project.projectId, { configuration: e.target.value })}
              />
            </label>
            <label>
              {t('Target framework')}
              <input
                placeholder="net10.0"
                value={project.settings.targetFramework || ''}
                disabled={busy}
                onChange={e => update(project.projectId, { targetFramework: e.target.value || null })}
              />
            </label>
            <label>
              <input
                type="checkbox"
                checked={project.settings.trustedEvaluation}
                disabled={busy}
                onChange={e => update(project.projectId, { trustedEvaluation: e.target.checked })}
              />
              {t(
                'Trust this project for MSBuild evaluation. Imports and property functions may execute code; no build or restore targets are requested.',
              )}
            </label>
            <button className="secondary-button" disabled={busy} onClick={() => void run(project, false)}>
              {t('Preview context')}
            </button>
            <button className="secondary-button" disabled={busy} onClick={() => void run(project, true)}>
              {t('Save context')}
            </button>
            {previews[project.projectId] && (
              <div>
                <p>
                  {t('Source files')}: {previews[project.projectId].files.length} · {t('References')}:{' '}
                  {previews[project.projectId].referencePaths.length} ·{' '}
                  {previews[project.projectId].isComplete ? t('Complete') : t('Partial')}
                </p>
                <ul>
                  {previews[project.projectId].diagnostics.map((d, i) => (
                    <li key={i}>
                      <code>{d.code}</code> {tMessage(d.message || d.detail || '')}
                    </li>
                  ))}
                </ul>
              </div>
            )}
          </div>
        ))}
      </div>
    </details>
  );
}
