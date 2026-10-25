/**
 * Navigation state in the URL fragment, e.g. #/recipes?ws=…&recipe=…&tab=runtime, so a reload or a copied address
 * returns to the same page, workspace, object and tab. Only identifiers go here — never runtime values or SQL.
 * The launch token also arrives in the fragment (#token=…); api.ts reads and removes it before routing starts.
 */
export type Route = { page: string; params: Record<string, string> };
export const routeUpdated = 'schemora-route-updated';
const indexKey = 'schemoraHistoryIndex';

export function routeEntry() {
  const state = history.state && typeof history.state === 'object' ? history.state : {};
  return { hash: location.hash, index: typeof state[indexKey] === 'number' ? (state[indexKey] as number) : null };
}

export function initializeRouteHistory() {
  if (routeEntry().index === null) history.replaceState({ ...history.state, [indexKey]: 0 }, '', location.href);
}

export function replaceRoute(page: string, params: Record<string, string | null | undefined> = {}) {
  const next = formatRoute(page, params);
  history.replaceState(history.state, '', location.pathname + location.search + next);
  window.dispatchEvent(new Event(routeUpdated));
}

export function readRoute(hash = typeof location === 'undefined' ? '' : location.hash): Route {
  const match = /^#\/([a-z]+)(?:\?(.*))?$/.exec(hash);
  if (!match) return { page: '', params: {} };
  return { page: match[1], params: Object.fromEntries(new URLSearchParams(match[2] || '')) };
}

export function formatRoute(page: string, params: Record<string, string | null | undefined> = {}): string {
  const query = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) if (value) query.set(key, value);
  const text = query.toString();
  return '#/' + page + (text ? '?' + text : '');
}

/** A page change: a new history entry, so Back returns to the previous page. */
export function pushRoute(page: string, params: Record<string, string | null | undefined> = {}) {
  initializeRouteHistory();
  const next = formatRoute(page, params);
  if (location.hash !== next) {
    history.pushState({ [indexKey]: (routeEntry().index || 0) + 1 }, '', location.pathname + location.search + next);
    window.dispatchEvent(new Event(routeUpdated));
  }
}

/** Selection changes within a page replace the current entry instead of filling the history. */
export function updateRouteParams(page: string, params: Record<string, string | null | undefined>) {
  const current = readRoute();
  if (current.page && current.page !== page) return;
  const next = formatRoute(page, { ...current.params, ...params });
  if (location.hash !== next) replaceRoute(page, { ...current.params, ...params });
}
