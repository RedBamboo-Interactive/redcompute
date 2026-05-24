import { useState, useEffect, useCallback, useRef, useMemo } from "react"
import { api } from "@/api/client"
import type { JobRecord, ClaudeSessionInfo, ClaudeMessageRecord, ClaudeStreamEvent } from "@/api/types"
import { useWsSubscribe } from "@redbamboo/utility"

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

function aggregateDeltas(records: ClaudeMessageRecord[]): ClaudeMessageRecord[] {
  const result: ClaudeMessageRecord[] = []
  for (const rec of records) {
    if (rec.eventType === "status") continue
    // Remap user text to "prompt" so it gets its own badge and filter
    if (rec.role === "user" && rec.eventType === "text") {
      result.push({ ...rec, eventType: "prompt" })
      continue
    }
    const last = result[result.length - 1]
    if (last && last.eventType === rec.eventType && last.role === rec.role
        && (rec.eventType === "text" || rec.eventType === "thinking")) {
      result[result.length - 1] = { ...last, content: (last.content || "") + (rec.content || "") }
    } else if (last && last.eventType === "tool_use" && rec.eventType === "tool_result") {
      result[result.length - 1] = { ...last, toolResult: rec.toolResult || rec.content }
    } else {
      result.push(rec)
    }
  }
  return result
}

function extractCodexText(item: Record<string, unknown>): string | null {
  const content = item.content
  if (typeof content === "string") return content
  if (Array.isArray(content)) {
    const parts = (content as Array<Record<string, unknown>>)
      .map(b => (b.text as string) || "")
      .filter(Boolean)
    return parts.length > 0 ? parts.join("") : null
  }
  if (typeof item.text === "string") return item.text as string
  return null
}

function parseStreamOutput(streamOutput: string, startedAt: string): ClaudeMessageRecord[] {
  const events: ClaudeMessageRecord[] = []
  let nextId = 1
  const baseTime = new Date(startedAt).getTime()

  for (const line of streamOutput.split("\n")) {
    if (!line.trim()) continue
    let obj: Record<string, unknown>
    try { obj = JSON.parse(line) } catch { continue }

    const sessionId = (obj.session_id as string) || ""

    // Claude format
    if (obj.type === "assistant") {
      const msg = obj.message as Record<string, unknown> | undefined
      const content = msg?.content as Array<Record<string, unknown>> | undefined
      if (!content || !Array.isArray(content)) continue

      for (const block of content) {
        const timestamp = new Date(baseTime + nextId).toISOString()
        if (block.type === "thinking" && block.thinking) {
          events.push({ id: nextId++, sessionId, role: "assistant", eventType: "thinking", content: block.thinking as string, timestamp })
        } else if (block.type === "text" && block.text) {
          events.push({ id: nextId++, sessionId, role: "assistant", eventType: "text", content: block.text as string, timestamp })
        } else if (block.type === "tool_use") {
          const input = block.input != null ? (typeof block.input === "string" ? block.input : JSON.stringify(block.input)) : undefined
          events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName: block.name as string, toolInput: input, timestamp })
        }
      }
    } else if (obj.type === "user") {
      const msg = obj.message as Record<string, unknown> | undefined
      const content = msg?.content as Array<Record<string, unknown>> | undefined
      if (!content || !Array.isArray(content)) continue

      for (const block of content) {
        if (block.type !== "tool_result") continue
        const timestamp = new Date(baseTime + nextId).toISOString()
        const rc = typeof block.content === "string" ? block.content : JSON.stringify(block.content)
        events.push({ id: nextId++, sessionId, role: "user", eventType: "tool_result", content: rc, toolResult: rc, timestamp })
      }
    }

    // OpenCode format (simple type-based events)
    else if (obj.type === "text" || obj.type === "content") {
      const content = (obj.content as string) || (obj.text as string)
      if (content) {
        const timestamp = new Date(baseTime + nextId).toISOString()
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "text", content, timestamp })
      }
    } else if (obj.type === "thinking" || obj.type === "reasoning") {
      const content = (obj.content as string) || (obj.thinking as string)
      if (content) {
        const timestamp = new Date(baseTime + nextId).toISOString()
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "thinking", content, timestamp })
      }
    } else if (obj.type === "tool_use" || obj.type === "tool_call") {
      const timestamp = new Date(baseTime + nextId).toISOString()
      const toolName = (obj.name as string) || (obj.tool as string)
      const input = obj.input != null ? (typeof obj.input === "string" ? obj.input : JSON.stringify(obj.input))
        : obj.arguments != null ? JSON.stringify(obj.arguments) : undefined
      events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName, toolInput: input, timestamp })
    } else if (obj.type === "tool_result") {
      const timestamp = new Date(baseTime + nextId).toISOString()
      const content = (obj.content as string) || (obj.output as string)
      events.push({ id: nextId++, sessionId, role: "user", eventType: "tool_result", content, toolResult: content, timestamp })
    }

    // Codex format
    else if (obj.type === "item.completed") {
      const item = (obj.item as Record<string, unknown>) || obj
      const itemType = item.type as string
      const timestamp = new Date(baseTime + nextId).toISOString()

      if (itemType === "agentMessage") {
        const text = extractCodexText(item)
        if (text) events.push({ id: nextId++, sessionId, role: "assistant", eventType: "text", content: text, timestamp })
      } else if (itemType === "reasoning") {
        const summary = item.summary as Array<Record<string, unknown>> | undefined
        const text = summary?.map(s => (s.text as string) || "").filter(Boolean).join("\n")
        if (text) events.push({ id: nextId++, sessionId, role: "assistant", eventType: "thinking", content: text, timestamp })
      } else if (itemType === "commandExecution") {
        const cmd = item.command as string | undefined
        const output = item.output as string | undefined
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName: "Command", toolInput: cmd, timestamp })
        if (output) events.push({ id: nextId++, sessionId, role: "user", eventType: "tool_result", content: output, toolResult: output, timestamp: new Date(baseTime + nextId).toISOString() })
      } else if (itemType === "fileChange") {
        const filename = item.filename as string | undefined
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName: "FileEdit", toolInput: filename, timestamp })
      } else if (itemType === "mcpToolCall") {
        const toolName = item.name as string | undefined
        const args = item.arguments != null ? JSON.stringify(item.arguments) : undefined
        const output = item.output as string | undefined
        events.push({ id: nextId++, sessionId, role: "assistant", eventType: "tool_use", toolName: toolName, toolInput: args, timestamp })
        if (output) events.push({ id: nextId++, sessionId, role: "user", eventType: "tool_result", content: output, toolResult: output, timestamp: new Date(baseTime + nextId).toISOString() })
      }
    }
  }

  return events
}

function providerPrefix(_job: JobRecord): string {
  return "/ai-session"
}

function fetchSession(sessionId: string, prefix: string) {
  return api.get<{ session: ClaudeSessionInfo; messages: ClaudeMessageRecord[] }>(
    `${prefix}/sessions/${sessionId}`
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
  const jobRef = useRef(job)
  jobRef.current = job

  const prefix = providerPrefix(job)

  const resolveAndFetch = useCallback(async () => {
    const job = jobRef.current
    // Try direct session ID from job input first (works for all sessions, even old/dismissed)
    const directId = extractSessionId(job)
    if (directId) {
      try {
        const data = await fetchSession(directId, prefix)
        resolvedSessionId.current = directId
        setSession(data.session)
        setEvents(aggregateDeltas(data.messages))
        prevStatus.current = data.session.status
        setLoading(false)
        return
      } catch { /* session not found by direct ID, fall through */ }
    }

    // Fallback: DB lookup by jobId (handles old sessions without sessionId in inputJson)
    try {
      const data = await api.get<{ session: ClaudeSessionInfo; messages: ClaudeMessageRecord[] }>(
        `${prefix}/sessions/by-job/${jobId}`
      )
      resolvedSessionId.current = data.session.id
      setSession(data.session)
      setEvents(aggregateDeltas(data.messages))
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
          const hasStreamContent = streamEvents.length > 0

          // Inject system prompt + user prompt before stream events
          try {
            const input = JSON.parse(job.inputJson) as Record<string, unknown>
            const prefix: typeof streamEvents = []
            if (typeof input.system === "string" && input.system) {
              prefix.push({
                id: -1,
                sessionId: "",
                role: "assistant",
                eventType: "system",
                content: input.system,
                timestamp: job.startedAt || job.queuedAt,
              })
            }
            if (typeof input.prompt === "string" && input.prompt) {
              prefix.push({
                id: 0,
                sessionId: "",
                role: "user",
                eventType: "text",
                content: input.prompt,
                timestamp: job.startedAt || job.queuedAt,
              })
            }
            streamEvents.unshift(...prefix)
          } catch { /* ignore */ }

          // Synthesize assistant text event when no stream output (e.g. oneshot results)
          if (!hasStreamContent && typeof r.text === "string" && r.text) {
            streamEvents.push({
              id: 1,
              sessionId: "",
              role: "assistant",
              eventType: "text",
              content: r.text,
              timestamp: job.completedAt || job.startedAt || job.queuedAt,
            })
          }

          let execTitle = job.name
          if (!execTitle) {
            try {
              const inp = JSON.parse(job.inputJson) as Record<string, unknown>
              if (typeof inp.prompt === "string" && inp.prompt)
                execTitle = inp.prompt.length > 60 ? inp.prompt.slice(0, 57) + "..." : inp.prompt
            } catch { /* ignore */ }
          }

          setSession({
            id: job.id,
            projectName: execTitle || "Execute",
            projectPath: "",
            status: job.status === "Failed" ? "Error" : "Stopped",
            startedAt: job.startedAt || job.queuedAt,
            model: (r.model as string) || undefined,
            messageCount: streamEvents.length,
            costUsd: r.costUsd as number | undefined,
            inputTokens: r.inputTokens as number | undefined,
            outputTokens: r.outputTokens as number | undefined,
            effort,
          })
          setEvents(aggregateDeltas(streamEvents))
          isExecute.current = true
        }
      } catch { /* malformed resultJson */ }
    }

    // Fallback: job is still running — set up streaming via WebSocket using jobId
    if (!isExecute.current && (job.status === "Running" || job.status === "Queued")) {
      resolvedSessionId.current = jobId
      let effort: string | undefined
      let inputModel: string | undefined
      let runTitle = job.name
      try {
        const input = JSON.parse(job.inputJson) as Record<string, unknown>
        if (typeof input.effort === "string") effort = input.effort
        if (typeof input.model === "string") inputModel = input.model
        if (!runTitle && typeof input.prompt === "string" && input.prompt)
          runTitle = input.prompt.length > 60 ? input.prompt.slice(0, 57) + "..." : input.prompt
      } catch { /* ignore */ }

      setSession({
        id: jobId,
        projectName: runTitle || "Execute",
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
  }, [jobId, prefix])

  useEffect(() => {
    resolvedSessionId.current = null
    isExecute.current = false
    setSession(null)
    setEvents([])
    setLoading(true)
    resolveAndFetch()
  }, [resolveAndFetch])

  // When an execute job finishes, rebuild events from the complete stored output
  useEffect(() => {
    if (!isExecute.current) return
    if (job.status !== "Completed" && job.status !== "Failed") return
    if (!job.resultJson) return
    resolveAndFetch()
  }, [job.status, job.resultJson, resolveAndFetch])

  useWsSubscribe(useCallback((event) => {
    const isSessionEvent = event.type === "session.created" || event.type === "session.updated"
    if (isSessionEvent) {
      const s = event.data as ClaudeSessionInfo
      if (s.jobId === jobId) {
        if (!resolvedSessionId.current) {
          resolvedSessionId.current = s.id
          fetchSession(s.id, prefix).then(data => {
            setSession(data.session)
            setEvents(aggregateDeltas(data.messages))
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
            fetchSession(s.id, prefix).then(data => {
              setEvents(aggregateDeltas(data.messages))
            }).catch(() => {})
          }
        }
      }
    }

    if (event.type === "session.stream") {
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

      if (evt.type === "tool_result") {
        setEvents(prev => {
          const last = prev[prev.length - 1]
          if (last && last.eventType === "tool_use") {
            return [...prev.slice(0, -1), { ...last, toolResult: evt.toolResult || evt.content }]
          }
          return [...prev, {
            id: nextLocalId.current++,
            sessionId,
            role: "user" as const,
            eventType: "tool_result",
            content: evt.content,
            toolResult: evt.toolResult,
            messageId: evt.messageId,
            timestamp: new Date().toISOString(),
          }]
        })
        return
      }

      const record: ClaudeMessageRecord = {
        id: nextLocalId.current++,
        sessionId,
        role: "assistant",
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
  }, [jobId, prefix]))

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
