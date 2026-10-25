import { useEffect, useRef, useState } from 'react';
import { request, sessionToken } from './api';

export type RecipePicks = { favorites: string[]; recent: string[] };

const empty: RecipePicks = { favorites: [], recent: [] };
const recentLimit = 8;

function decode(value: unknown): RecipePicks {
  const ids = (list: unknown) => (Array.isArray(list) ? list.filter((id): id is string => typeof id === 'string').slice(0, 500) : []);
  const object = value && typeof value === 'object' ? (value as Partial<RecipePicks>) : {};
  return { favorites: ids(object.favorites), recent: ids(object.recent).slice(0, recentLimit) };
}

/** Favourite and recently opened recipes of a workspace, kept with the other interface settings on the host. */
export function useRecipePicks(workspaceId: string) {
  const [picks, setPicks] = useState<RecipePicks>(empty);
  const current = useRef(picks);
  current.current = picks;
  // Until the stored picks arrive, a write would replace them with an empty list; an early "opened" waits instead.
  const loaded = useRef(false);
  const pendingOpen = useRef<string | null>(null);
  const key = '/api/settings/' + encodeURIComponent('recipe-picks:' + workspaceId);
  useEffect(() => {
    setPicks(empty);
    loaded.current = false;
    pendingOpen.current = null;
    if (!workspaceId || !sessionToken) return;
    const controller = new AbortController();
    request<{ value: unknown }>(key, { signal: controller.signal })
      .then(result => {
        const stored = decode(result.value);
        current.current = stored;
        setPicks(stored);
        loaded.current = true;
        if (pendingOpen.current) recordOpened(pendingOpen.current);
        pendingOpen.current = null;
      })
      .catch(() => undefined);
    return () => controller.abort();
  }, [key]);
  function save(next: RecipePicks) {
    current.current = next;
    setPicks(next);
    // A lost write only forgets a star or a recent entry; it never blocks working with recipes.
    if (workspaceId && sessionToken) void request(key, { method: 'PUT', body: JSON.stringify({ value: next }) }).catch(() => undefined);
  }
  function recordOpened(recipeId: string) {
    if (!loaded.current) {
      pendingOpen.current = recipeId;
      return;
    }
    const { favorites, recent } = current.current;
    if (recent[0] === recipeId) return;
    save({ favorites, recent: [recipeId, ...recent.filter(id => id !== recipeId)].slice(0, recentLimit) });
  }
  return {
    picks,
    recordOpened,
    toggleFavorite(recipeId: string) {
      if (!loaded.current) return;
      const { favorites, recent } = current.current;
      save({
        favorites: favorites.includes(recipeId) ? favorites.filter(id => id !== recipeId) : [...favorites, recipeId],
        recent,
      });
    },
  };
}
