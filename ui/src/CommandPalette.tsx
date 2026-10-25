import { useEffect, useRef, useState } from 'react';
import { errorMessage, request } from './api';
import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import { useWorkspaces } from './workspaceContext';

type Hit = { id: string; kind: string; name: string; databaseId?: string; detail: string };
type Page = { items: Hit[]; total: number; kinds?: Record<string, number> };
const pageSize = 20;
const kindFilters = ['table', 'view', 'column', 'sqlCode', 'recipe'];
export type PaletteTarget =
  | { kind: 'page'; page: string }
  | { kind: 'recipe'; recipeId: string }
  | { kind: 'object'; databaseId: string; objectId: string; matchText?: string };
type Entry = { key: string; label: string; detail: string; target: PaletteTarget };

/** Ctrl+K / Cmd+K search across the selected workspace: pages, schema objects, columns, SQL code and recipes. */
export default function CommandPalette({
  pages,
  onOpen,
}: {
  pages: { page: string; label: string }[];
  onOpen: (target: PaletteTarget) => void;
}) {
  useLocale();
  const { workspaceId } = useWorkspaces();
  const [open, setOpen] = useState(false);
  const [query, setQuery] = useState('');
  const [hits, setHits] = useState<Hit[]>([]);
  const [total, setTotal] = useState(0);
  const [kinds, setKinds] = useState<Record<string, number>>({});
  const [kind, setKind] = useState('');
  const [limit, setLimit] = useState(pageSize);
  const [active, setActive] = useState(0);
  const [error, setError] = useState('');
  const input = useRef<HTMLInputElement | null>(null);
  const dialog = useRef<HTMLDivElement | null>(null);
  const returnFocus = useRef<HTMLElement | null>(null);

  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      // Monaco uses Ctrl+K as a chord prefix, so the editor keeps the shortcut.
      if (event.target instanceof Element && event.target.closest('.monaco-editor')) return;
      if ((event.ctrlKey || event.metaKey) && !event.altKey && event.key.toLowerCase() === 'k') {
        event.preventDefault();
        show();
      }
    };
    const onRequest = () => show();
    window.addEventListener('keydown', onKey);
    window.addEventListener('schemora-open-search', onRequest);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('schemora-open-search', onRequest);
    };
  }, []);

  function show() {
    if (!dialog.current?.contains(document.activeElement))
      returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setOpen(true);
    setActive(0);
    setTimeout(() => input.current?.select(), 0);
  }
  function close() {
    setOpen(false);
    returnFocus.current?.focus();
  }

  const trimmed = query.trim();
  useEffect(() => {
    if (!open || !workspaceId || trimmed.length < 2) {
      setHits([]);
      setTotal(0);
      setKinds({});
      setError('');
      return;
    }
    const controller = new AbortController();
    const timer = setTimeout(() => {
      const parameters = new URLSearchParams({ query: trimmed, offset: '0', limit: String(limit), kind });
      request<Page>('/api/workspaces/' + encodeURIComponent(workspaceId) + '/search?' + parameters, { signal: controller.signal })
        .then(page => {
          if (controller.signal.aborted) return;
          setHits(page.items);
          setTotal(page.total);
          setKinds(page.kinds || {});
          setError('');
          if (limit === pageSize) setActive(0);
        })
        .catch(cause => {
          if (!controller.signal.aborted) setError(errorMessage(cause, t('Search failed.')));
        });
    }, 180);
    return () => {
      clearTimeout(timer);
      controller.abort();
    };
  }, [open, workspaceId, trimmed, kind, limit]);
  useEffect(() => setLimit(pageSize), [trimmed, kind]);

  const lower = trimmed.toLocaleLowerCase();
  const entries: Entry[] = [
    ...pages
      .filter(item => !lower || item.label.toLocaleLowerCase().includes(lower))
      .map(item => ({
        key: 'page:' + item.page,
        label: item.label,
        detail: t('Page'),
        target: { kind: 'page', page: item.page } as PaletteTarget,
      })),
    ...hits
      .filter(hit => hit.kind === 'recipe' || hit.databaseId)
      .map((hit, index) => ({
        key: hit.kind + ':' + hit.id + ':' + index,
        label: hit.name,
        detail: t('Kind: ' + hit.kind) + (hit.kind === 'column' && hit.detail ? ' · ' + hit.detail : ''),
        target:
          hit.kind === 'recipe'
            ? ({ kind: 'recipe', recipeId: hit.id } as PaletteTarget)
            : ({
                kind: 'object',
                databaseId: hit.databaseId!,
                objectId: hit.id,
                matchText: hit.kind === 'sqlCode' ? trimmed : undefined,
              } as PaletteTarget),
      })),
  ];

  function choose(entry: Entry | undefined) {
    if (!entry) return;
    setOpen(false);
    onOpen(entry.target);
  }

  if (!open) return null;
  return (
    <div className="palette-backdrop" onMouseDown={event => event.target === event.currentTarget && close()}>
      <div
        ref={dialog}
        className="palette"
        role="dialog"
        aria-modal="true"
        aria-label={t('Search the workspace')}
        onKeyDown={event => {
          if (event.key === 'Escape') {
            event.preventDefault();
            event.stopPropagation();
            close();
          } else if (event.key === 'Tab') {
            const controls = Array.from(dialog.current?.querySelectorAll<HTMLElement>('input, button:not(:disabled)') || []);
            const next = controls[0],
              last = controls[controls.length - 1];
            if (event.shiftKey && document.activeElement === next) {
              event.preventDefault();
              last?.focus();
            } else if (!event.shiftKey && document.activeElement === last) {
              event.preventDefault();
              next?.focus();
            }
          }
        }}
      >
        <input
          ref={input}
          type="search"
          role="combobox"
          aria-expanded="true"
          aria-controls="palette-results"
          aria-activedescendant={entries[active] ? 'palette-' + active : undefined}
          aria-label={t('Search the workspace')}
          placeholder={t('Tables, columns, SQL code, recipes, pages…')}
          value={query}
          onChange={event => setQuery(event.target.value)}
          onKeyDown={event => {
            if (event.key === 'ArrowDown') {
              event.preventDefault();
              setActive(value => Math.min(value + 1, entries.length - 1));
            } else if (event.key === 'ArrowUp') {
              event.preventDefault();
              setActive(value => Math.max(value - 1, 0));
            } else if (event.key === 'Enter' && (event.ctrlKey || event.metaKey)) {
              event.preventDefault();
              setLimit(value => Math.min(100, value + pageSize));
            } else if (event.key === 'Enter') {
              event.preventDefault();
              choose(entries[active]);
            }
          }}
        />
        {trimmed.length >= 2 && Object.keys(kinds).length > 0 && (
          <div className="palette-kinds" role="group" aria-label={t('Filter results by kind')}>
            {['', ...kindFilters.filter(item => kinds[item])].map(item => (
              <button
                key={item || 'all'}
                type="button"
                aria-pressed={kind === item}
                onMouseDown={event => event.preventDefault()}
                onClick={() => {
                  setKind(item);
                  input.current?.focus();
                }}
              >
                {item ? t('Kind: ' + item) : t('All')}{' '}
                <span>{item ? kinds[item] : Object.values(kinds).reduce((sum, count) => sum + count, 0)}</span>
              </button>
            ))}
          </div>
        )}
        {error && (
          <p className="palette-note" role="alert">
            {error}
          </p>
        )}
        <ul id="palette-results" role="listbox" aria-label={t('Search results')}>
          {entries.map((entry, index) => (
            <li
              key={entry.key}
              id={'palette-' + index}
              role="option"
              aria-selected={index === active}
              className={index === active ? 'active' : ''}
              onMouseEnter={() => setActive(index)}
              onMouseDown={event => event.preventDefault()}
              onClick={() => choose(entry)}
            >
              <span>{entry.label}</span>
              <small>{entry.detail}</small>
            </li>
          ))}
        </ul>
        {total > hits.length && trimmed.length >= 2 && (
          <button
            type="button"
            className="text-button palette-more"
            onMouseDown={event => event.preventDefault()}
            onClick={() => {
              setLimit(value => Math.min(100, value + pageSize));
              input.current?.focus();
            }}
            disabled={limit >= 100}
          >
            {limit >= 100 ? t('Refine the search to see other matches.') : t('Show more')}
          </button>
        )}
        <p className="palette-note">
          {!workspaceId
            ? t('Create or choose a workspace to search its schema.')
            : trimmed.length < 2
              ? t('Type at least two characters.')
              : total > hits.length
                ? t('Showing {0} of {1} matches.', hits.length, total) + ' ' + t('Press Ctrl+Enter or click for more.')
                : !entries.length
                  ? t('Nothing found.')
                  : t('↑ ↓ to choose, Enter to open, Esc to close.')}
        </p>
      </div>
    </div>
  );
}
