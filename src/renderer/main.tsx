import '@fontsource/ibm-plex-sans/latin-400.css';
import '@fontsource/ibm-plex-sans/latin-500.css';
import '@fontsource/ibm-plex-sans/latin-600.css';
import '@fontsource/ibm-plex-mono/latin-400.css';
import './styles.css';
import { createRoot } from 'react-dom/client';
import { App } from './App';
import { store } from './store';

store.start();
createRoot(document.getElementById('root') as HTMLElement).render(<App />);
