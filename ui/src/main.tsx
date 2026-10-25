import './productImprovements.css';
import { StrictMode } from 'react';
import { createRoot } from 'react-dom/client';
import App from './App';
import { WorkspaceProvider } from './workspaceContext';
import './styles.css';

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <WorkspaceProvider>
      <App />
    </WorkspaceProvider>
  </StrictMode>,
);

import './light.css';
// Page styles share classes (.field-label, .copy-state, …); load them all up front so the cascade does not depend on which page was opened first.
import './relationships.css';
import './recipes.css';
import './changes.css';
import './settings.css';
