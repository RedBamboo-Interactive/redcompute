import { createLocalStore } from "@redbamboo/utility"

export type Theme = "dark" | "light"

const store = createLocalStore<{ theme: Theme }>("redcompute_theme_v2", {
  theme: "dark",
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
