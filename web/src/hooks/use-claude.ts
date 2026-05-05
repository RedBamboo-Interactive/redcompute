import { useState, useCallback, useEffect } from "react"
import { api } from "@/api/client"
import type { ClaudeSessionInfo, ClaudeStreamEvent, ProjectInfo, WsEvent } from "@/api/types"

export interface MessageBlock {
  id: string
  role: "user" | "assistant"
  parts: MessagePart[]
  timestamp: string
}

export interface MessagePart {
  type: "text" | "thinking" | "tool_use" | "tool_result" | "error"
  content: string
  toolName?: string
  toolInput?: string
}

interface PersistedMessage {
  id: number
  sessionId: string
  role: string
  eventType: string
  content?: string
  toolName?: string
  toolInput?: string
  toolResult?: string
  messageId?: string
  timestamp: string
}

let partIdCounter = 0

function rebuildBlocks(records: PersistedMessage[]): MessageBlock[] {
  const blocks: MessageBlock[] = []
  let currentBlock: MessageBlock | null = null

  for (const rec of records) {
    if (rec.role === "user") {
      currentBlock = null
      blocks.push({
        id: `db-${rec.id}`,
        role: "user",
        parts: [{ type: "text", content: rec.content || "" }],
        timestamp: rec.timestamp,
      })
      continue
    }

    if (!currentBlock || currentBlock.role !== "assistant") {
      currentBlock = {
        id: `db-${rec.id}`,
        role: "assistant",
        parts: [],
        timestamp: rec.timestamp,
      }
      blocks.push(currentBlock)
    }

    // Skip status events in history display
    if (rec.eventType === "status") continue

    const part: MessagePart = {
      type: rec.eventType as MessagePart["type"],
      content: rec.content || rec.toolResult || "",
      toolName: rec.toolName,
      toolInput: rec.toolInput,
    }

    // Merge consecutive text parts
    if (rec.eventType === "text" && currentBlock.parts.length > 0) {
      const last = currentBlock.parts[currentBlock.parts.length - 1]
      if (last.type === "text") {
        last.content += rec.content || ""
        continue
      }
    }

    currentBlock.parts.push(part)
  }

  return blocks
}

export function useClaude() {
  const [sessions, setSessions] = useState<ClaudeSessionInfo[]>([])
  const [activeSessionId, setActiveSessionId] = useState<string | null>(null)
  const [messages, setMessages] = useState<Record<string, MessageBlock[]>>({})
  const [streaming, setStreaming] = useState<Record<string, boolean>>({})

  const refresh = useCallback(async () => {
    try {
      const data = await api.get<ClaudeSessionInfo[]>("/claude/sessions")
      setSessions(data)
      const activeStreams: Record<string, boolean> = {}
      for (const s of data) {
        if (s.status === "Active") activeStreams[s.id] = true
      }
      setStreaming(prev => ({ ...prev, ...activeStreams }))
    } catch { /* offline */ }
  }, [])

  // Load active sessions on mount
  useEffect(() => { refresh() }, [refresh])

  // Load persisted history when switching sessions (messages intentionally excluded)
  useEffect(() => {
    if (!activeSessionId) return
    if (messages[activeSessionId]?.length) return

    api.get<{ session: ClaudeSessionInfo; messages: PersistedMessage[] }>(`/claude/sessions/${activeSessionId}`)
      .then(data => {
        if (data.messages?.length) {
          setMessages(prev => ({ ...prev, [activeSessionId]: rebuildBlocks(data.messages) }))
        }
      })
      .catch(() => {})
  // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [activeSessionId])

  const loadProjects = useCallback(async () => {
    return api.get<ProjectInfo[]>("/claude/projects")
  }, [])

  const startSession = useCallback(async (projectPath: string) => {
    const session = await api.post<ClaudeSessionInfo>("/claude/sessions", { projectPath })
    setSessions(prev => [...prev, session])
    setActiveSessionId(session.id)
    setMessages(prev => ({ ...prev, [session.id]: [] }))
    return session
  }, [])

  const sendMessage = useCallback(async (sessionId: string, content: string) => {
    const userBlock: MessageBlock = {
      id: `user-${Date.now()}`,
      role: "user",
      parts: [{ type: "text", content }],
      timestamp: new Date().toISOString(),
    }
    setMessages(prev => ({
      ...prev,
      [sessionId]: [...(prev[sessionId] || []), userBlock],
    }))
    setStreaming(prev => ({ ...prev, [sessionId]: true }))
    await api.post(`/claude/sessions/${sessionId}/message`, { content })
  }, [])

  const interruptSession = useCallback(async (sessionId: string) => {
    try {
      await api.post(`/claude/sessions/${sessionId}/interrupt`)
    } catch {
      // If interrupt fails, user can still use stop
    }
  }, [])

  const stopSession = useCallback(async (sessionId: string) => {
    await api.post(`/claude/sessions/${sessionId}/stop`)
  }, [])

  const dismissSession = useCallback(async (sessionId: string) => {
    setSessions(prev => prev.filter(s => s.id !== sessionId))
    setActiveSessionId(prev => prev === sessionId ? null : prev)
    try { await api.post(`/claude/sessions/${sessionId}/dismiss`) } catch {}
  }, [])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "claude.session.created") {
      const session = event.data as ClaudeSessionInfo
      setSessions(prev => {
        if (prev.some(s => s.id === session.id)) return prev
        return [...prev, session]
      })
      setActiveSessionId(session.id)
    } else if (event.type === "claude.session.updated") {
      const session = event.data as ClaudeSessionInfo
      setSessions(prev => prev.map(s => s.id === session.id ? session : s))
    } else if (event.type === "claude.session.ended") {
      const { id } = event.data as { id: string }
      setSessions(prev => prev.map(s => s.id === id ? { ...s, status: "Stopped" as const } : s))
      setStreaming(prev => ({ ...prev, [id]: false }))
    } else if (event.type === "claude.stream") {
      const { sessionId, event: evt } = event.data as { sessionId: string; event: ClaudeStreamEvent }

      if (evt.type === "status" && (evt.content === "idle" || evt.content === "interrupted")) {
        setStreaming(prev => ({ ...prev, [sessionId]: false }))
        return
      }

      setMessages(prev => {
        const sessionMsgs = [...(prev[sessionId] || [])]

        let lastBlock = sessionMsgs[sessionMsgs.length - 1]
        if (!lastBlock || lastBlock.role !== "assistant") {
          lastBlock = {
            id: `assistant-${Date.now()}-${partIdCounter++}`,
            role: "assistant",
            parts: [],
            timestamp: new Date().toISOString(),
          }
          sessionMsgs.push(lastBlock)
        } else {
          lastBlock = { ...lastBlock, parts: [...lastBlock.parts] }
          sessionMsgs[sessionMsgs.length - 1] = lastBlock
        }

        const part: MessagePart = {
          type: evt.type as MessagePart["type"],
          content: evt.content || evt.toolResult || "",
          toolName: evt.toolName,
          toolInput: typeof evt.toolInput === "string" ? evt.toolInput : evt.toolInput ? JSON.stringify(evt.toolInput) : undefined,
        }

        if (evt.type === "text" && lastBlock.parts.length > 0) {
          const lastPart = lastBlock.parts[lastBlock.parts.length - 1]
          if (lastPart.type === "text") {
            lastBlock.parts[lastBlock.parts.length - 1] = {
              ...lastPart,
              content: lastPart.content + (evt.content || ""),
            }
            return { ...prev, [sessionId]: sessionMsgs }
          }
        }

        lastBlock.parts.push(part)
        return { ...prev, [sessionId]: sessionMsgs }
      })
    }
  }, [])

  const activeSession = sessions.find(s => s.id === activeSessionId) || null
  const activeMessages = activeSessionId ? (messages[activeSessionId] || []) : []
  const isStreaming = activeSessionId ? (streaming[activeSessionId] || false) : false

  return {
    sessions,
    activeSession,
    activeSessionId,
    setActiveSessionId,
    activeMessages,
    isStreaming,
    refresh,
    loadProjects,
    startSession,
    sendMessage,
    interruptSession,
    stopSession,
    dismissSession,
    handleWsEvent,
  }
}
