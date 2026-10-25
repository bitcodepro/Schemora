import professionalNext from './locales/professional-next.json' with { type: 'json' };
import professional from './locales/professional.json' with { type: 'json' };
import experience from './locales/experience.json' with { type: 'json' };
import improvements from './locales/improvements.json' with { type: 'json' };
import shell from './locales/shell.json' with { type: 'json' };
import graph from './locales/graph.json' with { type: 'json' };
import recipes from './locales/recipes.json' with { type: 'json' };
import common from './locales/common.json' with { type: 'json' };
import backendConcat from './locales/backend-concat.json' with { type: 'json' };
import backendHost from './locales/backend-host.json' with { type: 'json' };
import backendGraph from './locales/backend-graph.json' with { type: 'json' };
import ux from './locales/ux.json' with { type: 'json' };
import errors from './locales/errors.json' with { type: 'json' };

export const supportedLocales = ['en', 'ru', 'uk'] as const;
export type Locale = (typeof supportedLocales)[number];
export type Message = Record<Locale, string>;
export const languageStorageKey = 'schemora.language';
export const localeNames: Record<Locale, string> = { en: 'English', ru: 'Русский', uk: 'Українська' };
const tags: Record<Locale, string> = { en: 'en-US', ru: 'ru-RU', uk: 'uk-UA' };
export const messages: Readonly<Record<string, Message>> = {
  ...professionalNext,
  ...professional,
  ...experience,
  ...shell,
  ...graph,
  ...recipes,
  ...common,
  ...backendHost,
  ...backendGraph,
  ...backendConcat,
  ...improvements,
  ...ux,
};
export function isLocale(value: unknown): value is Locale {
  return typeof value === 'string' && supportedLocales.includes(value as Locale);
}
export function normalizeLocale(value: unknown): Locale {
  return isLocale(value) ? value : 'en';
}
export function initialLocale(storage?: Pick<Storage, 'getItem'>): Locale {
  try {
    return normalizeLocale(storage?.getItem(languageStorageKey));
  } catch {
    return 'en';
  }
}
let locale: Locale = initialLocale(
  typeof window === 'undefined'
    ? undefined
    : (() => {
        try {
          return window.localStorage;
        } catch {
          return undefined;
        }
      })(),
);
let revision = 0;
const listeners = new Set<() => void>();
export const getLocale = () => locale;
export const getLocaleRevision = () => revision;
export const localeTag = () => tags[locale];
export function subscribeLocale(listener: () => void): () => void {
  listeners.add(listener);
  return () => {
    listeners.delete(listener);
  };
}
function updateDocument() {
  if (typeof document !== 'undefined') document.documentElement.lang = locale;
}
updateDocument();
export function setLocale(value: unknown): void {
  const next = normalizeLocale(value);
  revision++;
  try {
    if (typeof window !== 'undefined') window.localStorage.setItem(languageStorageKey, next);
  } catch {
    /* Server preference still persists when browser storage is unavailable. */
  }
  const changed = locale !== next;
  locale = next;
  updateDocument();
  if (changed) for (const listener of listeners) listener();
}
if (typeof window !== 'undefined')
  window.addEventListener('storage', event => {
    if (event.key === languageStorageKey) setLocale(event.newValue);
  });

const aliases = new Map<string, Message>();
for (const entry of Object.values(messages))
  for (const language of supportedLocales) if (!aliases.has(entry[language])) aliases.set(entry[language], entry);
const format = (text: string, values: readonly unknown[]) =>
  text.replace(/\{(\d+)\}/g, (token, index: string) => (Number(index) < values.length ? String(values[Number(index)] ?? '') : token));
export function t(key: string | null | undefined, ...values: readonly unknown[]): string {
  if (!key) return '';
  const entry = Object.hasOwn(messages, key) ? messages[key] : aliases.get(key);
  return format(entry ? entry[locale] : key, values);
}
const escapePattern = (text: string) => text.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
type Pattern = { regex: RegExp; entry: Message; indices: number[]; literalLength: number };
const patterns: Pattern[] = [];
for (const entry of Object.values(messages))
  for (const language of supportedLocales) {
    const source = entry[language];
    const indices: number[] = [];
    let start = 0;
    let pattern = '^';
    let literalLength = 0;
    for (const match of source.matchAll(/\{(\d+)\}/g)) {
      const literal = source.slice(start, match.index);
      literalLength += literal.length;
      pattern += escapePattern(literal) + '([\\s\\S]*?)';
      indices.push(Number(match[1]));
      start = match.index + match[0].length;
    }
    if (!indices.length) continue;
    literalLength += source.length - start;
    if (literalLength < 12) continue;
    pattern += escapePattern(source.slice(start)) + '$';
    patterns.push({ regex: new RegExp(pattern), entry, indices, literalLength });
  }
patterns.sort((a, b) => b.literalLength - a.literalLength);
/** Server errors whose code always carries one fixed message, translated by code (generated: npm run i18n:errors). */
export const errorMessages: Readonly<Record<string, Message>> = errors;

/** Translates a server error by its code when the code is keyed, so rewording the server text cannot untranslate it. */
export function tError(code: string | null | undefined, message: string | null | undefined): string {
  const keyed = code && Object.hasOwn(errorMessages, 'error.' + code) ? errorMessages['error.' + code] : undefined;
  return keyed ? keyed[locale] : tMessage(message);
}
// Only use for application-owned messages. SQL, names, paths and user-authored text remain untouched.
export function tMessage(value: string | null | undefined): string {
  if (!value) return '';
  const direct = Object.hasOwn(messages, value) ? messages[value] : aliases.get(value);
  if (direct) return direct[locale];
  if (value.length > 16384) return value;
  for (const { regex, entry, indices } of patterns) {
    const match = regex.exec(value);
    if (!match) continue;
    const values: string[] = [];
    let consistent = true;
    indices.forEach((index, position) => {
      if (values[index] !== undefined && values[index] !== match[position + 1]) consistent = false;
      values[index] = match[position + 1];
    });
    if (consistent) return format(entry[locale], values);
  }
  return value.includes('\n')
    ? value
        .split('\n')
        .map(line => tMessage(line))
        .join('\n')
    : value;
}
