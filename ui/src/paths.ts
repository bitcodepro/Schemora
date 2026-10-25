/** Shortens an absolute path to its place under the current workspace roots; paths outside them stay unchanged. */
export function relativeToRoots(path: string, roots: readonly string[]): string {
  const normalize = (value: string) => value.replaceAll('\\', '/').replace(/\/+$/, '').toLocaleLowerCase();
  const target = normalize(path);
  let best = '';
  for (const root of roots) {
    const candidate = normalize(root);
    if ((target === candidate || target.startsWith(candidate + '/')) && candidate.length > best.length) best = candidate;
  }
  if (!best) return path;
  const original = path.replaceAll('\\', '/').replace(/\/+$/, '');
  const rest = original.slice(best.length).replace(/^\//, '');
  const rootName = original.slice(0, best.length).split('/').pop() || best;
  const shown = roots.length > 1 || !rest ? (rest ? rootName + '/' + rest : rootName) : rest;
  return path.includes('\\') ? shown.replaceAll('/', '\\') : shown;
}
