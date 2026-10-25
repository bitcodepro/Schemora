import { useState } from 'react';
import { request, errorMessage } from './api';
import { t } from './i18n';
import { useLocale } from './i18nReact';
import { useDisplayPath } from './workspaceContext';
export default function SourceLocation({ path, line }: { path: string; line?: number | null }) {
  useLocale();
  const [notice, setNotice] = useState('');
  const [busy, setBusy] = useState(false);
  const displayPath = useDisplayPath();
  return (
    <div className="source-location">
      <code title={path}>
        {displayPath(path)}
        {line ? `:${line}` : ''}
      </code>
      <div>
        <button
          className="text-button"
          onClick={() =>
            void navigator.clipboard
              .writeText(path + (line ? `:${line}` : ''))
              .then(() => setNotice(t('Source location copied.')))
              .catch(e => setNotice(errorMessage(e, t('Copy failed.'))))
          }
        >
          {t('Copy location')}
        </button>
        <select
          aria-label={t('Source editor')}
          defaultValue=""
          disabled={busy}
          onChange={e => {
            const editor = e.target.value;
            e.target.value = '';
            if (!editor) return;
            setBusy(true);
            void request('/api/source/open', { method: 'POST', body: JSON.stringify({ path, line, editor }) })
              .then(() => setNotice(t('Source opened in editor.')))
              .catch(e => setNotice(errorMessage(e, t('Unable to open source.'))))
              .finally(() => setBusy(false));
          }}
        >
          <option value="">{t('Open in editor')}</option>
          <option value="vscode">VS Code</option>
          <option value="visualstudio">Visual Studio</option>
        </select>
      </div>
      {notice && <small role="status">{notice}</small>}
    </div>
  );
}
