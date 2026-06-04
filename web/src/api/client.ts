const BASE = ""

async function request<T>(path: string, options?: RequestInit): Promise<T> {
  const headers: Record<string, string> = {
    ...(options?.headers as Record<string, string>),
  }

  const res = await fetch(`${BASE}${path}`, { ...options, headers, credentials: "include" })
  if (res.status === 401) {
    window.dispatchEvent(new CustomEvent("redcompute:auth-required"))
    throw new Error("Authentication required")
  }
  if (!res.ok) {
    const body = await res.json().catch(() => ({ error: "unknown", message: res.statusText }))
    throw new Error(body.message || res.statusText)
  }
  const ct = res.headers.get("content-type") || ""
  if (ct.includes("application/json")) {
    return res.json()
  }
  return null as T
}

export const api = {
  get: <T>(path: string) => request<T>(path),
  post: <T>(path: string, body?: unknown, extraHeaders?: Record<string, string>) =>
    request<T>(path, {
      method: "POST",
      headers: { ...(body ? { "Content-Type": "application/json" } : {}), ...extraHeaders },
      body: body ? JSON.stringify(body) : undefined,
    }),
  put: <T>(path: string, body?: unknown) =>
    request<T>(path, {
      method: "PUT",
      headers: body ? { "Content-Type": "application/json" } : {},
      body: body ? JSON.stringify(body) : undefined,
    }),
  delete: <T>(path: string) => request<T>(path, { method: "DELETE" }),
}
