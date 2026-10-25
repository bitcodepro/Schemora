import { decodeRecipeImport, recipeForSave } from './recipeTypes.ts';
import type { RecipeDefinition } from './recipeTypes.ts';
export const draftKey = (workspace: string) => 'schemora.authoring-draft:' + workspace;
export function encodeDraft(recipe: RecipeDefinition, imported: boolean) {
  return JSON.stringify({ format: 1, savedAt: new Date().toISOString(), imported, definition: recipeForSave(recipe) });
}
export function decodeDraft(text: string) {
  const value = JSON.parse(text);
  if (value.format !== 1 || typeof value.savedAt !== 'string') throw Error('Invalid draft');
  return { definition: decodeRecipeImport(value.definition), imported: value.imported === true, savedAt: value.savedAt as string };
}
export function compareRecipe(before: RecipeDefinition, after: RecipeDefinition) {
  return (['sqlTemplate', 'inputs', 'databaseIds', 'dependencies', 'name', 'description', 'tags', 'compatibilityLevel'] as const)
    .filter(key => JSON.stringify(before[key]) !== JSON.stringify(after[key]))
    .map(key => ({
      key,
      before: typeof before[key] === 'string' ? (before[key] as string) : JSON.stringify(before[key] ?? [], null, 2),
      after: typeof after[key] === 'string' ? (after[key] as string) : JSON.stringify(after[key] ?? [], null, 2),
    }));
}
