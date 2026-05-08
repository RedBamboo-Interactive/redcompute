const TOKEN_KEY = "redcompute_access_token"

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string) {
  localStorage.setItem(TOKEN_KEY, token)
  document.cookie = `redcompute_token=${encodeURIComponent(token)}; path=/; max-age=${60 * 60 * 24 * 365}; SameSite=Strict; Secure`
}

export function clearToken() {
  localStorage.removeItem(TOKEN_KEY)
  document.cookie = "redcompute_token=; path=/; max-age=0"
}

export function isRemoteAccess(): boolean {
  const host = window.location.hostname
  return host !== "localhost" && host !== "127.0.0.1" && host !== "::1"
}

export function authUrl(path: string): string {
  const token = getToken()
  if (!token || !isRemoteAccess()) return path
  const sep = path.includes("?") ? "&" : "?"
  return `${path}${sep}token=${encodeURIComponent(token)}`
}
