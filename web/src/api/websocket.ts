import { getToken } from "./auth"
import type { WsEvent } from "./types"

type EventHandler = (event: WsEvent) => void

export function createWebSocket(onEvent: EventHandler, onReconnect?: () => void) {
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:"
  const token = getToken()
  const tokenParam = token ? `?token=${encodeURIComponent(token)}` : ""
  const url = `${protocol}//${window.location.host}/ws${tokenParam}`
  let ws: WebSocket | null = null
  let reconnectTimeout: ReturnType<typeof setTimeout> | null = null
  let closed = false
  let hasConnected = false

  function connect() {
    if (closed) return
    ws = new WebSocket(url)

    ws.onopen = () => {
      if (hasConnected && onReconnect) onReconnect()
      hasConnected = true
    }

    ws.onmessage = (e) => {
      try {
        const event = JSON.parse(e.data) as WsEvent
        onEvent(event)
      } catch { /* ignore malformed messages */ }
    }

    ws.onclose = () => {
      if (!closed) {
        reconnectTimeout = setTimeout(connect, 3000)
      }
    }

    ws.onerror = () => {
      ws?.close()
    }
  }

  connect()

  return {
    close() {
      closed = true
      if (reconnectTimeout) clearTimeout(reconnectTimeout)
      ws?.close()
    },
  }
}
