const TOKEN_KEY = "redcompute_access_token"

export function getToken(): string | null {
  return localStorage.getItem(TOKEN_KEY)
}

export function setToken(token: string) {
  localStorage.setItem(TOKEN_KEY, token)
}

export function clearToken() {
  localStorage.removeItem(TOKEN_KEY)
}

export function isRemoteAccess(): boolean {
  const host = window.location.hostname
  return host !== "localhost" && host !== "127.0.0.1" && host !== "::1"
}
