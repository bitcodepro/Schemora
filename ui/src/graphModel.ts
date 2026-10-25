import { t } from './i18n.ts';
import type { Table } from './types';

export type RelationKind =
  | 'declared-fk'
  | 'orm-mapping'
  | 'inferred-candidate'
  | 'observed-join'
  | 'user-confirmed'
  | 'embedded-sql-candidate';
export type RelationStatus = 'physical' | 'mapped' | 'candidate' | 'observed' | 'confirmed' | 'rejected' | 'conflict' | 'stale';
export type RelationEvidence = { source: string; path?: string; line?: number; reason: string };
export type LogicalRelation = {
  id: string;
  name: string;
  fromTableId: string;
  toTableId: string;
  fromColumns: string[];
  toColumns: string[];
  kind: RelationKind;
  confidence: number;
  status: RelationStatus;
  canUseForJoin: boolean;
  evidence: RelationEvidence[];
  reviewReason?: string;
};
export type GraphTable = Table & { databaseId: string; databaseName: string };
export type WorkspaceGraph = {
  tables: GraphTable[];
  relations: LogicalRelation[];
  diagnostics: { severity: string; code: string; message: string }[];
  totalTables: number;
  truncated: boolean;
};
export type TablePage = { items: GraphTable[]; total: number; offset: number; limit: number };
export type JoinPath = { tableIds: string[]; relationIds: string[]; sql: string; warnings: string[]; explanations?: string[] };
export type GraphPositions = Record<string, { x: number; y: number }>;

export const relationKinds: RelationKind[] = [
  'declared-fk',
  'orm-mapping',
  'user-confirmed',
  'observed-join',
  'embedded-sql-candidate',
  'inferred-candidate',
];
export const kindLabels: Record<RelationKind, string> = {
  'declared-fk': 'Declared FK',
  'orm-mapping': 'ORM mapping',
  'user-confirmed': 'Confirmed #2',
  'observed-join': 'Observed join',
  'embedded-sql-candidate': 'SQL from C# / Dapper',
  'inferred-candidate': 'Candidate',
};
export const statusLabels: Record<RelationStatus, string> = {
  physical: 'Physical FK',
  mapped: 'Exact ORM mapping',
  candidate: 'Needs review',
  observed: 'Observed JOIN',
  confirmed: 'Confirmed',
  rejected: 'Rejected',
  conflict: 'Conflict',
  stale: 'Needs another review',
};

export const kindColors: Record<RelationKind, string> = {
  'declared-fk': '#6dceb2',
  'orm-mapping': '#78afe3',
  'user-confirmed': '#b49bdc',
  'observed-join': '#c7ae76',
  'embedded-sql-candidate': '#d1a278',
  'inferred-candidate': '#8b9aa6',
};

// Eight hues that stay apart on both canvases; schemas beyond eight reuse them in order.
export const schemaPalette: Record<'dark' | 'light', readonly string[]> = {
  dark: ['#5fb3a3', '#d9a54a', '#7c9ce6', '#d57a9a', '#9bc46a', '#b58ae0', '#e0855a', '#5bb6d8'],
  light: ['#2f8a79', '#a8741b', '#4a6cc2', '#b24d72', '#5f8f2e', '#8357b8', '#bd5b2c', '#2b88ad'],
};

/** Stable colour per schema name: schemas are ordered by name, so the same set always gets the same colours. */
export function schemaColors(schemas: Iterable<string>, theme: 'dark' | 'light'): Map<string, string> {
  const names = [...new Set(schemas)].sort((a, b) => a.localeCompare(b, 'en'));
  return new Map(names.map((name, index) => [name, schemaPalette[theme][index % schemaPalette[theme].length]]));
}

export function relationAppearance(relation: LogicalRelation): { color: string; dashed: boolean; label: string } {
  return {
    color:
      relation.status === 'conflict' || relation.status === 'stale'
        ? '#dc929a'
        : relation.status === 'rejected'
          ? '#6a727d'
          : kindColors[relation.kind] || '#8b9aa6',
    dashed: !relation.canUseForJoin,
    label: `${t(kindLabels[relation.kind]) || relation.kind} · ${Math.round(relation.confidence * 100)}%`,
  };
}

export function readPositions(value: unknown): GraphPositions {
  if (!value || typeof value !== 'object' || !('positions' in value)) return {};
  const positions = (value as { positions?: unknown }).positions;
  if (!positions || typeof positions !== 'object' || Array.isArray(positions)) return {};
  const valid: GraphPositions = {};
  for (const [id, point] of Object.entries(positions).slice(0, 5000)) {
    if (!point || typeof point !== 'object') continue;
    const { x, y } = point as { x?: unknown; y?: unknown };
    if (
      typeof x === 'number' &&
      typeof y === 'number' &&
      Number.isFinite(x) &&
      Number.isFinite(y) &&
      Math.abs(x) <= 1_000_000 &&
      Math.abs(y) <= 1_000_000
    ) {
      Object.defineProperty(valid, id, { value: { x, y }, enumerable: true, writable: true, configurable: true });
    }
  }
  return valid;
}

const columnWidth = 380;
const fullRowHeight = 215;
// Compact cards show only the table name, so rows can sit much closer.
const compactRowHeight = 95;

export function layoutTables(tables: GraphTable[], relations: LogicalRelation[], focusTableId?: string, compact = false): GraphPositions {
  const rowHeight = compact ? compactRowHeight : fullRowHeight;
  const ordered = [...tables].sort((a, b) =>
    `${a.databaseName}.${a.schema}.${a.name}.${a.id}`.localeCompare(`${b.databaseName}.${b.schema}.${b.name}.${b.id}`, 'en'),
  );
  const rank = new Map(ordered.map((table, index) => [table.id, index]));
  const byRank = (a: string, b: string) => rank.get(a)! - rank.get(b)!;
  const adjacent = new Map<string, Set<string>>(tables.map(table => [table.id, new Set<string>()]));
  const outgoing = new Map<string, Set<string>>(tables.map(table => [table.id, new Set<string>()]));
  for (const relation of relations) {
    if (!rank.has(relation.fromTableId) || !rank.has(relation.toTableId) || relation.fromTableId === relation.toTableId) continue;
    adjacent.get(relation.fromTableId)!.add(relation.toTableId);
    adjacent.get(relation.toTableId)!.add(relation.fromTableId);
    outgoing.get(relation.fromTableId)!.add(relation.toTableId);
  }
  const neighbours = (id: string) => [...adjacent.get(id)!].sort(byRank);
  if (focusTableId && rank.has(focusTableId)) return neighbourhoodLayout(focusTableId, ordered, neighbours, rowHeight);

  // Without a focus table, each connected component is drawn in layers along the FK direction:
  // referencing tables to the left, referenced tables to the right, so edges read left to right.
  const positions: GraphPositions = {};
  const placed = new Set<string>();
  let componentTop = 30;
  for (const root of ordered.map(table => table.id)) {
    if (placed.has(root)) continue;
    const component = [root];
    placed.add(root);
    for (let index = 0; index < component.length; index++)
      for (const next of neighbours(component[index]))
        if (!placed.has(next)) {
          placed.add(next);
          component.push(next);
        }
    component.sort(byRank);

    // Depth-first search in rank order drops edges back into the current path, which breaks cycles deterministically.
    const acyclic = new Map<string, string[]>(component.map(id => [id, []]));
    const state = new Map<string, 'open' | 'done'>();
    const visit = (id: string) => {
      state.set(id, 'open');
      for (const next of [...outgoing.get(id)!].sort(byRank)) {
        if (state.get(next) === 'open') continue;
        acyclic.get(id)!.push(next);
        if (!state.has(next)) visit(next);
      }
      state.set(id, 'done');
    };
    for (const id of component) if (!state.has(id)) visit(id);

    const incoming = new Map<string, number>(component.map(id => [id, 0]));
    for (const targets of acyclic.values()) for (const target of targets) incoming.set(target, incoming.get(target)! + 1);
    const layerOf = new Map<string, number>(component.map(id => [id, 0]));
    const ready = component.filter(id => incoming.get(id) === 0);
    while (ready.length) {
      const id = ready.shift()!;
      for (const next of acyclic.get(id)!) {
        layerOf.set(next, Math.max(layerOf.get(next)!, layerOf.get(id)! + 1));
        incoming.set(next, incoming.get(next)! - 1);
        if (incoming.get(next) === 0) {
          ready.push(next);
          ready.sort(byRank);
        }
      }
    }

    const layers: string[][] = [];
    for (const id of component) (layers[layerOf.get(id)!] ||= []).push(id);
    const row = new Map<string, number>();
    const number = (layer: string[]) => layer.forEach((id, index) => row.set(id, index));
    layers.forEach(number);
    // Two barycentre sweeps order each layer by the rows of its neighbours to reduce crossings.
    const sweep = (layer: string[], reference: Set<string>) => {
      const centre = (id: string) => {
        const rows = neighbours(id)
          .filter(next => reference.has(next))
          .map(next => row.get(next)!);
        return rows.length ? rows.reduce((sum, value) => sum + value, 0) / rows.length : row.get(id)!;
      };
      const keys = new Map(layer.map(id => [id, centre(id)]));
      layer.sort((a, b) => keys.get(a)! - keys.get(b)! || byRank(a, b));
      number(layer);
    };
    for (let index = 1; index < layers.length; index++) sweep(layers[index], new Set(layers[index - 1]));
    for (let index = layers.length - 2; index >= 0; index--) sweep(layers[index], new Set(layers[index + 1]));

    layers.forEach((layer, column) =>
      layer.forEach((id, index) => (positions[id] = { x: 30 + column * columnWidth, y: componentTop + index * rowHeight })),
    );
    componentTop += Math.max(...layers.map(layer => layer.length), 1) * rowHeight + 50;
  }
  return positions;
}

function neighbourhoodLayout(
  focusTableId: string,
  ordered: GraphTable[],
  neighbours: (id: string) => string[],
  rowHeight: number,
): GraphPositions {
  const positions: GraphPositions = {};
  const seen = new Set<string>();
  const roots = [focusTableId, ...ordered.filter(table => table.id !== focusTableId).map(table => table.id)];
  let componentTop = 30;
  for (const root of roots) {
    if (seen.has(root)) continue;
    const queue: { id: string; depth: number }[] = [{ id: root, depth: 0 }];
    const rows = new Map<number, number>();
    seen.add(root);
    for (let index = 0; index < queue.length; index++) {
      const current = queue[index];
      const row = rows.get(current.depth) || 0;
      positions[current.id] = { x: 30 + current.depth * columnWidth, y: componentTop + row * rowHeight };
      rows.set(current.depth, row + 1);
      for (const next of neighbours(current.id)) {
        if (seen.has(next)) continue;
        seen.add(next);
        queue.push({ id: next, depth: current.depth + 1 });
      }
    }
    componentTop += Math.max(...rows.values(), 1) * rowHeight + 50;
  }
  return positions;
}

const visibleColumnLimit = 4;

/** Columns a card lists: key and join columns always, then the rest in table order up to the usual four rows. */
export function visibleColumns(table: GraphTable, linked: readonly string[]) {
  const important = new Set([...table.primaryKey, ...linked]);
  let room = Math.max(0, visibleColumnLimit - table.columns.filter(column => important.has(column.name)).length);
  return table.columns.filter(column => important.has(column.name) || room-- > 0);
}

export function retainedRelations(tables: GraphTable[], relations: LogicalRelation[]): LogicalRelation[] {
  const ids = new Set(tables.map(table => table.id));
  return relations.filter(relation => ids.has(relation.fromTableId) && ids.has(relation.toTableId));
}
