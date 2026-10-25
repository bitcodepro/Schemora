import AnalysisCoverage from './AnalysisCoverage';
import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useEffect, useState } from 'react';
import { errorMessage, request } from './api';
import type { Database } from './types';

type ProjectMapping = { projectId: string; path: string; databaseId?: string };
type Mappings = { projects: ProjectMapping[]; databases: Database[] };

export default function EfMappings({
  workspaceId,
  onSaved,
  refreshKey = 0,
}: {
  workspaceId: string;
  onSaved: () => void;
  refreshKey?: number;
}) {
  useLocale();
  const [mappings, setMappings] = useState<Mappings | null>(null);
  const [choices, setChoices] = useState<Record<string, string>>({});
  const [savingId, setSavingId] = useState('');
  const [error, setError] = useState('');
  const [busy, setBusy] = useState(false);

  useEffect(() => {
    setMappings(null);
    setError('');
    if (!workspaceId) return;
    const controller = new AbortController();
    setBusy(true);
    request<Mappings>(`/api/workspaces/${encodeURIComponent(workspaceId)}/ef-mappings`, { signal: controller.signal })
      .then(loaded => {
        setMappings(loaded);
        setChoices(Object.fromEntries(loaded.projects.map(project => [project.projectId, project.databaseId || ''])));
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Could not load EF bindings.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setBusy(false);
      });
    return () => controller.abort();
  }, [workspaceId, refreshKey]);

  async function save(projectId: string) {
    const databaseId = choices[projectId];
    if (!databaseId) return;
    setSavingId(projectId);
    setError('');
    try {
      await request<unknown>(`/api/workspaces/${encodeURIComponent(workspaceId)}/ef-mappings`, {
        method: 'PUT',
        body: JSON.stringify({ projectId, databaseId }),
      });
      setMappings(previous =>
        previous
          ? {
              ...previous,
              projects: previous.projects.map(project => (project.projectId === projectId ? { ...project, databaseId } : project)),
            }
          : previous,
      );
      onSaved();
    } catch (cause) {
      setError(errorMessage(cause, t('Could not save the project binding.')));
    } finally {
      setSavingId('');
    }
  }

  return (
    <>
      <AnalysisCoverage key={workspaceId} workspaceId={workspaceId} />
      <details className="ef-mappings">
        <summary>
          {t('C# → Database · EF / Dapper')} {mappings && <span>{mappings.projects.length}</span>}
        </summary>
        <div className="ef-mapping-body">
          <p>
            {t(
              'For multiple databases, explicitly map each C# project to a database. EF mappings are analyzed as ORM relationships; SQL from Dapper is treated as observed JOINs. Dapper splitOn describes materialization and does not by itself confirm a foreign key. With a single database, the server can establish an unambiguous binding.',
            )}
          </p>
          {error && (
            <div className="alert error" role="alert">
              <p>{tMessage(error)}</p>
            </div>
          )}
          {busy ? (
            <p className="muted-text">{t('Loading projects…')}</p>
          ) : mappings?.projects.length ? (
            <div className="ef-project-list">
              {mappings.projects.map(project => (
                <div className="ef-project" key={project.projectId}>
                  <code>{project.path}</code>
                  <select
                    aria-label={t('Database for {0}', project.path)}
                    value={choices[project.projectId] || ''}
                    onChange={event => setChoices(previous => ({ ...previous, [project.projectId]: event.target.value }))}
                    disabled={Boolean(savingId)}
                  >
                    <option value="">{t('Not bound to a database')}</option>
                    {mappings.databases.map(database => (
                      <option key={database.id} value={database.id}>
                        {database.name}
                      </option>
                    ))}
                  </select>
                  <button
                    className="secondary-button"
                    disabled={Boolean(savingId) || !choices[project.projectId] || choices[project.projectId] === project.databaseId}
                    onClick={() => void save(project.projectId)}
                  >
                    {savingId === project.projectId ? t('Saving…') : t('Bind')}
                  </button>
                </div>
              ))}
            </div>
          ) : (
            <p className="muted-text">{t('The catalog contains no C# projects. Refresh the workspace to discover them.')}</p>
          )}
        </div>
      </details>
    </>
  );
}
