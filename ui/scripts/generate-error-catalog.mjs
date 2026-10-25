// Regenerates src/locales/errors.json: one entry per server error code that always carries the same fixed message, taken
// from the translation that message has today. Run after adding or rewording server errors: npm run i18n:errors
import fs from 'node:fs';
import { fixedMessageCodes } from '../src/serverErrors.mjs';
import { messages, setLocale, supportedLocales, tMessage } from '../src/i18n.ts';

const target = new URL('../src/locales/errors.json', import.meta.url);
const previous = fs.existsSync(target) ? JSON.parse(fs.readFileSync(target, 'utf8')) : {};
const catalog = {};
const untranslated = [];
for (const [code, message] of [...fixedMessageCodes()].sort(([a], [b]) => a.localeCompare(b))) {
  const entry = {};
  for (const locale of supportedLocales) {
    setLocale(locale);
    entry[locale] = tMessage(message);
  }
  setLocale('en');
  // A message the prose catalogs do not know keeps its earlier code entry, if there is one.
  if (supportedLocales.every(locale => entry[locale] === message) && !Object.hasOwn(messages, message)) {
    if (previous['error.' + code]) catalog['error.' + code] = previous['error.' + code];
    else untranslated.push(code + ': ' + message);
    continue;
  }
  catalog['error.' + code] = entry;
}
fs.writeFileSync(target, JSON.stringify(catalog, null, 2) + '\n');
console.log(`${Object.keys(catalog).length} error codes written to src/locales/errors.json`);
if (untranslated.length) {
  console.error('Add translations for:\n  ' + untranslated.join('\n  '));
  process.exitCode = 1;
}
