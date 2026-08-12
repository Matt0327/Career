// Client-side UI preferences — presentation only (theme, motion). Persisted in localStorage and applied
// to the document root as data-attributes the stylesheet reacts to. Game state lives in the save DB;
// these ride with the WebView profile and never need to travel with a save.

export type Theme = 'system' | 'light' | 'dark'

export interface Prefs {
  theme: Theme
  reduceMotion: boolean
}

const KEY = 'callsign.prefs'
const DEFAULTS: Prefs = { theme: 'system', reduceMotion: false }

export function loadPrefs(): Prefs {
  try {
    return { ...DEFAULTS, ...(JSON.parse(localStorage.getItem(KEY) || '{}') as Partial<Prefs>) }
  } catch {
    return { ...DEFAULTS }
  }
}

export function savePrefs(p: Prefs): void {
  localStorage.setItem(KEY, JSON.stringify(p))
  applyPrefs(p)
}

/** Reflect prefs onto <html> — theme overrides the OS setting; reduced motion stills transitions. */
export function applyPrefs(p: Prefs): void {
  const el = document.documentElement
  if (p.theme === 'system') el.removeAttribute('data-theme')
  else el.setAttribute('data-theme', p.theme)
  if (p.reduceMotion) el.setAttribute('data-motion', 'reduced')
  else el.removeAttribute('data-motion')
}
