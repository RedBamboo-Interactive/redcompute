export type Theme = "dark" | "light"

const STORAGE_KEY = "redcompute_theme"
const listeners = new Set<() => void>()
let cached: Theme | null = null

function notify() {
  for (const fn of listeners) fn()
}

export function getTheme(): Theme {
  if (cached) return cached
  const raw = localStorage.getItem(STORAGE_KEY)
  cached = raw === "light" ? "light" : "dark"
  return cached
}

export function setTheme(theme: Theme) {
  localStorage.setItem(STORAGE_KEY, theme)
  cached = theme
  notify()
}

export function subscribeTheme(callback: () => void): () => void {
  listeners.add(callback)

  const onStorage = (e: StorageEvent) => {
    if (e.key === STORAGE_KEY) {
      cached = null
      notify()
    }
  }
  window.addEventListener("storage", onStorage)

  return () => {
    listeners.delete(callback)
    window.removeEventListener("storage", onStorage)
  }
}
