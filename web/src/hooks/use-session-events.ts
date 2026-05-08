import { useState, useEffect, useCallback, useRef, useMemo } from "react"
import { api } from "@/api/client"
import type { ClaudeSessionInfo, ClaudeMessageRecord, ClaudeStreamEvent, WsEvent } from "@/api/types"
import { useWsSubscribe } from "@/contexts/ws-events"

interface SessionEventsResult {
  session: ClaudeSessionInfo | null
  events: ClaudeMessageRecord[]
  filteredEvents: ClaudeMessageRecord[]
  loading: boolean
  isLive: boolean
  search: string
  setSearch: (s: string) => void
  typeFilter: Set<string> | null
  toggleType: (type: string) => void
  clearFilters: () => void
}

function fetchSession(sessionId: string) {
  return api.get<{ session: ClaudeSessionInfo; messages: ClaudeMessageRecord[] }>(
    `/claude/sessions/${sessionId}`
  )
}

export function useSessionEvents(jobId: string): SessionEventsResult {
  const [session, setSession] = useState<ClaudeSessionInfo | null>(null)
  const [events, setEvents] = useState<ClaudeMessageRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState("")
  const [typeFilter, setTypeFilter] = useState<Set<string> | null>(null)
  const resolvedSessionId = useRef<string | null>(null)
  const nextLocalId = useRef(100_000)
  const prevStatus = useRef<string | null>(null)

  const resolveAndFetch = useCallback(async () => {
    try {
      const sessions = await api.get<ClaudeSessionInfo[]>("/claude/sessions")
      const match = sessions.find(s => s.jobId === jobId)
      if (!match) {
        setLoading(false)
        return
      }
      resolvedSessionId.current = match.id

      const data = await fetchSession(match.id)
      setSession(data.session)
      setEvents(data.messages.toReversed())
      prevStatus.current = data.session.status
    } catch { /* offline */ }
    setLoading(false)
  }, [jobId])

  useEffect(() => {
    resolvedSessionId.current = null
    setSession(null)
    setEvents([])
    setLoading(true)
    resolveAndFetch()
  }, [resolveAndFetch])

  useWsSubscribe(useCallback((event: WsEvent) => {
    if (event.type === "claude.session.created" || event.type === "claude.session.updated") {
      const s = event.data as ClaudeSessionInfo
      if (s.jobId === jobId) {
        if (!resolvedSessionId.current) {
          resolvedSessionId.current = s.id
          fetchSession(s.id).then(data => {
            setSession(data.session)
            setEvents(data.messages.toReversed())
            prevStatus.current = data.session.status
            setLoading(false)
          }).catch(() => {})
        } else {
          const wasActive = prevStatus.current === "Active" || prevStatus.current === "Starting"
          const nowIdle = s.status === "Idle" || s.status === "Stopped" || s.status === "Error"
          prevStatus.current = s.status
          setSession(s)

          // Re-fetch clean persisted messages when assistant turn completes
          if (wasActive && nowIdle) {
            fetchSession(s.id).then(data => {
              setEvents(data.messages.toReversed())
            }).catch(() => {})
          }
        }
      }
    }

    if (event.type === "claude.stream") {
      const { sessionId, event: evt } = event.data as { sessionId: string; event: ClaudeStreamEvent }
      if (sessionId !== resolvedSessionId.current) return
      if (evt.type === "status") return

      // Skip partial text/thinking deltas — they'd create hundreds of tiny rows.
      // We accumulate into the last event of the same type instead.
      if (evt.isPartial && (evt.type === "text" || evt.type === "thinking")) {
        setEvents(prev => {
          const last = prev[prev.length - 1]
          if (last && last.eventType === evt.type && last.id >= 100_000) {
            const updated = { ...last, content: (last.content || "") + (evt.content || "") }
            return [...prev.slice(0, -1), updated]
          }
          return [...prev, {
            id: nextLocalId.current++,
            sessionId,
            role: "assistant",
            eventType: evt.type,
            content: evt.content,
            timestamp: new Date().toISOString(),
          }]
        })
        return
      }

      const record: ClaudeMessageRecord = {
        id: nextLocalId.current++,
        sessionId,
        role: evt.type === "tool_result" ? "user" : "assistant",
        eventType: evt.type,
        content: evt.content,
        toolName: evt.toolName,
        toolInput: evt.toolInput != null ? (typeof evt.toolInput === "string" ? evt.toolInput : JSON.stringify(evt.toolInput)) : undefined,
        toolResult: evt.toolResult,
        messageId: evt.messageId,
        timestamp: new Date().toISOString(),
      }
      setEvents(prev => [...prev, record])
    }
  }, [jobId]))

  const isLive = session?.status === "Active" || session?.status === "Starting"

  const filteredEvents = useMemo(() => {
    let result = events
    if (typeFilter && typeFilter.size > 0) {
      result = result.filter(e => typeFilter.has(e.eventType))
    }
    if (search) {
      const q = search.toLowerCase()
      result = result.filter(e =>
        (e.content || "").toLowerCase().includes(q) ||
        (e.toolName || "").toLowerCase().includes(q) ||
        (e.toolInput || "").toLowerCase().includes(q) ||
        (e.toolResult || "").toLowerCase().includes(q)
      )
    }
    return result
  }, [events, typeFilter, search])

  const toggleType = useCallback((type: string) => {
    setTypeFilter(prev => {
      if (!prev) {
        const s = new Set<string>([type])
        return s
      }
      const next = new Set(prev)
      if (next.has(type)) next.delete(type)
      else next.add(type)
      return next.size === 0 ? null : next
    })
  }, [])

  const clearFilters = useCallback(() => {
    setSearch("")
    setTypeFilter(null)
  }, [])

  return { session, events, filteredEvents, loading, isLive, search, setSearch, typeFilter, toggleType, clearFilters }
}
