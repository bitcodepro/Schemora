import { useState } from 'react';
import { t } from './i18n.ts';
import { useLocale } from './i18nReact';
import { statusLabels } from './recipeFormat';
import type { RecipeEntry } from './recipeTypes';

type View = 'all' | 'favorites' | 'recent';

export default function RecipeCatalogList({
  entries,
  loading,
  busy,
  selectedId,
  favorites,
  recent,
  onOpen,
  onToggleFavorite,
}: {
  entries: RecipeEntry[];
  loading: boolean;
  busy: boolean;
  selectedId: string;
  favorites: string[];
  recent: string[];
  onOpen: (entry: RecipeEntry) => void;
  onToggleFavorite: (recipeId: string) => void;
}) {
  useLocale();
  const [query, setQuery] = useState('');
  const [tag, setTag] = useState('');
  const [view, setView] = useState<View>('all');
  const tags = [...new Set(entries.flatMap(entry => entry.definition.tags))].sort();
  const starred = new Set(favorites);
  const matching = entries.filter(
    entry =>
      (!tag || entry.definition.tags.includes(tag)) &&
      `${entry.definition.name} ${entry.definition.description} ${entry.definition.tags.join(' ')}`
        .toLocaleLowerCase()
        .includes(query.toLocaleLowerCase()),
  );
  // Favourites lead the full list; "Recent" keeps the order in which recipes were opened.
  const filtered =
    view === 'recent'
      ? recent.flatMap(id => matching.filter(entry => entry.definition.id === id))
      : view === 'favorites'
        ? matching.filter(entry => starred.has(entry.definition.id))
        : [...matching.filter(entry => starred.has(entry.definition.id)), ...matching.filter(entry => !starred.has(entry.definition.id))];
  const views: { value: View; label: string; count: number }[] = [
    { value: 'all', label: t('All'), count: entries.length },
    { value: 'favorites', label: t('Favorites'), count: entries.filter(entry => starred.has(entry.definition.id)).length },
    { value: 'recent', label: t('Recent'), count: recent.filter(id => entries.some(entry => entry.definition.id === id)).length },
  ];
  return (
    <aside className="recipe-catalog">
      <div className="section-heading">
        <h2>
          {t('Рецепты')} <span className="section-count">{entries.length}</span>
        </h2>
      </div>
      <div className="recipe-catalog-views" role="group" aria-label={t('Show recipes')}>
        {views.map(item => (
          <button key={item.value} type="button" aria-pressed={view === item.value} onClick={() => setView(item.value)}>
            {item.label} <span>{item.count}</span>
          </button>
        ))}
      </div>
      <input
        type="search"
        aria-label={t('Поиск рецептов')}
        value={query}
        onChange={event => setQuery(event.target.value)}
        placeholder={t('Название, описание, тег…')}
      />
      <select aria-label={t('Фильтр тега рецептов')} value={tag} onChange={event => setTag(event.target.value)}>
        <option value="">{t('Все теги')}</option>
        {tags.map(value => (
          <option key={value} value={value}>
            {value}
          </option>
        ))}
      </select>
      <div className="recipe-catalog-list">
        {loading ? (
          <p className="muted-text">{t('Загрузка рецептов…')}</p>
        ) : filtered.length ? (
          filtered.slice(0, 250).map(entry => {
            const favorite = starred.has(entry.definition.id);
            return (
              <div key={entry.definition.id} className={'recipe-catalog-item' + (selectedId === entry.definition.id ? ' selected' : '')}>
                <button className="recipe-catalog-open" disabled={busy} onClick={() => onOpen(entry)}>
                  <strong>{entry.definition.name}</strong>
                  <small>{entry.definition.description || t('Без описания')}</small>
                  <div>
                    <span className={`recipe-state ${entry.validation.status.toLowerCase()}`}>
                      {t(statusLabels[entry.validation.status])}
                    </span>
                    <em>v{entry.definition.version}</em>
                  </div>
                  {entry.definition.tags.length > 0 && <p>{entry.definition.tags.join(' · ')}</p>}
                </button>
                <button
                  type="button"
                  className={'recipe-favorite' + (favorite ? ' starred' : '')}
                  aria-pressed={favorite}
                  aria-label={t('Favorite: {0}', entry.definition.name)}
                  title={favorite ? t('Remove from favorites') : t('Add to favorites')}
                  onClick={() => onToggleFavorite(entry.definition.id)}
                >
                  {favorite ? '★' : '☆'}
                </button>
              </div>
            );
          })
        ) : (
          <p className="muted-text">
            {view === 'favorites' && !query && !tag
              ? t('Star recipes you use often to keep them here.')
              : view === 'recent' && !query && !tag
                ? t('Recipes you open appear here.')
                : entries.length
                  ? t('Рецепты по этому фильтру не найдены.')
                  : t('Создайте рецепт по таблице, SQL-файлу или доверенному JOIN-пути.')}
          </p>
        )}
      </div>
      {filtered.length > 250 && (
        <p className="workspace-limit-note">
          {t('Первые250 из')} {filtered.length}
          {t('; уточните поиск.')}
        </p>
      )}
    </aside>
  );
}
