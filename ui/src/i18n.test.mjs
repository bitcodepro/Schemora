import test from 'node:test';
import assert from 'node:assert/strict';
import {
  initialLocale,
  normalizeLocale,
  isLocale,
  messages,
  supportedLocales,
  t,
  tMessage,
  getLocale,
  localeTag,
  setLocale,
  subscribeLocale,
  getLocaleRevision,
  languageStorageKey,
} from './i18n.ts';

test('English is the default independent of browser locale; invalid or unavailable storage cannot change it', () => {
  assert.equal(initialLocale(), 'en');
  assert.equal(initialLocale({ getItem: () => null }), 'en');
  assert.equal(initialLocale({ getItem: () => 'de' }), 'en');
  assert.equal(
    initialLocale({
      getItem: () => {
        throw new Error('blocked');
      },
    }),
    'en',
  );
  assert.equal(initialLocale({ getItem: key => (key === languageStorageKey ? 'uk' : null) }), 'uk');
  assert.equal(normalizeLocale('ru-RU'), 'en');
  assert.equal(isLocale('uk'), true);
  assert.equal(isLocale('__proto__'), false);
});
test('every catalog message has three nonempty translations with identical parameter placeholders', () => {
  assert.ok(Object.keys(messages).length > 700);
  const parameters = value => [...value.matchAll(/\{(\d+)\}/g)].map(match => match[1]).sort();
  for (const [key, entry] of Object.entries(messages)) {
    for (const locale of supportedLocales) {
      assert.equal(typeof entry[locale], 'string', key + ' ' + locale);
      assert.ok(entry[locale].trim(), key + ' ' + locale);
      assert.deepEqual(parameters(entry[locale]), parameters(key), key + ' ' + locale);
    }
  }
});
test('switching languages updates application labels and date/number locales', () => {
  for (const [locale, label, tag] of [
    ['en', 'Interface language', 'en-US'],
    ['ru', 'Язык интерфейса', 'ru-RU'],
    ['uk', 'Мова інтерфейсу', 'uk-UA'],
  ]) {
    setLocale(locale);
    assert.equal(getLocale(), locale);
    assert.equal(t('Interface language'), label);
    assert.equal(localeTag(), tag);
  }
  setLocale('en');
});
test('locale notifications update subscribers without replacing component state', () => {
  setLocale('en');
  let changed = 0;
  const before = getLocaleRevision();
  const stop = subscribeLocale(() => changed++);
  setLocale('en');
  assert.equal(changed, 0);
  setLocale('ru');
  assert.equal(changed, 1);
  assert.ok(getLocaleRevision() > before);
  stop();
  setLocale('uk');
  assert.equal(changed, 1);
  setLocale('en');
});
test('formatted messages can be retranslated after a locale change without translating their values', () => {
  setLocale('en');
  const original = t('Ошибка запроса ({0})', 'Рабочая область');
  assert.equal(original, 'Request failed (Рабочая область)');
  setLocale('uk');
  assert.equal(tMessage(original), 'Помилка запиту (Рабочая область)');
  setLocale('ru');
  assert.equal(tMessage(original), 'Ошибка запроса (Рабочая область)');
  setLocale('en');
});
test('literal text and escaped output remain text; SQL, identifiers and user content are not translated', () => {
  setLocale('uk');
  const sql = 'SELECT [Язык] FROM [dbo].[Workspace] WHERE [Value] IN ({{ids:Ids}});';
  assert.equal(tMessage(sql), sql);
  assert.equal(tMessage('Мій власний опис — <b>не HTML</b>'), 'Мій власний опис — <b>не HTML</b>');
  assert.equal(t('Ошибка запроса ({0})', '<script>example</script>'), 'Помилка запиту (<script>example</script>)');
  setLocale('en');
});
test('existing status dictionaries resolve live rather than freezing the startup language', () => {
  setLocale('en');
  const label = t('Подтверждена');
  assert.ok(!/[А-Яа-я]/.test(label));
  setLocale('uk');
  assert.equal(t(label), messages['Подтверждена'].uk);
  setLocale('ru');
  assert.equal(t(label), 'Подтверждена');
  setLocale('en');
});
test('unknown external tooling diagnostics stay intact and oversized messages do not enter template matching', () => {
  setLocale('ru');
  const native = 'MSBuild native failure XYZ0123 at C:\\Build\\foo.sql(1,1)';
  assert.equal(tMessage(native), native);
  const long = 'X'.repeat(20000);
  assert.equal(tMessage(long), long);
  assert.equal(tMessage(undefined), '');
  setLocale('en');
});

test('generic date/reason fragments cannot capture unrelated diagnostics or user text', () => {
  setLocale('uk');
  const value = 'Unknown component at a custom location: user description';
  assert.equal(tMessage(value), value);
  setLocale('en');
});
test('concatenated application diagnostics use a complete template and preserve object identifiers', () => {
  setLocale('en');
  const key = 'БД рецепта больше нет в текущем каталоге: {0}';
  assert.equal(tMessage('БД рецепта больше нет в текущем каталоге: db-123'), t(key, 'db-123'));
  const changed = 'Изменилось определение используемого объекта {0}.{1}. Проверьте SQL и сохраните новую версию.';
  assert.equal(
    tMessage('Изменилось определение используемого объекта dbo.MyView. Проверьте SQL и сохраните новую версию.'),
    t(changed, 'dbo', 'MyView'),
  );
  setLocale('en');
});
test('every literal t() key used by the UI has a catalog entry', async () => {
  const fs = await import('node:fs');
  const directory = new URL('.', import.meta.url);
  const known = new Set(Object.keys(messages));
  for (const entry of Object.values(messages)) known.add(entry.en);
  const missing = [];
  for (const file of fs.readdirSync(directory).filter(name => /\.tsx?$/.test(name))) {
    const text = fs.readFileSync(new URL(file, directory), 'utf8');
    for (const match of text.matchAll(/\bt\(\s*(['"])((?:\\.|(?!\1).)*)\1\s*[,)]/g)) {
      const key = match[2].replace(/\\(['"\\])/g, '$1');
      if (!known.has(key)) missing.push(file + ': ' + key);
    }
  }
  assert.deepEqual(missing, []);
});
