import { useEffect, useRef, useState } from 'react';
import { initializeRouteHistory, pushRoute, readRoute, replaceRoute, routeEntry, routeUpdated } from './route';
import type { Route } from './route';
import { useWorkspaces } from './workspaceContext';

const pages = ['workspace', 'schema', 'graph', 'recipes', 'changes', 'diagnostics', 'professional'] as const;
export type PageName = (typeof pages)[number];
const isPage = (value: string): value is PageName => (pages as readonly string[]).includes(value);
const canLeave = () => window.dispatchEvent(new Event('schemora-leave-editor', { cancelable: true }));

/** Apply the complete address on every history traversal; page components only serialize their local selections. */
export function useAppNavigation() {
  const { workspaces, workspaceId, select } = useWorkspaces();
  const [navigation, setNavigation] = useState(() => {
    const route = readRoute();
    return { route: isPage(route.page) ? route : ({ page: 'workspace', params: {} } as Route), nonce: 1 };
  });
  const live = useRef({ workspaceId, workspaces, select });
  live.current = { workspaceId, workspaces, select };
  const accepted = useRef(routeEntry());
  const restoring = useRef(false);
  const pendingWorkspace = useRef(navigation.route.params.ws || '');
  const previousWorkspace = useRef('');

  function apply(route: Route) {
    const context = live.current;
    const target = context.workspaces.some(item => item.id === route.params.ws) ? route.params.ws : context.workspaceId;
    pendingWorkspace.current = target || route.params.ws || '';
    if (target && target !== context.workspaceId) context.select(target, { confirmed: true });
    setNavigation(previous => ({
      route: { ...route, params: { ...route.params, ...(target ? { ws: target } : {}) } },
      nonce: previous.nonce + 1,
    }));
  }

  useEffect(() => {
    initializeRouteHistory();
    if (!isPage(readRoute().page)) replaceRoute('workspace');
    accepted.current = routeEntry();
    const remember = () => {
      accepted.current = routeEntry();
    };
    const restore = () => {
      const entry = routeEntry();
      if (entry.hash === accepted.current.hash) {
        restoring.current = false;
        return;
      }
      if (restoring.current) return;
      const route = readRoute();
      if (!isPage(route.page)) return;
      if (!canLeave()) {
        const delta = entry.index !== null && accepted.current.index !== null ? accepted.current.index - entry.index : 0;
        if (delta) {
          restoring.current = true;
          history.go(delta);
        } else {
          const previous = readRoute(accepted.current.hash);
          replaceRoute(previous.page, previous.params);
        }
        return;
      }
      accepted.current = entry;
      apply(route);
    };
    window.addEventListener(routeUpdated, remember);
    window.addEventListener('popstate', restore);
    window.addEventListener('hashchange', restore);
    return () => {
      window.removeEventListener(routeUpdated, remember);
      window.removeEventListener('popstate', restore);
      window.removeEventListener('hashchange', restore);
    };
  }, []);

  useEffect(() => {
    if (!workspaces.length || !workspaceId) return;
    const wanted = pendingWorkspace.current;
    if (wanted && wanted !== workspaceId && workspaces.some(item => item.id === wanted)) {
      select(wanted, { confirmed: true });
      return;
    }
    if (wanted && wanted !== workspaceId && !workspaces.some(item => item.id === wanted)) {
      const route = { page: navigation.route.page, params: { ...navigation.route.params, ws: workspaceId } };
      pendingWorkspace.current = '';
      replaceRoute(route.page, route.params);
      setNavigation(previous => ({ route, nonce: previous.nonce + 1 }));
    }
    if (wanted) {
      pendingWorkspace.current = '';
    } else if (previousWorkspace.current && previousWorkspace.current !== workspaceId) {
      const route = { page: navigation.route.page, params: { ws: workspaceId } };
      replaceRoute(route.page, route.params);
      setNavigation(previous => ({ route, nonce: previous.nonce + 1 }));
    }
    previousWorkspace.current = workspaceId;
    if (!readRoute().params.ws) replaceRoute(navigation.route.page, { ...readRoute().params, ws: workspaceId });
  }, [workspaceId, workspaces, navigation.nonce]);

  function navigate(page: PageName, params: Record<string, string> = {}) {
    if (readRoute().page === page && !Object.keys(params).length) return true;
    if (!canLeave()) return false;
    const route = { page, params: { ws: live.current.workspaceId, ...params } };
    if (readRoute().page === page) replaceRoute(page, route.params);
    else pushRoute(page, route.params);
    apply(route);
    return true;
  }

  return { page: navigation.route.page as PageName, navigation, navigate };
}
