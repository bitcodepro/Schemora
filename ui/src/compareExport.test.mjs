import test from 'node:test';
import assert from 'node:assert/strict';
import { comparisonCsv, comparisonHtml } from './compareExport.ts';

test('comparison CSV has a BOM, CRLF rows and RFC 4180 quoting', () => {
  const csv = comparisonCsv(
    [
      { kind: 'columnAdded', schema: 'dbo', table: 'Заказы', column: 'Note', after: 'nvarchar(100) NULL' },
      { kind: 'tableRemoved', schema: 'dbo', table: 'Old', before: 'Id int NOT NULL; Name "x", y' },
    ],
    { kind: 'Изменение', object: 'Объект', base: 'Repo', compared: 'Stage' },
    kind => (kind === 'columnAdded' ? 'Колонка добавлена' : kind),
  );
  assert.ok(csv.startsWith('﻿Изменение,Объект,Repo,Stage\r\n'));
  assert.ok(csv.includes('Колонка добавлена,dbo.Заказы.Note,,nvarchar(100) NULL\r\n'));
  assert.ok(csv.includes('tableRemoved,dbo.Old,"Id int NOT NULL; Name ""x"", y",\r\n'));
});

test('comparison HTML report escapes every value and needs nothing external', () => {
  const html = comparisonHtml(
    [
      { kind: 'columnAdded', schema: 'dbo', table: '<script>alert(1)</script>', column: 'Note', after: 'nvarchar(100) NULL' },
      { kind: 'tableRemoved', schema: 'dbo', table: 'Old', before: 'A & "B"' },
    ],
    {
      lang: 'ru',
      title: 'Сравнение <Repo>',
      subtitle: 'Repo → Stage',
      summary: 'Различий: 2',
      headings: { kind: 'Изменение', object: 'Объект', base: 'Repo', compared: 'Stage' },
    },
    kind => (kind === 'columnAdded' ? 'Колонка добавлена' : kind),
  );
  assert.ok(html.startsWith('<!doctype html>'));
  assert.ok(html.includes('<html lang="ru">'));
  assert.ok(html.includes('&lt;script&gt;alert(1)&lt;/script&gt;'));
  assert.ok(!html.includes('<script>'));
  assert.ok(html.includes('A &amp; &quot;B&quot;'));
  assert.ok(html.includes('<title>Сравнение &lt;Repo&gt;</title>'));
  assert.ok(html.includes('<span class="added">Колонка добавлена</span> 1'));
  assert.ok(!/\b(src|href)=/.test(html));
});
