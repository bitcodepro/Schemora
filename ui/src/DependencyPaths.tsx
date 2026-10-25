import { useEffect, useState } from 'react';
import { Background, Controls, ReactFlow } from '@xyflow/react';
import { request, errorMessage } from './api';
import { t } from './i18n';
import { useLocale } from './i18nReact';
type Node = { id: string; name: string; kind: string; depth: number };
type Page = {
  nodes: Node[];
  edges: { id: string; source: string; target: string; explanation: string; confidence: number }[];
  total: number;
  offset: number;
  limit: number;
  partial: boolean;
};
export default function DependencyPaths({
  workspace,
  objectId,
  onRecipe,
}: {
  workspace: string;
  objectId: string;
  onRecipe: (workspace: string, recipe: string) => void;
}) {
  useLocale();
  const [query, setQuery] = useState(''),
    [offset, setOffset] = useState(0),
    [data, setData] = useState<Page | null>(null),
    [error, setError] = useState('');
  useEffect(() => {
    setOffset(0);
    setQuery('');
  }, [workspace, objectId]);
  useEffect(() => {
    const c = new AbortController();
    setData(null);
    setError('');
    const timeout = setTimeout(
      () =>
        void request<Page>(
          '/api/workspaces/' +
            workspace +
            '/dependency-paths?' +
            new URLSearchParams({ objectId, query, offset: String(offset), limit: '25' }),
          { signal: c.signal },
        )
          .then(setData)
          .catch(e => {
            if (!c.signal.aborted) setError(errorMessage(e, t('Unable to load workspace navigation.')));
          }),
      150,
    );
    return () => {
      clearTimeout(timeout);
      c.abort();
    };
  }, [workspace, objectId, query, offset]);
  const nodes = data?.nodes || [],
    recipes = nodes.filter(n => n.kind === 'recipe');
  return (
    <section aria-label={t('Recipe dependency graph')}>
      <h3>{t('Recipe dependency graph')}</h3>
      <p>{t('Arrows show impact from a referenced object to its caller. One shortest path is shown per recipe.')}</p>
      <label>
        {t('Search dependent recipes')}
        <input
          type="search"
          value={query}
          onChange={e => {
            setQuery(e.target.value);
            setOffset(0);
          }}
        />
      </label>
      {error && <p role="alert">{error}</p>}
      {data && (
        <>
          <p role="status">
            {t('Showing {0} of {1} recipes', recipes.length, data.total)} · {t('Objects per page')}: {data.limit}
          </p>
          {data.partial && (
            <p role="alert">{t('Dependency results are partial: unresolved references or the traversal limit were reached.')}</p>
          )}
          <div style={{ height: 450 }}>
            <ReactFlow
              nodes={nodes.map((n, i) => ({
                id: n.id,
                position: { x: n.depth * 280, y: i * 80 },
                data: { label: n.name + ' · ' + t('Kind: ' + n.kind) },
              }))}
              edges={data.edges.map(e => ({
                ...e,
                label:
                  t('Dependency: ' + e.explanation.split(':')[0]) +
                  (e.explanation.includes(':') ? ':' + e.explanation.split(':').slice(1).join(':') : ''),
              }))}
              onNodeClick={(_, n) => {
                if (n.id.startsWith('recipe:')) onRecipe(workspace, n.id.slice(7));
              }}
              fitView
              nodesConnectable={false}
              nodesDraggable={false}
            >
              <Background />
              <Controls />
            </ReactFlow>
          </div>
          <ul>
            {data.edges.map(e => (
              <li key={e.id}>
                {nodes.find(n => n.id === e.source)?.name} → {nodes.find(n => n.id === e.target)?.name}:{' '}
                {t('Dependency: ' + e.explanation.split(':')[0])} · {Math.round(e.confidence * 100)}%
              </li>
            ))}
          </ul>
          {recipes.map(r => (
            <button className="text-button" key={r.id} onClick={() => onRecipe(workspace, r.id.slice(7))}>
              {r.name}
            </button>
          ))}
          {!data.total && <p>{t('No dependent recipes.')}</p>}
          <div>
            <button className="secondary-button" disabled={!offset} onClick={() => setOffset(Math.max(0, offset - 25))}>
              {t('Previous page')}
            </button>
            <span>
              {data.total ? offset + 1 : 0}–{Math.min(offset + 25, data.total)} / {data.total}
            </span>
            <button className="secondary-button" disabled={offset + 25 >= data.total} onClick={() => setOffset(offset + 25)}>
              {t('Next page')}
            </button>
          </div>
        </>
      )}
    </section>
  );
}
