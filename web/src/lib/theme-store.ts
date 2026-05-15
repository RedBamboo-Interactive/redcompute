import { createLocalStore } from "@redbamboo/utility"

export type Theme = "dark" | "light"
export type Contrast = "low" | "high"

const store = createLocalStore<{ theme: Theme; contrast: Contrast }>("redcompute_theme_v3", {
  theme: "dark",
  contrast: "low",
})

export function getTheme(): Theme {
  return store.get().theme
}

export function setTheme(theme: Theme) {
  store.set({ theme })
}

export function subscribeTheme(callback: () => void): () => void {
  return store.subscribe(callback)
}

export function getContrast(): Contrast {
  return store.get().contrast
}

export function setContrast(contrast: Contrast) {
  store.set({ contrast })
}

export function subscribeContrast(callback: () => void): () => void {
  return store.subscribe(callback)
}
