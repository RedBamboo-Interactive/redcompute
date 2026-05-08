import { useState, useEffect, useCallback, useRef, useMemo } from "react"
import { api } from "@/api/client"
import type { JobRecord, ClaudeSessionInfo, ClaudeMessageRecord, ClaudeStreamEvent, WsEvent } from "@/api/types"
import { useWsSubscribe } from "@/contexts/ws-events"

interface SessionEventsResult {
  session: ClaudeSessionInfo | null
  events: ClaudeMessageRecord[]
  filteredEvents: ClaudeMessageRecord[]
  loading: boolean
  isLive: boolean
  isExecuteResult: boolean
  search: string
  setSearch: (s: string) => void
  typeFilter: Set<string> | null
  toggleType: (type: string) => void
  clearFilters: () => void
}

function parseStreamOutput(streamOutput: string, startedAt: string): ClaudeMessageRecord[] {
  const events: ClaudeMessageRecord[] = []
  let nextId = 1
  const baseTime = new Date(startedAt).getTime()
  const seenBlockCounts = new Map<string, number>()

  for (const line of streamOutput.split("\n")) {
    if (!line.trim()) continue
    let obj: Record<string, unknown>
    try { obj = JSON.parse(line) } catch { continue }

    if (obj.type !== "assistant") continue
    const msg = obj.message as Record<string, unknown> | undefined
    const content = msg?.content as Array<Record<string, unknown>> | undefined
    if (!content || !Array.isArray(content)) continue

    const msgId = (msg?.id as string) || ""
    const prevCount = seenBlockCounts.get(msgId) || 0
    const sessionId = (obj.session_id as string) || ""

    for (let i = prevCount; i < content.length; i++) {
      const block = content[i]
      const timestamp = new Date(baseTime + nextId).toISOString()

      if (block.type === "thinking" && block.thinking) {
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "thinking", content: block.thinking as string, timestamp })
      } else if (block.type === "text" && block.text) {
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "text", content: block.text as string, timestamp })
      } else if (block.type === "tool_use") {
        const input = block.input != null ? (typeof block.input === "string" ? block.input : JSON.stringify(block.input)) : undefined
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName: block.name as string, toolInput: input, timestamp })
      } else if (block.type === "tool_result") {
        const rc = typeof block.content === "string" ? block.content : JSON.stringify(block.content)
        events.push({ id: nextId++, sessionId, role: "user", eventType: "tool_result", content: rc, toolResult: rc, timestamp })
      }
    }
    seenBlockCounts.set(msgId, content.length)
  }

  return events
}

function fetchSession(sessionId: string) {
  return api.get<{ session: ClaudeSessionInfo; messages: ClaudeMessageRecord[] }>(
    `/claude/sessions/${sessionId}`
  )
}

function extractSessionId(job: JobRecord): string | null {
  try {
    const obj = JSON.parse(job.inputJson) as Record<string, unknown>
    if (typeof obj.sessionId === "string") return obj.sessionId
  } catch { /* ignore */ }
  return null
}

export function useSessionEvents(job: JobRecord): SessionEventsResult {
  const jobId = job.id
  const [session, setSession] = useState<ClaudeSessionInfo | null>(null)
  const [events, setEvents] = useState<ClaudeMessageRecord[]>([])
  const [loading, setLoading] = useState(true)
  const [search, setSearch] = useState("")
  const [typeFilter, setTypeFilter] = useState<Set<string> | null>(null)
  const resolvedSessionId = useRef<string | null>(null)
  const nextLocalId = useRef(100_000)
  const prevStatus = useRef<string | null>(null)
  const isExecute = useRef(false)

  const resolveAndFetch = useCallback(async () => {
    // Try direct session ID from job input first (works for all sessions, even old/dismissed)
    const directId = extractSessionId(job)
    if (directId) {
      try {
        const data = await fetchSession(directId)
        resolvedSessionId.current = directId
        setSession(data.session)
        setEvents(data.messages.toReversed())
        prevStatus.current = data.session.status
        setLoading(false)
        return
      } catch { /* session not found by direct ID, fall through */ }
    }

    // Fallback: DB lookup by jobId (handles old sessions without sessionId in inputJson)
    try {
      const data = await api.get<{ session: ClaudeSessionInfo; messages: ClaudeMessageRecord[] }>(
        `/claude/sessions/by-job/${jobId}`
      )
      resolvedSessionId.current = data.session.id
      setSession(data.session)
      setEvents(data.messages.toReversed())
      prevStatus.current = data.session.status
      setLoading(false)
      return
    } catch { /* not found or offline */ }

    // Fallback: one-shot execute job — synthesize session + events from resultJson
    if (job.resultJson) {
      try {
        const r = JSON.parse(job.resultJson) as Record<string, unknown>
        if ("text" in r || "streamOutput" in r) {
          let effort: string | undefined
          try {
            const input = JSON.parse(job.inputJson) as Record<string, unknown>
            if (typeof input.effort === "string") effort = input.effort
          } catch { /* ignore */ }

          const streamEvents = typeof r.streamOutput === "string"
            ? parseStreamOutput(r.streamOutput, job.startedAt || job.queuedAt)
            : []

          setSession({
            id: job.id,
            projectName: job.name || "Execute",
            projectPath: "",
            status: r.success ? "Stopped" : "Error",
            startedAt: job.startedAt || job.queuedAt,
            model: (r.model as string) || undefined,
            messageCount: streamEvents.length,
            costUsd: r.costUsd as number | undefined,
            inputTokens: r.inputTokens as number | undefined,
            outputTokens: r.outputTokens as number | undefined,
            effort,
          })
          setEvents(streamEvents)
          isExecute.current = true
        }
      } catch { /* malformed resultJson */ }
    }

    // Fallback: job is still running — set up streaming via WebSocket using jobId
    if (!isExecute.current && (job.status === "Running" || job.status === "Queued")) {
      resolvedSessionId.current = jobId
      let effort: string | undefined
      let inputModel: string | undefined
      try {
        const input = JSON.parse(job.inputJson) as Record<string, unknown>
        if (typeof input.effort === "string") effort = input.effort
        if (typeof input.model === "string") inputModel = input.model
      } catch { /* ignore */ }

      setSession({
        id: jobId,
        projectName: job.name || "Execute",
        projectPath: "",
        status: "Active",
        startedAt: job.startedAt || job.queuedAt,
        model: inputModel,
        messageCount: 0,
        effort,
      })
      isExecute.current = true
    }

    setLoading(false)
  }, [job, jobId])

  useEffect(() => {
    resolvedSessionId.current = null
    isExecute.current = false
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

  return { session, events, filteredEvents, loading, isLive, isExecuteResult: isExecute.current, search, setSearch, typeFilter, toggleType, clearFilters }
}
