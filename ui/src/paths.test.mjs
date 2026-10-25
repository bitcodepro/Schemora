import test from 'node:test';
import assert from 'node:assert/strict';
import { relativeToRoots } from './paths.ts';

test('paths under the single root are shown relative to it, keeping Windows separators', () => {
  assert.equal(relativeToRoots('C:\\Repos\\Shop\\Db\\Schema.sql', ['C:\\Repos\\Shop']), 'Db\\Schema.sql');
  assert.equal(relativeToRoots('c:\\repos\\shop\\Db\\Schema.sql', ['C:\\Repos\\Shop\\']), 'Db\\Schema.sql');
  assert.equal(relativeToRoots('C:\\Repos\\Shop', ['C:\\Repos\\Shop']), 'Shop');
});

test('several roots keep the root folder name; the deepest matching root wins', () => {
  const roots = ['E:\\Work', 'E:\\Work\\Billing', 'D:\\Other'];
  assert.equal(relativeToRoots('E:\\Work\\Billing\\Billing.sqlproj', roots), 'Billing\\Billing.sqlproj');
  assert.equal(relativeToRoots('E:\\Work\\Api\\Api.csproj', roots), 'Work\\Api\\Api.csproj');
});

test('paths outside the roots and sibling prefixes are left unchanged', () => {
  assert.equal(relativeToRoots('C:\\Repos\\ShopTools\\x.sql', ['C:\\Repos\\Shop']), 'C:\\Repos\\ShopTools\\x.sql');
  assert.equal(relativeToRoots('/srv/db/a.sql', []), '/srv/db/a.sql');
  assert.equal(relativeToRoots('/srv/db/a.sql', ['/srv']), 'db/a.sql');
});
