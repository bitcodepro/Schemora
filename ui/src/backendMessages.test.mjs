import test from 'node:test';
import assert from 'node:assert/strict';
import { errorMessages, setLocale, tError, tMessage } from './i18n.ts';
import { fixedMessageCodes, serverErrors } from './serverErrors.mjs';

// The UI translates a server error by its code when the code always carries one fixed message (src/locales/errors.json),
// and otherwise by matching the prose against the locale catalogs. Either way, a new error must not reach the user in
// English only.
test('every server error message shown in the UI has a translation', () => {
  const fixed = fixedMessageCodes();
  const missing = [];
  let checked = 0;
  setLocale('uk');
  try {
    for (const { file, code, message: literal, interpolated } of serverErrors()) {
      let index = 0;
      const message = interpolated ? literal.replace(/\{[^{}]+\}/g, () => `Sample${index++}`) : literal;
      checked++;
      if (tError(code, message) === message) missing.push(`${file} ${code}: ${message}`);
      else if (fixed.has(code) && !Object.hasOwn(errorMessages, 'error.' + code))
        missing.push(`${file} ${code}: run npm run i18n:errors to key this message by its code`);
    }
  } finally {
    setLocale('en');
  }
  assert.ok(checked > 200, 'expected to find the server messages; check the source path');
  assert.deepEqual(missing, []);
});

test('a code-keyed translation wins over prose and survives a reworded message', () => {
  const [code, message] = [...fixedMessageCodes()].find(([key]) => Object.hasOwn(errorMessages, 'error.' + key));
  setLocale('ru');
  try {
    assert.equal(tError(code, message), errorMessages['error.' + code].ru);
    assert.equal(tError(code, message + ' (reworded on the server)'), errorMessages['error.' + code].ru);
    assert.equal(tError('NOT_A_CODE', message), tMessage(message));
  } finally {
    setLocale('en');
  }
});
