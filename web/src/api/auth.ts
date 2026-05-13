import { createRemoteConnection } from "@redbamboo/utility"

export const connectionStore = createRemoteConnection({
  storageKey: "redcompute_access_token",
  cookieName: "redcompute_token",
})

export function getToken(): string | null {
  return connectionStore.get()?.token ?? null
}

export function setToken(token: string) {
  connectionStore.set({ token })
}

export function clearToken() {
  connectionStore.clear()
}

export function isRemoteAccess(): boolean {
  return connectionStore.isRemoteAccess()
}

export function authUrl(path: string): string {
  return connectionStore.authUrl(path)
}
