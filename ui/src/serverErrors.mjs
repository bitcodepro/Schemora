import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

// Shared by the translation tests and scripts/generate-error-catalog.mjs: every AppException/RecipeEngineException the
// host can throw, with its code and message as written in the C# source.
const sourceRoot = fileURLToPath(new URL('../../src/', import.meta.url));
export const consoleOnlyCodes = /^LAUNCH_/;

function csharpFiles(directory) {
  return fs.readdirSync(directory, { withFileTypes: true }).flatMap(entry => {
    if (['bin', 'obj'].includes(entry.name)) return [];
    const full = path.join(directory, entry.name);
    return entry.isDirectory() ? csharpFiles(full) : entry.name.endsWith('.cs') ? [full] : [];
  });
}

/** @returns {{ file: string, code: string, message: string, interpolated: boolean }[]} */
export function serverErrors() {
  const thrown = /new (?:AppException|RecipeEngineException)\(\s*"([A-Z0-9_]+)"\s*,\s*(\$?)"((?:\\.|[^"\\])*)"/g;
  const found = [];
  for (const file of csharpFiles(sourceRoot))
    for (const [, code, interpolated, literal] of fs.readFileSync(file, 'utf8').matchAll(thrown)) {
      if (consoleOnlyCodes.test(code)) continue;
      found.push({
        file: path.basename(file),
        code,
        message: literal.replace(/\\"/g, '"').replace(/\\\\/g, '\\'),
        interpolated: Boolean(interpolated),
      });
    }
  return found;
}

/** Codes thrown with exactly one fixed message everywhere: these can be translated by code instead of by wording. */
export function fixedMessageCodes() {
  const byCode = new Map();
  for (const error of serverErrors()) {
    const known = byCode.get(error.code) || { messages: new Set(), interpolated: false };
    known.messages.add(error.message);
    known.interpolated ||= error.interpolated;
    byCode.set(error.code, known);
  }
  return new Map(
    [...byCode]
      .filter(([, value]) => !value.interpolated && value.messages.size === 1)
      .map(([code, value]) => [code, [...value.messages][0]]),
  );
}
