import { useLocale } from './i18nReact';
import { useEffect, useRef } from 'react';
import * as monaco from 'monaco-editor/editor/editor.api.js';
import EditorWorker from 'monaco-editor/editor/editor.worker.js?worker';
import 'monaco-editor/languages/definitions/sql/register.js';
import 'monaco-editor/languages/definitions/csharp/register.js';
import 'monaco-editor/editor/contrib/find/browser/findController.js';
import 'monaco-editor/editor/contrib/bracketMatching/browser/bracketMatching.js';

// Every worker and grammar is emitted into ui/dist; no AMD/CDN loader is used.
globalThis.MonacoEnvironment = { globalAPI: false, getWorker: () => new EditorWorker() };
monaco.editor.defineTheme('schemora-dark', {
  base: 'vs-dark',
  inherit: true,
  rules: [
    { token: 'keyword', foreground: '82BAE0' },
    { token: 'string', foreground: '91C5A5' },
    { token: 'comment', foreground: '5C8391' },
    { token: 'number', foreground: 'C6BA86' },
  ],
  colors: {
    'editor.background': '#101E28',
    'editor.foreground': '#BED5E1',
    'editorLineNumber.foreground': '#4F7385',
    'editorLineNumber.activeForeground': '#83B4B6',
    'editor.selectionBackground': '#2E515E',
    'editorCursor.foreground': '#7AD4BA',
    'editor.lineHighlightBackground': '#182B35',
    'editorIndentGuide.background1': '#243E4A',
  },
});
monaco.editor.defineTheme('schemora-light', {
  base: 'vs',
  inherit: true,
  rules: [
    { token: 'keyword', foreground: '165890' },
    { token: 'string', foreground: '25734B' },
    { token: 'comment', foreground: '687D86' },
  ],
  colors: {
    'editor.background': '#F8FBFD',
    'editor.foreground': '#203C4D',
    'editorLineNumber.foreground': '#8AA0AE',
    'editor.selectionBackground': '#CEE3EC',
    'editor.lineHighlightBackground': '#EEF4F7',
  },
});
const currentTheme = () => (document.documentElement.dataset.theme === 'light' ? 'schemora-light' : 'schemora-dark');
window.addEventListener('schemora-theme', () => monaco.editor.setTheme(currentTheme()));
let modelSequence = 0;

type Props = {
  value: string;
  onChange?: (value: string) => void;
  readOnly?: boolean;
  language?: 'sql' | 'csharp';
  height?: number;
  label: string;
  matchText?: string;
};

export default function SqlEditor({ value, onChange, readOnly = false, language = 'sql', height = 330, label, matchText }: Props) {
  useLocale();
  const element = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneCodeEditor | null>(null);
  const model = useRef<monaco.editor.ITextModel | null>(null);
  const callback = useRef(onChange);
  callback.current = onChange;
  const applyingValue = useRef(false);

  useEffect(() => {
    if (!element.current) return;
    const instanceModel = monaco.editor.createModel(
      value,
      language,
      monaco.Uri.parse(`inmemory://schemora/model-${++modelSequence}.${language === 'sql' ? 'sql' : 'cs'}`),
    );
    const instance = monaco.editor.create(element.current, {
      model: instanceModel,
      theme: currentTheme(),
      readOnly,
      automaticLayout: true,
      accessibilitySupport: 'on',
      tabFocusMode: true,
      ariaLabel: label,
      minimap: { enabled: false },
      fontFamily: 'Consolas, "Cascadia Code", monospace',
      fontSize: 14,
      lineHeight: 22,
      padding: { top: 12, bottom: 12 },
      scrollBeyondLastLine: false,
      lineNumbersMinChars: 3,
      tabSize: 4,
      insertSpaces: true,
      wordWrap: 'on',
      wordBasedSuggestions: 'off',
      suggestSelection: 'first',
      suggest: { shareSuggestSelections: false, showWords: false },
      renderValidationDecorations: readOnly ? 'off' : 'on',
      stickyScroll: { enabled: false },
    });
    editor.current = instance;
    model.current = instanceModel;
    const changes = instanceModel.onDidChangeContent(() => {
      if (!applyingValue.current) callback.current?.(instanceModel.getValue());
    });
    return () => {
      changes.dispose();
      instance.dispose();
      instanceModel.dispose();
      editor.current = null;
      model.current = null;
    };
  }, []);

  useEffect(() => {
    if (!model.current || model.current.getValue() === value) return;
    applyingValue.current = true;
    model.current.setValue(value);
    applyingValue.current = false;
  }, [value]);
  useEffect(() => {
    editor.current?.updateOptions({ readOnly, ariaLabel: label });
  }, [readOnly, label]);
  useEffect(() => {
    if (model.current) monaco.editor.setModelLanguage(model.current, language);
  }, [language]);

  useEffect(() => {
    if (!matchText || !model.current || !editor.current) return;
    const match = model.current.findMatches(matchText, false, false, false, null, false, 1)[0];
    if (match) {
      editor.current.setSelection(match.range);
      editor.current.revealRangeInCenter(match.range);
    }
  }, [matchText, value]);
  const fittedHeight = Math.min(height, Math.max(150, value.split('\n').length * 22 + 34));
  return <div ref={element} style={{ height: fittedHeight }} className="sql-editor" aria-label={label} />;
}

export function SqlDiffEditor({ before, after, label, height = 390 }: { before: string; after: string; label: string; height?: number }) {
  useLocale();
  const element = useRef<HTMLDivElement | null>(null);
  const editor = useRef<monaco.editor.IStandaloneDiffEditor | null>(null);
  const models = useRef<{ original: monaco.editor.ITextModel; modified: monaco.editor.ITextModel } | null>(null);
  useEffect(() => {
    if (!element.current) return;
    const id = ++modelSequence;
    const original = monaco.editor.createModel(before, 'sql', monaco.Uri.parse(`inmemory://schemora/diff-${id}-before.sql`));
    const modified = monaco.editor.createModel(after, 'sql', monaco.Uri.parse(`inmemory://schemora/diff-${id}-after.sql`));
    const instance = monaco.editor.createDiffEditor(element.current, {
      theme: currentTheme(),
      readOnly: true,
      originalEditable: false,
      automaticLayout: true,
      renderSideBySide: true,
      ignoreTrimWhitespace: false,
      fontFamily: 'Consolas, monospace',
      fontSize: 13,
      lineHeight: 21,
      minimap: { enabled: false },
      scrollBeyondLastLine: false,
      ariaLabel: label,
      wordWrap: 'on',
    });
    instance.setModel({ original, modified });
    editor.current = instance;
    models.current = { original, modified };
    return () => {
      instance.dispose();
      original.dispose();
      modified.dispose();
      editor.current = null;
      models.current = null;
    };
  }, []);
  useEffect(() => {
    if (models.current?.original.getValue() !== before) models.current?.original.setValue(before);
    if (models.current?.modified.getValue() !== after) models.current?.modified.setValue(after);
  }, [before, after]);
  useEffect(() => {
    editor.current?.updateOptions({ ariaLabel: label });
  }, [label]);
  return <div ref={element} className="sql-editor" style={{ height }} aria-label={label} />;
}
