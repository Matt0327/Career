import { StrictMode } from 'react'
import { createRoot } from 'react-dom/client'
import { App } from './App'
import { applyPrefs, loadPrefs } from './prefs'
import './styles.css'

applyPrefs(loadPrefs()) // set theme/motion before first paint to avoid a flash

createRoot(document.getElementById('root')!).render(
  <StrictMode>
    <App />
  </StrictMode>,
)
