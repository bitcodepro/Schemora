export type ComparedChange = { kind: string; schema: string; table: string; column?: string; before?: string; after?: string };

const cell = (value: string) => (/[",\r\n;]/.test(value) ? `"${value.replaceAll('"', '""')}"` : value);

/** CSV for spreadsheets: UTF-8 with BOM so Excel keeps Cyrillic names, CRLF rows, RFC 4180 quoting. */
export function comparisonCsv(
  changes: readonly ComparedChange[],
  headings: { kind: string; object: string; base: string; compared: string },
  kindLabel: (kind: string) => string,
): string {
  const rows = [
    [headings.kind, headings.object, headings.base, headings.compared],
    ...changes.map(change => [
      kindLabel(change.kind),
      `${change.schema}.${change.table}${change.column ? '.' + change.column : ''}`,
      change.before || '',
      change.after || '',
    ]),
  ];
  return '﻿' + rows.map(row => row.map(cell).join(',')).join('\r\n') + '\r\n';
}

const escapeHtml = (value: string) =>
  value.replaceAll('&', '&amp;').replaceAll('<', '&lt;').replaceAll('>', '&gt;').replaceAll('"', '&quot;').replaceAll("'", '&#39;');

/** A self-contained, printable HTML report: no scripts, no external assets, every value escaped. */
export function comparisonHtml(
  changes: readonly ComparedChange[],
  report: {
    lang: string;
    title: string;
    subtitle: string;
    summary: string;
    headings: { kind: string; object: string; base: string; compared: string };
  },
  kindLabel: (kind: string) => string,
): string {
  const counts = new Map<string, number>();
  for (const change of changes) counts.set(change.kind, (counts.get(change.kind) || 0) + 1);
  const tone = (kind: string) => (/removed/i.test(kind) ? 'removed' : /added/i.test(kind) ? 'added' : 'changed');
  const rows = changes
    .map(
      change =>
        `<tr><td><span class="${tone(change.kind)}">${escapeHtml(kindLabel(change.kind))}</span></td>` +
        `<td><code>${escapeHtml(`${change.schema}.${change.table}${change.column ? '.' + change.column : ''}`)}</code></td>` +
        `<td><code>${escapeHtml(change.before || '—')}</code></td><td><code>${escapeHtml(change.after || '—')}</code></td></tr>`,
    )
    .join('\n');
  const totals = [...counts]
    .sort(([a], [b]) => a.localeCompare(b))
    .map(([kind, count]) => `<li><span class="${tone(kind)}">${escapeHtml(kindLabel(kind))}</span> ${count}</li>`)
    .join('');
  return `<!doctype html>
<html lang="${escapeHtml(report.lang)}">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>${escapeHtml(report.title)}</title>
<style>
body{margin:32px;font:14px/1.5 'Segoe UI',Arial,sans-serif;color:#1d2a33;background:#fff}
h1{font-size:22px;margin:0 0 4px}p{margin:0 0 16px;color:#4d6170}
ul{display:flex;flex-wrap:wrap;gap:8px 18px;padding:0;margin:0 0 20px;list-style:none}
table{width:100%;border-collapse:collapse}th,td{text-align:left;vertical-align:top;padding:7px 9px;border-bottom:1px solid #d9e2e8}
th{background:#f1f5f8;font-weight:600}code{font:12px Consolas,monospace;white-space:pre-wrap;word-break:break-word}
span.added,span.removed,span.changed{display:inline-block;padding:1px 7px;border-radius:9px;font-size:12px}
.added{background:#dff3e6;color:#1b6b3a}.removed{background:#fbe1e1;color:#8f2525}.changed{background:#fdf0d5;color:#7a5410}
@media print{body{margin:12mm}tr{break-inside:avoid}}
</style>
</head>
<body>
<h1>${escapeHtml(report.title)}</h1>
<p>${escapeHtml(report.subtitle)}</p>
<p>${escapeHtml(report.summary)}</p>
<ul>${totals}</ul>
<table>
<thead><tr><th>${escapeHtml(report.headings.kind)}</th><th>${escapeHtml(report.headings.object)}</th><th>${escapeHtml(report.headings.base)}</th><th>${escapeHtml(report.headings.compared)}</th></tr></thead>
<tbody>
${rows}
</tbody>
</table>
</body>
</html>
`;
}
