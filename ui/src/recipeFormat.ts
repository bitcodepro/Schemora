import { localeTag } from './i18n.ts';
import type { RecipeEntry } from './recipeTypes';

export const formatDate = (value: string) => new Date(value).toLocaleString(localeTag());

export const statusLabels: Record<RecipeEntry['validation']['status'], string> = {
  Valid: 'Совместим',
  Warning: 'Есть замечания',
  Broken: 'Несовместим',
  NeedsReview: 'Нужна проверка',
};

export function saveDownload(contents: string, name: string, mime: string) {
  const url = URL.createObjectURL(new Blob([contents], { type: mime }));
  const anchor = document.createElement('a');
  anchor.href = url;
  anchor.download = name;
  anchor.click();
  URL.revokeObjectURL(url);
}

export function filename(name: string) {
  return name.replace(/[^a-zA-Z0-9а-яА-Я._-]/g, '_') || 'recipe';
}
