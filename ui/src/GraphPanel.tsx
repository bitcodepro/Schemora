import { t, tMessage } from './i18n.ts';
import { useLocale } from './i18nReact';
import { memo, useEffect, useMemo, useRef, useState } from 'react';
import type { CSSProperties } from 'react';
import {
  Background,
  BackgroundVariant,
  Controls,
  getNodesBounds,
  Handle,
  MarkerType,
  MiniMap,
  Position,
  ReactFlow,
  useNodesState,
} from '@xyflow/react';
import { toPng, toSvg } from 'html-to-image';
import type { Edge, Node, NodeProps, ReactFlowInstance } from '@xyflow/react';
import '@xyflow/react/dist/style.css';
import './relationships.css';
import { errorMessage, request } from './api';
import { useWorkspaces } from './workspaceContext';
import { updateRouteParams } from './route';
import type { Catalog } from './types';
import {
  kindColors,
  kindLabels,
  layoutTables,
  readPositions,
  relationAppearance,
  relationKinds,
  retainedRelations,
  schemaColors,
  visibleColumns,
  statusLabels,
} from './graphModel';
import type { GraphPositions, GraphTable, JoinPath, LogicalRelation, RelationKind, WorkspaceGraph } from './graphModel';
import TablePicker, { qualifiedTableName } from './TablePicker';
import EfMappings from './EfMappings';
import ProjectAnalysisPanel from './ProjectAnalysisPanel';
import SourceLocation from './SourceLocation';
import DatabaseAliases from './DatabaseAliases';
import ManualRelations from './ManualRelations';
import type { RecipeSeed } from './recipeTypes';

type TableNode = Node<
  { table: GraphTable; focused: boolean; color?: string; compact: boolean; linked: string[]; active: string[] },
  'schemaTable'
>;

function KeyIcon() {
  return (
    <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true">
      <circle cx="5" cy="8" r="3.2" fill="none" stroke="currentColor" strokeWidth="1.7" />
      <path d="M8.2 8H15M12.5 8v3M14.6 8v2.2" fill="none" stroke="currentColor" strokeWidth="1.7" strokeLinecap="round" />
    </svg>
  );
}

function LinkIcon() {
  return (
    <svg viewBox="0 0 16 16" width="13" height="13" aria-hidden="true">
      <path
        d="M6.6 9.4l2.8-2.8M7.3 4.9l1.1-1.1a2.6 2.6 0 013.7 3.7L11 8.6M8.7 11.1l-1.1 1.1a2.6 2.6 0 01-3.7-3.7L5 7.4"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.6"
        strokeLinecap="round"
      />
    </svg>
  );
}

const SchemaTableNode = memo(function SchemaTableNode({ data, selected }: NodeProps<TableNode>) {
  useLocale();
  const table = data.table;
  const linked = new Set(data.linked);
  const active = new Set(data.active);
  const shown = visibleColumns(table, data.linked);
  return (
    <div
      className={`flow-table-card ${selected ? 'selected' : ''} ${data.focused ? 'focused' : ''} ${data.compact ? 'compact' : ''} ${data.color ? 'by-schema' : ''}`}
      style={data.color ? ({ '--schema-color': data.color } as CSSProperties) : undefined}
    >
      <Handle type="target" id="target-left" position={Position.Left} />
      <Handle type="target" id="target-right" position={Position.Right} />
      <div className="flow-table-database">{table.databaseName}</div>
      <div className="flow-table-name">
        <span>{table.schema}.</span>
        <strong>{table.name}</strong>
      </div>
      {!data.compact && (
        <>
          <div className="flow-table-columns">
            {shown.map(column => {
              const key = table.primaryKey.includes(column.name);
              const link = linked.has(column.name);
              return (
                <div
                  key={column.name}
                  className={(link ? 'flow-linked' : '') + (active.has(column.name) ? ' flow-active' : '')}
                  title={[key ? t('Primary key') : '', link ? t('Joins another table') : ''].filter(Boolean).join(' · ') || undefined}
                >
                  <span className={key ? 'flow-pk' : link ? 'flow-fk' : ''}>{key ? <KeyIcon /> : link ? <LinkIcon /> : '·'}</span>
                  <code>{column.name}</code>
                  <small>{column.dataType}</small>
                  {/* Relations attach to the row of the column they join on. */}
                  {link && (
                    <>
                      <Handle type="target" id={`col:${column.name}:target-left`} position={Position.Left} />
                      <Handle type="target" id={`col:${column.name}:target-right`} position={Position.Right} />
                      <Handle type="source" id={`col:${column.name}:source-right`} position={Position.Right} />
                      <Handle type="source" id={`col:${column.name}:source-left`} position={Position.Left} />
                    </>
                  )}
                </div>
              );
            })}
          </div>
          <div className="flow-table-count">
            {table.columns.length} {t('columns')}
            {table.columns.length > shown.length && (
              <span>
                {t('more')} {table.columns.length - shown.length}
              </span>
            )}
          </div>
        </>
      )}
      <Handle type="source" id="source-right" position={Position.Right} />
      <Handle type="source" id="source-left" position={Position.Left} />
    </div>
  );
});
const nodeTypes = { schemaTable: SchemaTableNode };

type GraphView = { bySchema: boolean; compact: boolean };
const graphViewKey = 'schemora.graph-view';
function readGraphView(): GraphView {
  try {
    const stored = JSON.parse(localStorage.getItem(graphViewKey) || '{}') as Partial<GraphView>;
    return { bySchema: stored.bySchema === true, compact: stored.compact === true };
  } catch {
    return { bySchema: false, compact: false };
  }
}

/** A role token's current value; the graph canvas takes colours as props instead of from the stylesheets. */
function token(name: string) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

function currentTheme(): 'light' | 'dark' {
  return document.documentElement.dataset.theme === 'light' ? 'light' : 'dark';
}
function useTheme() {
  const [theme, setTheme] = useState(currentTheme);
  useEffect(() => {
    const update = () => setTheme(currentTheme());
    window.addEventListener('schemora-theme', update);
    return () => window.removeEventListener('schemora-theme', update);
  }, []);
  return theme;
}

export default function GraphPanel({
  initialDatabaseId = '',
  onCreateRecipe,
}: {
  initialDatabaseId?: string;
  onCreateRecipe?: (seed: RecipeSeed) => void;
}) {
  const locale = useLocale();
  const { workspaceId } = useWorkspaces();
  const workspaceRef = useRef(workspaceId);
  workspaceRef.current = workspaceId;
  const [catalog, setCatalog] = useState<Catalog | null>(null);
  const [databaseId, setDatabaseId] = useState('');
  useEffect(() => {
    if (workspaceId && catalog?.workspace.id === workspaceId) updateRouteParams('graph', { ws: workspaceId, db: databaseId });
  }, [workspaceId, databaseId, catalog]);
  const [schema, setSchema] = useState('');
  const [query, setQuery] = useState('');
  const [selectedKinds, setSelectedKinds] = useState<RelationKind[]>(relationKinds);
  const [minimumConfidence, setMinimumConfidence] = useState(0);
  const [focusTable, setFocusTable] = useState<GraphTable | null>(null);
  const [hops, setHops] = useState(1);
  const [graph, setGraph] = useState<WorkspaceGraph | null>(null);
  const [relations, setRelations] = useState<LogicalRelation[]>([]);
  const [relationQuery, setRelationQuery] = useState('');
  const [relationOffset, setRelationOffset] = useState(0);
  const [relationTotal, setRelationTotal] = useState(0);
  const [relationsError, setRelationsError] = useState('');
  const [refresh, setRefresh] = useState(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState('');
  const [graphError, setGraphError] = useState('');
  const [positions, setPositions] = useState<GraphPositions>({});
  const [view, setView] = useState<GraphView>(readGraphView);
  function changeView(update: Partial<GraphView>) {
    setView(previous => {
      const next = { ...previous, ...update };
      try {
        localStorage.setItem(graphViewKey, JSON.stringify(next));
      } catch {
        // The view still changes; it is just not remembered for the next visit.
      }
      return next;
    });
  }
  const positionsRef = useRef(positions);
  positionsRef.current = positions;
  const [positionsLoaded, setPositionsLoaded] = useState(false);
  const [saveState, setSaveState] = useState('');
  const saveQueue = useRef<Promise<void>>(Promise.resolve());
  const [nodes, setNodes, onNodesChange] = useNodesState<TableNode>([]);
  const [instance, setInstance] = useState<ReactFlowInstance<TableNode, Edge> | null>(null);
  const theme = useTheme();
  const colorsBySchema = useMemo(
    () =>
      schemaColors(
        (graph?.tables || []).map(table => table.schema),
        theme,
      ),
    [graph, theme],
  );
  const [selectedTableId, setSelectedTableId] = useState('');
  const [selectedRelationId, setSelectedRelationId] = useState('');
  const [decisionReason, setDecisionReason] = useState('');
  const [decisionBusy, setDecisionBusy] = useState(false);
  const [decisionError, setDecisionError] = useState('');
  const [fromTable, setFromTable] = useState<GraphTable | null>(null);
  const [toTable, setToTable] = useState<GraphTable | null>(null);
  const [paths, setPaths] = useState<JoinPath[] | null>(null);
  const [pathBusy, setPathBusy] = useState(false);
  const pathRequest = useRef<AbortController | null>(null);
  const [pathError, setPathError] = useState('');
  const [pathIndex, setPathIndex] = useState<number | null>(null);
  const [copyState, setCopyState] = useState('');

  useEffect(() => {
    setCatalog(null);
    setGraph(null);
    setRelations([]);
    setDatabaseId('');
    setSchema('');
    setFocusTable(null);
    setSelectedTableId('');
    setSelectedRelationId('');
    setPositions({});
    setPositionsLoaded(false);
    setSaveState('');
    setError('');
    setFromTable(null);
    setToTable(null);
    setPaths(null);
    setPathIndex(null);
    if (!workspaceId) return;
    const controller = new AbortController();
    request<Catalog>(`/api/workspaces/${encodeURIComponent(workspaceId)}/catalog`, { signal: controller.signal })
      .then(loaded => {
        setCatalog(loaded);
        if (loaded.databases.some(database => database.id === initialDatabaseId)) setDatabaseId(initialDatabaseId);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setError(errorMessage(cause, t('Catalog unavailable.')));
      });
    request<{ value: unknown }>(`/api/settings/${encodeURIComponent('graph:' + workspaceId)}`, { signal: controller.signal })
      .then(loaded => {
        setPositions(readPositions(loaded.value));
        setSaveState(t('Layout restored'));
      })
      .catch(cause => {
        if (!controller.signal.aborted) setSaveState(errorMessage(cause, t('Could not load the graph layout.')));
      })
      .finally(() => {
        if (!controller.signal.aborted) setPositionsLoaded(true);
      });
    return () => controller.abort();
  }, [workspaceId, initialDatabaseId]);

  useEffect(() => {
    setSchema('');
    setFocusTable(null);
    setFromTable(null);
    setToTable(null);
    setPaths(null);
    setPathIndex(null);
  }, [databaseId]);

  useEffect(() => {
    if (!workspaceId) return;
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const parameters = new URLSearchParams({
        hops: String(hops),
        minimumConfidence: String(minimumConfidence / 100),
        limit: '150',
        kind: selectedKinds.join(','),
      });
      if (databaseId) parameters.set('databaseId', databaseId);
      if (schema) parameters.set('schema', schema);
      if (query) parameters.set('query', query);
      if (focusTable) parameters.set('focusTableId', focusTable.id);
      setLoading(true);
      setGraphError('');
      request<WorkspaceGraph>(`/api/workspaces/${encodeURIComponent(workspaceId)}/graph?${parameters}`, { signal: controller.signal })
        .then(setGraph)
        .catch(cause => {
          if (!controller.signal.aborted) setGraphError(errorMessage(cause, t('Could not load the graph.')));
        })
        .finally(() => {
          if (!controller.signal.aborted) setLoading(false);
        });
    }, 250);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [workspaceId, databaseId, schema, query, focusTable, hops, minimumConfidence, selectedKinds, refresh]);

  useEffect(() => {
    if (!workspaceId) return;
    const controller = new AbortController();
    setRelationsError('');
    request<{ items: LogicalRelation[]; total: number }>(
      `/api/workspaces/${encodeURIComponent(workspaceId)}/relations/page?offset=${relationOffset}&limit=100&query=${encodeURIComponent(relationQuery)}`,
      { signal: controller.signal },
    )
      .then(page => {
        setRelations(page.items);
        setRelationTotal(page.total);
      })
      .catch(cause => {
        if (!controller.signal.aborted) setRelationsError(errorMessage(cause, t('Relationship catalog unavailable.')));
      });
    return () => controller.abort();
  }, [workspaceId, refresh, relationOffset, relationQuery]);
  useEffect(() => {
    setRelationOffset(0);
  }, [workspaceId, relationQuery]);

  useEffect(() => {
    if (!graph || !instance) return;
    const timer = setTimeout(() => {
      void instance.fitView({ padding: 0.18, duration: 180 });
    }, 75);
    return () => clearTimeout(timer);
  }, [graph, instance, positionsLoaded]);

  useEffect(() => {
    setDecisionReason('');
    setDecisionError('');
  }, [selectedRelationId]);
  useEffect(() => {
    pathRequest.current?.abort();
    setPathBusy(false);
    setPaths(null);
    setPathIndex(null);
    setPathError('');
    setCopyState('');
  }, [fromTable?.id, toTable?.id]);
  useEffect(() => () => pathRequest.current?.abort(), []);

  const selectedPath = pathIndex === null ? null : paths?.[pathIndex] || null;
  const pathEdges = useMemo(() => new Set(selectedPath?.relationIds || []), [selectedPath]);
  useEffect(() => {
    if (!graph) {
      setNodes([]);
      return;
    }
    const layout = layoutTables(graph.tables, graph.relations, focusTable?.id, view.compact);
    // Columns that join tables: every drawn relation marks them; the selected relation or JOIN path marks them as active.
    const linked = new Map<string, Set<string>>();
    const active = new Map<string, Set<string>>();
    const mark = (target: Map<string, Set<string>>, tableId: string, columns: string[]) => {
      if (!target.has(tableId)) target.set(tableId, new Set());
      for (const column of columns) target.get(tableId)!.add(column);
    };
    for (const relation of retainedRelations(graph.tables, graph.relations)) {
      mark(linked, relation.fromTableId, relation.fromColumns);
      mark(linked, relation.toTableId, relation.toColumns);
      if (relation.id === selectedRelationId || pathEdges.has(relation.id)) {
        mark(active, relation.fromTableId, relation.fromColumns);
        mark(active, relation.toTableId, relation.toColumns);
      }
    }
    setNodes(
      graph.tables.map(table => ({
        id: table.id,
        type: 'schemaTable',
        position: positions[table.id] || layout[table.id],
        data: {
          table,
          focused: table.id === focusTable?.id,
          compact: view.compact,
          color: view.bySchema ? colorsBySchema.get(table.schema) : undefined,
          linked: [...(linked.get(table.id) || [])],
          active: [...(active.get(table.id) || [])],
        },
        selected: table.id === selectedTableId,
      })),
    );
  }, [graph, positions, focusTable?.id, selectedTableId, setNodes, view, colorsBySchema, selectedRelationId, pathEdges]);

  // Bring the chosen JOIN path into view on the canvas; tables outside the loaded section are skipped.
  useEffect(() => {
    if (!selectedPath || !instance) return;
    const visible = selectedPath.tableIds.filter(id => nodes.some(node => node.id === id)).map(id => ({ id }));
    if (visible.length) void instance.fitView({ nodes: visible, padding: 0.3, duration: 220 });
  }, [selectedPath, instance]);
  const nodeX = useMemo(() => new Map(nodes.map(node => [node.id, node.position.x])), [nodes]);
  const [exportBusy, setExportBusy] = useState(false);

  // Only visible nodes are in the DOM, so the whole fragment is fitted first; the export itself is drawn at 1:1 scale.
  async function exportGraph(kind: 'png' | 'svg') {
    if (!instance || !nodes.length) return;
    setExportBusy(true);
    setSaveState('');
    try {
      await instance.fitView({ padding: 0.05, duration: 0 });
      await new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)));
      const viewport = document.querySelector<HTMLElement>('.relationship-canvas .react-flow__viewport');
      const canvas = document.querySelector<HTMLElement>('.relationship-canvas .react-flow');
      if (!viewport || !canvas) return;
      const bounds = getNodesBounds(nodes);
      const padding = 40;
      const width = Math.ceil(bounds.width + padding * 2);
      const height = Math.ceil(bounds.height + padding * 2);
      const options = {
        backgroundColor: getComputedStyle(canvas).backgroundColor,
        width,
        height,
        style: {
          width: `${width}px`,
          height: `${height}px`,
          transform: `translate(${padding - bounds.x}px, ${padding - bounds.y}px) scale(1)`,
        },
      };
      const url =
        kind === 'png'
          ? await toPng(viewport, { ...options, pixelRatio: width * height > 16_000_000 ? 1 : 2 })
          : await toSvg(viewport, options);
      const link = document.createElement('a');
      link.href = url;
      link.download = `schemora-graph.${kind}`;
      link.click();
    } catch (cause) {
      setSaveState(errorMessage(cause, t('Graph export failed.')));
    } finally {
      setExportBusy(false);
    }
  }
  // An edge starts at the row of its join column; compact cards and columns the card does not know use the table handles.
  const columnHandle = (tableId: string, column: string | undefined) =>
    !view.compact && column && graph?.tables.some(table => table.id === tableId && table.columns.some(item => item.name === column))
      ? `col:${column}:`
      : '';
  const edges = useMemo<Edge[]>(
    () =>
      graph
        ? retainedRelations(graph.tables, graph.relations).map(relation => {
            const appearance = relationAppearance(relation);
            const forward =
              relation.fromTableId === relation.toTableId || (nodeX.get(relation.fromTableId) ?? 0) <= (nodeX.get(relation.toTableId) ?? 0);
            return {
              id: relation.id,
              source: relation.fromTableId,
              target: relation.toTableId,
              type: 'smoothstep',
              sourceHandle: columnHandle(relation.fromTableId, relation.fromColumns[0]) + (forward ? 'source-right' : 'source-left'),
              targetHandle:
                columnHandle(relation.toTableId, relation.toColumns[0]) +
                (forward && relation.fromTableId !== relation.toTableId ? 'target-left' : 'target-right'),
              label: appearance.label,
              selected: relation.id === selectedRelationId,
              interactionWidth: 24,
              style: {
                stroke: appearance.color,
                strokeWidth: pathEdges.has(relation.id) || relation.id === selectedRelationId ? 3.5 : 1.6,
                strokeDasharray: appearance.dashed ? '6 5' : undefined,
                opacity: pathEdges.size && !pathEdges.has(relation.id) ? 0.25 : relation.status === 'rejected' ? 0.45 : 1,
              },
              markerEnd: { type: MarkerType.ArrowClosed, color: appearance.color, width: 17, height: 17 },
              labelStyle: { fill: appearance.color, fontSize: 12 },
              labelBgStyle: { fill: token('--bg-inset'), fillOpacity: 0.94 },
            };
          })
        : [],
    [graph, selectedRelationId, pathEdges, locale, nodeX, theme, view.compact],
  );

  const knownTables = useMemo(() => {
    const known = new Map<string, GraphTable>((graph?.tables || []).map(table => [table.id, table]));
    for (const table of [focusTable, fromTable, toTable]) if (table) known.set(table.id, table);
    return known;
  }, [graph, focusTable, fromTable, toTable]);
  const tableName = (id: string) => (knownTables.has(id) ? qualifiedTableName(knownTables.get(id)!) : t('Table · {0}', id.slice(0, 12)));
  const selectedRelation =
    relations.find(relation => relation.id === selectedRelationId) || graph?.relations.find(relation => relation.id === selectedRelationId);
  const selectedTable = knownTables.get(selectedTableId);
  const nearbyRelations = selectedTableId
    ? (graph?.relations || []).filter(relation => relation.fromTableId === selectedTableId || relation.toTableId === selectedTableId)
    : [];
  const filteredRelations = relations;
  const knownSchemas = [...new Set([schema, ...(graph?.tables.map(table => table.schema) || [])].filter(Boolean))].sort();

  function persistPositions(next: GraphPositions) {
    const targetWorkspace = workspaceId;
    setPositions(next);
    setSaveState(t('Saving layout…'));
    saveQueue.current = saveQueue.current
      .catch(() => undefined)
      .then(async () => {
        try {
          await request<unknown>(`/api/settings/${encodeURIComponent('graph:' + targetWorkspace)}`, {
            method: 'PUT',
            body: JSON.stringify({ value: { version: 1, positions: next } }),
          });
          if (workspaceRef.current === targetWorkspace) setSaveState(t('Layout saved'));
        } catch (cause) {
          if (workspaceRef.current === targetWorkspace) setSaveState(errorMessage(cause, t('Layout was not saved.')));
        }
      });
  }

  function toggleKind(kind: RelationKind) {
    setSelectedKinds(previous =>
      previous.includes(kind) ? (previous.length > 1 ? previous.filter(item => item !== kind) : previous) : [...previous, kind],
    );
  }

  async function decide(decision: 'confirmed' | 'rejected') {
    if (!selectedRelation || !decisionReason.trim()) return;
    pathRequest.current?.abort();
    setPathBusy(false);
    setDecisionBusy(true);
    setDecisionError('');
    try {
      await request<unknown>(
        `/api/workspaces/${encodeURIComponent(workspaceId)}/relations/${encodeURIComponent(selectedRelation.id)}/decision`,
        { method: 'POST', body: JSON.stringify({ decision, reason: decisionReason.trim() }) },
      );
      setDecisionReason('');
      setPaths(null);
      setPathIndex(null);
      setRefresh(previous => previous + 1);
    } catch (cause) {
      setDecisionError(errorMessage(cause, t('Decision was not saved.')));
    } finally {
      setDecisionBusy(false);
    }
  }

  async function findPaths() {
    if (!fromTable || !toTable || fromTable.id === toTable.id) return;
    pathRequest.current?.abort();
    const controller = new AbortController();
    pathRequest.current = controller;
    setPathBusy(true);
    setPathError('');
    setPaths(null);
    setPathIndex(null);
    setCopyState('');
    try {
      const found = await request<JoinPath[]>(`/api/workspaces/${encodeURIComponent(workspaceId)}/paths`, {
        method: 'POST',
        body: JSON.stringify({ fromTableId: fromTable.id, toTableId: toTable.id }),
        signal: controller.signal,
      });
      if (!controller.signal.aborted) {
        setPaths(found);
        if (found.length === 1) setPathIndex(0);
      }
    } catch (cause) {
      if (!controller.signal.aborted) setPathError(errorMessage(cause, t('Could not find a path.')));
    } finally {
      if (pathRequest.current === controller) {
        pathRequest.current = null;
        setPathBusy(false);
      }
    }
  }

  async function copySql() {
    if (!selectedPath) return;
    try {
      await navigator.clipboard.writeText(selectedPath.sql);
      setCopyState(t('SQL copied'));
    } catch {
      setCopyState(t('Copying is unavailable. Select the SQL in the field and copy it manually.'));
    }
  }

  function refreshRelationships() {
    const targetWorkspace = workspaceId;
    if (workspaceRef.current !== targetWorkspace) return;
    pathRequest.current?.abort();
    setPathBusy(false);
    setPaths(null);
    setPathIndex(null);
    setRefresh(previous => previous + 1);
    void request<Catalog>('/api/workspaces/' + encodeURIComponent(targetWorkspace) + '/catalog')
      .then(loaded => {
        if (workspaceRef.current === targetWorkspace) setCatalog(loaded);
      })
      .catch(cause => {
        if (workspaceRef.current === targetWorkspace) setError(errorMessage(cause, t('Catalog unavailable.')));
      });
  }

  function selectRelation(relation: LogicalRelation) {
    setSelectedRelationId(relation.id);
    setSelectedTableId('');
  }

  return (
    <div className="relationship-panel">
      <div className="relationship-top-filters">
        <label>
          <span className="field-label">{t('Database')}</span>
          <select
            aria-label={t('Graph database filter')}
            value={databaseId}
            onChange={event => setDatabaseId(event.target.value)}
            disabled={!workspaceId || pathBusy || decisionBusy}
          >
            <option value="">{t('All databases · relationships across databases')}</option>
            {catalog?.databases.map(database => (
              <option value={database.id} key={database.id}>
                {database.name}
              </option>
            ))}
          </select>
        </label>
        <button className="secondary-button" disabled={!workspaceId || loading} onClick={refreshRelationships}>
          {t('Refresh relationships')}
        </button>
      </div>
      {error && (
        <div className="alert error" role="alert">
          <p>{tMessage(error)}</p>
        </div>
      )}
      {!workspaceId ? (
        <div className="panel-placeholder">
          <strong>{t('Create a workspace first')}</strong>
          <p>{t('Open “Workspace”, select folders, and run a scan.')}</p>
        </div>
      ) : (
        <>
          <section className="relationship-filter-card">
            <div className="graph-filter-row">
              <label>
                <span className="field-label">{t('Search tables / columns')}</span>
                <input
                  type="search"
                  value={query}
                  onChange={event => setQuery(event.target.value)}
                  placeholder={t('Table or column name…')}
                  aria-label={t('Search the graph')}
                />
              </label>
              <label>
                <span className="field-label">{t('Schema')}</span>
                <input
                  list="graph-schema-options"
                  value={schema}
                  onChange={event => {
                    setSchema(event.target.value);
                    setFocusTable(null);
                  }}
                  placeholder={t('All schemas')}
                  aria-label={t('Schema filter')}
                />
                <datalist id="graph-schema-options">
                  {knownSchemas.map(name => (
                    <option key={name} value={name} />
                  ))}
                </datalist>
              </label>
              <label className="confidence-filter">
                <span className="field-label">
                  {t('Minimum confidence')} <strong>{minimumConfidence}%</strong>
                </span>
                <input
                  type="range"
                  min="0"
                  max="100"
                  step="5"
                  value={minimumConfidence}
                  onChange={event => setMinimumConfidence(Number(event.target.value))}
                  aria-label={t('Minimum confidence')}
                />
              </label>
            </div>
            <div className="relation-kind-filters" aria-label={t('Relationship types')}>
              {relationKinds.map(kind => (
                <button
                  key={kind}
                  aria-pressed={selectedKinds.includes(kind)}
                  className={selectedKinds.includes(kind) ? 'active' : ''}
                  onClick={() => toggleKind(kind)}
                >
                  <span style={{ backgroundColor: kindColors[kind] }} />
                  {t(kindLabels[kind])}
                </button>
              ))}
            </div>
            <div className="graph-focus-row">
              <TablePicker
                workspaceId={workspaceId}
                databaseId={databaseId}
                schema={schema}
                label={t('Focused table')}
                value={focusTable}
                onChange={setFocusTable}
                emptyLabel={t('Entire loaded graph section')}
              />
              <label className="hops-picker">
                <span className="field-label">{t('Hops from focus')}</span>
                <select value={hops} onChange={event => setHops(Number(event.target.value))} aria-label={t('Number of hops')}>
                  <option value="0">{t('0 · table only')}</option>
                  <option value="1">{t('1 · immediate neighbors')}</option>
                  <option value="2">{t('2 hops')}</option>
                  <option value="3">{t('3 hops')}</option>
                </select>
              </label>
              <div className="graph-scope-note">
                {t(
                  'Filters and neighborhoods are applied on the server. Each section contains up to 150 tables. Search for a focus table across the entire catalog.',
                )}
              </div>
            </div>
          </section>
          {graphError && (
            <div className="alert error" role="alert">
              <p>{tMessage(graphError)}</p>
            </div>
          )}
          {Boolean(graph?.diagnostics.length) && (
            <details className="model-diagnostics" open={graph?.diagnostics.some(item => item.severity.toLowerCase() === 'error')}>
              <summary>
                {t('Metadata loaded with warnings ·')} {graph?.diagnostics.length}
              </summary>
              {graph?.diagnostics.map((diagnostic, index) => (
                <div className={`alert ${diagnostic.severity === 'error' ? 'error' : 'warning'}`} key={`${diagnostic.code}-${index}`}>
                  <div>
                    <strong>{diagnostic.code}</strong>
                    <p>{tMessage(diagnostic.message)}</p>
                  </div>
                </div>
              ))}
            </details>
          )}
          <section className="flow-browser">
            <div className="flow-browser-toolbar">
              <span>
                {loading
                  ? t('Refreshing graph…')
                  : t(
                      '{0} of {1} tables · {2} relationships',
                      graph?.tables.length || 0,
                      graph?.totalTables || 0,
                      graph?.relations.length || 0,
                    )}
                {graph?.truncated && <b>{t('Graph section limited — choose a focus or refine the filters')}</b>}
              </span>
              <button
                className="text-button"
                disabled={!graph?.tables.length || !positionsLoaded}
                onClick={() => {
                  if (graph) {
                    persistPositions({
                      ...positionsRef.current,
                      ...layoutTables(graph.tables, graph.relations, focusTable?.id, view.compact),
                    });
                    setTimeout(() => {
                      void instance?.fitView({ duration: 200, padding: 0.18 });
                    }, 80);
                  }
                }}
              >
                {t('Auto layout')}
              </button>
              <button className="text-button" disabled={!nodes.length || exportBusy} onClick={() => void exportGraph('png')}>
                {t('Export PNG')}
              </button>
              <button className="text-button" disabled={!nodes.length || exportBusy} onClick={() => void exportGraph('svg')}>
                {t('Export SVG')}
              </button>
              <button className="text-button" aria-pressed={view.bySchema} onClick={() => changeView({ bySchema: !view.bySchema })}>
                {t('Colour by schema')}
              </button>
              <button className="text-button" aria-pressed={view.compact} onClick={() => changeView({ compact: !view.compact })}>
                {t('Compact cards')}
              </button>
            </div>
            {view.bySchema && colorsBySchema.size > 0 && (
              <ul className="graph-schema-legend" aria-label={t('Schemas')}>
                {[...colorsBySchema].map(([name, color]) => (
                  <li key={name}>
                    <span style={{ backgroundColor: color }} aria-hidden="true" />
                    {name}
                  </li>
                ))}
              </ul>
            )}
            <div className="relationship-canvas">
              <ReactFlow<TableNode, Edge>
                nodes={nodes}
                edges={edges}
                nodeTypes={nodeTypes}
                onNodesChange={onNodesChange}
                onInit={setInstance}
                onNodeClick={(_, node) => {
                  setSelectedTableId(node.id);
                  setSelectedRelationId('');
                }}
                onEdgeClick={(_, edge) => {
                  setSelectedRelationId(edge.id);
                  setSelectedTableId('');
                }}
                onNodeDragStop={(_, node, dragged) => {
                  const next = { ...positionsRef.current };
                  for (const moved of [node, ...dragged]) next[moved.id] = { x: moved.position.x, y: moved.position.y };
                  persistPositions(next);
                }}
                nodesConnectable={false}
                nodesDraggable={positionsLoaded}
                deleteKeyCode={null}
                onlyRenderVisibleElements
                fitView
                minZoom={0.08}
                maxZoom={2.5}
                colorMode={theme}
                panOnDrag
                zoomOnScroll
                zoomOnPinch
                ariaLabelConfig={{
                  'controls.zoomIn.ariaLabel': t('Zoom in'),
                  'controls.zoomOut.ariaLabel': t('Zoom out'),
                  'controls.fitView.ariaLabel': t('Fit the entire section'),
                }}
              >
                <Background variant={BackgroundVariant.Dots} gap={21} size={1} color={token('--border-strong')} />
                <Controls showInteractive={false} />
                <MiniMap
                  pannable
                  zoomable
                  ariaLabel={t('Schema minimap')}
                  nodeColor={node =>
                    (view.bySchema && colorsBySchema.get((node.data as TableNode['data']).table.schema)) || token('--bg-active')
                  }
                  nodeStrokeColor={token('--accent-border')}
                  bgColor={token('--bg-inset')}
                  maskColor={theme === 'light' ? '#dfe9ef99' : '#0b141ac9'}
                />
              </ReactFlow>
              {!loading && !graph?.tables.length && (
                <div className="flow-empty">
                  {t('There are no tables in this section.')}
                  <br />
                  {t('Refresh the workspace or change the filters.')}
                </div>
              )}
            </div>
            <div className="flow-browser-footer">
              <span>{t('Solid line: the server allows JOIN · dashed: review required')}</span>
              <span>
                {tMessage(saveState) || (positionsLoaded ? t('Drag tables to move them; the layout is saved') : t('Loading layout…'))}
              </span>
            </div>
          </section>
          <div className="relation-inspection-grid">
            <section className="relation-catalog">
              <div className="section-heading">
                <h2>
                  {t('Workspace relationships')} <span className="section-count">{relationTotal}</span>
                </h2>
              </div>
              <input
                type="search"
                placeholder={t('Name, type, or status…')}
                aria-label={t('Search relationships')}
                value={relationQuery}
                onChange={event => setRelationQuery(event.target.value)}
              />
              {relationsError && <p className="job-error">{tMessage(relationsError)}</p>}
              <div className="relation-catalog-list">
                {filteredRelations.slice(0, 250).map(relation => (
                  <button
                    key={relation.id}
                    onClick={() => selectRelation(relation)}
                    className={selectedRelationId === relation.id ? 'selected' : ''}
                  >
                    <span className="relation-color-dot" style={{ backgroundColor: relationAppearance(relation).color }} />
                    <span>
                      <strong>{relation.name}</strong>
                      <small>
                        {t(kindLabels[relation.kind]) || relation.kind} · {t(statusLabels[relation.status]) || relation.status}
                      </small>
                    </span>
                    <em>{Math.round(relation.confidence * 100)}%</em>
                  </button>
                ))}
                {!filteredRelations.length && <p className="muted-text">{t('No relationships found. Check the scan and EF bindings.')}</p>}
              </div>
              {relationTotal > 100 && (
                <div className="enhancement-actions">
                  <button
                    className="text-button"
                    disabled={relationOffset === 0}
                    onClick={() => setRelationOffset(v => Math.max(0, v - 100))}
                  >
                    {t('Previous page')}
                  </button>
                  <span>
                    {relationOffset + 1}–{Math.min(relationOffset + 100, relationTotal)} / {relationTotal}
                  </span>
                  <button
                    className="text-button"
                    disabled={relationOffset + 100 >= relationTotal}
                    onClick={() => setRelationOffset(v => v + 100)}
                  >
                    {t('Next page')}
                  </button>
                </div>
              )}
              {filteredRelations.length > 250 && (
                <p className="workspace-limit-note">
                  {t('First 250 of')} {filteredRelations.length}
                  {t('; refine your search.')}
                </p>
              )}
            </section>
            <section className="relation-inspector">
              {selectedRelation ? (
                <>
                  <div className="section-heading">
                    <h2>{t('Relationship evidence')}</h2>
                    <span className={selectedRelation.canUseForJoin ? 'confirmed-tag' : 'neutral-tag'}>
                      {selectedRelation.canUseForJoin ? t('JOIN allowed by the server') : t('JOIN not allowed')}
                    </span>
                  </div>
                  <h3>{selectedRelation.name}</h3>
                  <div className="relation-inspector-tags">
                    <span className="neutral-tag">{t(kindLabels[selectedRelation.kind]) || selectedRelation.kind}</span>
                    <span
                      className={
                        selectedRelation.status === 'conflict' || selectedRelation.status === 'stale' ? 'failure-tag' : 'neutral-tag'
                      }
                    >
                      {t(statusLabels[selectedRelation.status]) || selectedRelation.status}
                    </span>
                    <span className="neutral-tag">{Math.round(selectedRelation.confidence * 100)}%</span>
                  </div>
                  <div className="relation-endpoints">
                    <div>
                      <span>FROM</span>
                      <strong title={selectedRelation.fromTableId}>{tableName(selectedRelation.fromTableId)}</strong>
                      <code>{selectedRelation.fromColumns.join(', ')}</code>
                    </div>
                    <b>→</b>
                    <div>
                      <span>TO</span>
                      <strong title={selectedRelation.toTableId}>{tableName(selectedRelation.toTableId)}</strong>
                      <code>{selectedRelation.toColumns.join(', ')}</code>
                    </div>
                  </div>
                  {selectedRelation.reviewReason && (
                    <p className="relation-review-reason">
                      {t('User decision:')}{' '}
                      {selectedRelation.evidence.some(item => ['user-decision', 'manual-relation'].includes(item.source))
                        ? selectedRelation.reviewReason
                        : tMessage(selectedRelation.reviewReason)}
                    </p>
                  )}
                  <div className="evidence-list">
                    {selectedRelation.evidence.map((evidence, index) => (
                      <article key={index}>
                        <span>{evidence.source}</span>
                        <p>
                          {['user-decision', 'manual-relation'].includes(evidence.source) ? evidence.reason : tMessage(evidence.reason)}
                        </p>
                        {evidence.path && <SourceLocation path={evidence.path} line={evidence.line} />}
                      </article>
                    ))}
                  </div>
                  {selectedRelation.kind !== 'declared-fk' &&
                    (selectedRelation.kind !== 'orm-mapping' || !selectedRelation.canUseForJoin) &&
                    selectedRelation.status !== 'stale' && (
                      <div className="relation-decision">
                        <label className="field-label">
                          {t('Reason for the decision')}
                          <textarea
                            aria-label={t('Reason for the relationship decision')}
                            value={decisionReason}
                            maxLength={1000}
                            onChange={event => setDecisionReason(event.target.value)}
                            placeholder={t('Why is this logical relationship correct or incorrect?')}
                            disabled={decisionBusy}
                            rows={3}
                          />
                        </label>
                        <div>
                          <button
                            className="primary-button"
                            disabled={decisionBusy || !decisionReason.trim()}
                            onClick={() => void decide('confirmed')}
                          >
                            {decisionBusy ? t('Saving…') : t('Confirm relationship')}
                          </button>
                          <button
                            className="secondary-button"
                            disabled={decisionBusy || !decisionReason.trim()}
                            onClick={() => void decide('rejected')}
                          >
                            {t('Reject')}
                          </button>
                        </div>
                        <p>
                          {t('The decision is stored separately from the schema. The server rechecks key compatibility and conflicts.')}
                        </p>
                        {decisionError && (
                          <p className="job-error" role="alert">
                            {tMessage(decisionError)}
                          </p>
                        )}
                      </div>
                    )}
                </>
              ) : selectedTable ? (
                <>
                  <div className="section-heading">
                    <h2>{t('Table')}</h2>
                    <button className="text-button" onClick={() => setFocusTable(selectedTable)}>
                      {t('Focus and neighbors')}
                    </button>
                  </div>
                  <h3>{qualifiedTableName(selectedTable)}</h3>
                  <div className="selected-table-columns">
                    {selectedTable.columns.map(column => (
                      <div key={column.name}>
                        <code>{column.name}</code>
                        <span>{column.dataType}</span>
                        {selectedTable.primaryKey.includes(column.name) && <b>PK</b>}
                      </div>
                    ))}
                  </div>
                  <h4 className="nearby-title">{t('Relationships in the loaded section')}</h4>
                  {nearbyRelations.map(relation => (
                    <button className="nearby-relation" key={relation.id} onClick={() => selectRelation(relation)}>
                      {relation.name}
                      <span>{t(kindLabels[relation.kind]) || relation.kind}</span>
                    </button>
                  ))}
                  {!nearbyRelations.length && <p className="muted-text">{t('There are no neighboring relationships in this section.')}</p>}
                </>
              ) : (
                <div className="relation-detail-empty">
                  <strong>{t('Select an edge or table')}</strong>
                  <p>{t('View the relationship’s source, evidence, confidence, and whether it can be used in JOIN.')}</p>
                </div>
              )}
            </section>
          </div>
          <ManualRelations workspaceId={workspaceId} onChanged={refreshRelationships} />
          <section className="path-explorer source-card">
            <div className="source-header">
              <div className="section-title">
                <h2>{t('Path / Join Explorer')}</h2>
              </div>
              <span className="quiet-label">{t('Only paths trusted by the server')}</span>
            </div>
            <div className="path-picker-row">
              <TablePicker
                workspaceId={workspaceId}
                databaseId={databaseId}
                label={t('Source table')}
                value={fromTable}
                onChange={setFromTable}
                disabled={pathBusy}
              />
              <span className="path-direction-arrow">→</span>
              <TablePicker
                workspaceId={workspaceId}
                databaseId={databaseId}
                label={t('Target table')}
                value={toTable}
                onChange={setToTable}
                disabled={pathBusy}
              />
              <button
                className="primary-button"
                disabled={!fromTable || !toTable || fromTable.id === toTable.id || pathBusy}
                onClick={() => void findPaths()}
              >
                {pathBusy ? t('Searching…') : t('Find paths')}
              </button>
            </div>
            {pathError && (
              <p className="job-error" role="alert">
                {tMessage(pathError)}
              </p>
            )}
            {paths && !paths.length && (
              <div className="path-no-result">
                {t(
                  'No trusted path found. Unconfirmed candidates, rejected relationships, and conflicting relationships are excluded. Check the evidence and EF bindings.',
                )}
              </div>
            )}
            {paths && paths.length > 0 && (
              <>
                <div className="path-choices">
                  <p>
                    {paths.length > 1
                      ? t('Paths found: {0}. Select one before copying the SQL.', paths.length)
                      : t('One trusted path found.')}
                  </p>
                  {paths.map((path, index) => (
                    <button
                      key={`${path.relationIds.join(':')}-${index}`}
                      className={pathIndex === index ? 'selected' : ''}
                      onClick={() => {
                        setPathIndex(index);
                        setCopyState('');
                      }}
                    >
                      <span>
                        {t('Path')} {index + 1} · {path.relationIds.length} {t('relationships')}
                      </span>
                      <strong>{path.tableIds.map(tableName).join(' → ')}</strong>
                    </button>
                  ))}
                </div>
                {selectedPath ? (
                  <div className="join-preview">
                    <div className="section-heading">
                      <h3>JOIN SQL · SQL Server</h3>
                      {onCreateRecipe && (
                        <button
                          className="secondary-button"
                          onClick={() =>
                            onCreateRecipe({
                              workspaceId,
                              name: `${fromTable?.name || t('Table')} → ${toTable?.name || t('table')}`,
                              sqlTemplate: selectedPath.sql,
                              databaseIds: catalog?.databases.map(database => database.id) || [],
                              inputs: [],
                            })
                          }
                        >
                          {t('Create recipe')}
                        </button>
                      )}
                      <button className="secondary-button" onClick={() => void copySql()}>
                        {t('Copy SQL')}
                      </button>
                    </div>
                    {selectedPath.explanations?.length ? (
                      <ul className="path-explanations">
                        {selectedPath.explanations.map((explanation, index) => (
                          <li key={index}>{tMessage(explanation)}</li>
                        ))}
                      </ul>
                    ) : null}
                    {selectedPath.warnings.map((warning, index) => (
                      <p className="join-warning" key={index}>
                        {tMessage(warning)}
                      </p>
                    ))}
                    <textarea
                      aria-label={t('JOIN SQL preview')}
                      readOnly
                      value={selectedPath.sql}
                      spellCheck={false}
                      rows={Math.min(18, Math.max(5, selectedPath.sql.split('\n').length + 1))}
                    />
                    <p className="copy-state" role="status">
                      {tMessage(copyState) || t('This is a SQL preview. Queries are not executed.')}
                    </p>
                  </div>
                ) : (
                  <div className="path-no-result">{t('Select a path to view its JOIN SQL.')}</div>
                )}
              </>
            )}
          </section>
          <ProjectAnalysisPanel workspaceId={workspaceId} onSaved={refreshRelationships} refreshKey={refresh} />
          <EfMappings workspaceId={workspaceId} refreshKey={refresh} onSaved={refreshRelationships} />
          <DatabaseAliases databases={catalog?.databases || []} onSaved={refreshRelationships} />
        </>
      )}
    </div>
  );
}
