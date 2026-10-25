import type { Graph, Table } from './types';

/** The focus owns the first slot, including when a high-degree table sorts after all its neighbours. */
export function schemaOverview(tables: Table[], relations: Graph['relations'], selected: Table | null, limit = 60) {
  if (tables.length <= limit) return { tables, partial: false };
  const focus = selected || tables[0];
  const ids = new Set<string>();
  for (const relation of relations) {
    if (relation.fromTableId === focus.id) ids.add(relation.toTableId);
    if (relation.toTableId === focus.id) ids.add(relation.fromTableId);
  }
  return {
    tables: [focus, ...tables.filter(table => table.id !== focus.id && ids.has(table.id)).slice(0, limit - 1)],
    partial: true,
  };
}
