import { useEffect, useRef, useState } from 'react';
import { t } from './i18n.ts';
import { useLocale } from './i18nReact';

const shortcuts: { keys: string[]; action: string; where: string }[] = [
  { keys: ['Ctrl', 'K'], action: 'Search the workspace and jump anywhere', where: 'Everywhere' },
  { keys: ['?'], action: 'Show this list', where: 'Everywhere, outside text fields' },
  { keys: ['Esc'], action: 'Close a dialog or search', where: 'Dialogs' },
  { keys: ['Ctrl', 'S'], action: 'Save the recipe definition', where: 'SQL recipes' },
  { keys: ['Ctrl', 'Enter'], action: 'Generate SQL from the checked inputs', where: 'SQL recipes' },
  { keys: ['Tab'], action: 'Move between controls; every action is reachable without a mouse', where: 'Everywhere' },
];

function typing(target: EventTarget | null) {
  return (
    target instanceof HTMLElement &&
    (target.isContentEditable || target.closest('input, textarea, select, .monaco-editor, [role="combobox"]') !== null)
  );
}

/** Keyboard reference opened with "?" (Shift+/ on most layouts) or from the header button. */
export default function ShortcutsHelp() {
  useLocale();
  const [open, setOpen] = useState(false);
  const close = useRef<HTMLButtonElement | null>(null);
  const returnFocus = useRef<HTMLElement | null>(null);
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== '?' || event.ctrlKey || event.metaKey || event.altKey || typing(event.target)) return;
      event.preventDefault();
      show();
    };
    const onRequest = () => show();
    window.addEventListener('keydown', onKey);
    window.addEventListener('schemora-open-shortcuts', onRequest);
    return () => {
      window.removeEventListener('keydown', onKey);
      window.removeEventListener('schemora-open-shortcuts', onRequest);
    };
  }, []);
  // Focus moves in only once the dialog is in the DOM.
  useEffect(() => {
    if (open) close.current?.focus();
  }, [open]);
  function show() {
    returnFocus.current = document.activeElement instanceof HTMLElement ? document.activeElement : null;
    setOpen(true);
  }
  function hide() {
    setOpen(false);
    returnFocus.current?.focus();
  }
  if (!open) return null;
  return (
    <div className="palette-backdrop" onMouseDown={event => event.target === event.currentTarget && hide()}>
      <div
        className="palette shortcuts-help"
        role="dialog"
        aria-modal="true"
        aria-labelledby="shortcuts-title"
        onKeyDown={event => {
          if (event.key === 'Escape') {
            event.preventDefault();
            hide();
          } else if (event.key === 'Tab') {
            // The only focusable control is the close button, so focus stays inside the dialog.
            event.preventDefault();
            close.current?.focus();
          }
        }}
      >
        <div className="section-heading">
          <h2 id="shortcuts-title">{t('Keyboard shortcuts')}</h2>
          <button ref={close} className="icon-button" aria-label={t('Закрыть')} onClick={hide}>
            ×
          </button>
        </div>
        <table className="shortcuts-table">
          <tbody>
            {shortcuts.map(item => (
              <tr key={item.action}>
                <td>
                  {item.keys.map((key, index) => (
                    <span key={key}>
                      {index > 0 && ' + '}
                      <kbd>{key}</kbd>
                    </span>
                  ))}
                </td>
                <td>
                  <span>{t(item.action)}</span>
                  <small>{t(item.where)}</small>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
        <p className="palette-note">{t('On macOS use Cmd instead of Ctrl.')}</p>
      </div>
    </div>
  );
}
