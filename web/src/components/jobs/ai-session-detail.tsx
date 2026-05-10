import { useState, useRef, useEffect, useCallback } from "react"
import { Badge, Separator, JsonHighlight } from "@redbamboo/ui"
import { api } from "@/api/client"
import { useSessionEvents } from "@/hooks/use-session-events"
import type { JobRecord, ClaudeMessageRecord } from "@/api/types"
import Markdown from "react-markdown"
import remarkGfm from "remark-gfm"
import rehypeHighlight from "rehype-highlight"

const readOnlyTools = new Set([
  "Read", "Glob", "Grep", "Agent", "WebSearch", "WebFetch", "Explore",
  "ToolSearch", "AskUserQuestion", "Monitor", "CronList",
])

const eventTypeConfig: Record<string, { label: string; color: string; bg: string }> = {
  text: { label: "text", color: "text-accent-teal", bg: "bg-accent-teal/20" },
  thinking: { label: "thinking", color: "text-accent-purple", bg: "bg-accent-purple/20" },
  tool_use: { label: "tool", color: "text-accent-gold", bg: "bg-accent-gold/20" },
  tool_result: { label: "result", color: "text-accent-teal", bg: "bg-accent-teal/20" },
  error: { label: "error", color: "text-accent-red", bg: "bg-accent-red/20" },
  prompt: { label: "prompt", color: "text-blue-400", bg: "bg-blue-400/20" },
}

const statusBadgeColor: Record<string, string> = {
  Starting: "bg-accent-gold/20 text-accent-gold border-accent-gold/30",
  Active: "bg-accent-teal/20 text-accent-teal border-accent-teal/30",
  Idle: "bg-accent-teal/20 text-accent-teal border-accent-teal/30",
  Stopped: "bg-text-disabled/20 text-text-disabled border-text-disabled/30",
  Error: "bg-accent-red/20 text-accent-red border-accent-red/30",
}

function toolColor(toolName?: string): string {
  if (!toolName) return "text-accent-teal"
  return readOnlyTools.has(toolName) ? "text-accent-teal" : "text-accent-gold"
}

function toolBg(toolName?: string): string {
  if (!toolName) return "bg-accent-teal/20"
  return readOnlyTools.has(toolName) ? "bg-accent-teal/20" : "bg-accent-gold/20"
}

function formatTokens(n?: number): string {
  if (n == null) return "-"
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`
  return String(n)
}

function shortModel(model?: string): string {
  if (!model) return "?"
  if (model.includes("opus")) return "Opus"
  if (model.includes("sonnet")) return "Sonnet"
  if (model.includes("haiku")) return "Haiku"
  return model.split("-").slice(0, 2).join("-")
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s
  return s.slice(0, max) + "..."
}

export function AiSessionDetail({ job }: { job: JobRecord }) {
  const { session, filteredEvents, loading, isLive, isExecuteResult, search, setSearch, typeFilter, toggleType, clearFilters, events } =
    useSessionEvents(job)
  const [expandedIds, setExpandedIds] = useState<Set<number>>(new Set())
  const scrollRef = useRef<HTMLDivElement>(null)
  const shouldAutoScroll = useRef(true)
  const [showScrollBtn, setShowScrollBtn] = useState(false)
  const [codeRedStatus, setCodeRedStatus] = useState<"idle" | "sent" | "error">("idle")

  const toggleExpand = useCallback((id: number) => {
    setExpandedIds(prev => {
      const next = new Set(prev)
      if (next.has(id)) next.delete(id)
      else next.add(id)
      return next
    })
  }, [])

  const handleScroll = useCallback(() => {
    const el = scrollRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60
    shouldAutoScroll.current = atBottom
    setShowScrollBtn(!atBottom)
  }, [])

  useEffect(() => {
    if (shouldAutoScroll.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [filteredEvents.length])

  const scrollToBottom = useCallback(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
      shouldAutoScroll.current = true
      setShowScrollBtn(false)
    }
  }, [])

  if (loading) {
    return (
      <div className="flex items-center justify-center h-40 text-text-muted text-sm">
        <i className="fa-solid fa-spinner-third fa-spin mr-2" />Loading session events...
      </div>
    )
  }

  if (!session) {
    return (
      <div className="flex items-center justify-center h-40 text-text-muted text-sm">
        {job.status === "Running" || job.status === "Queued" ? (
          <><i className="fa-solid fa-spinner-third fa-spin mr-2" />Executing agent&hellip;</>
        ) : (
          "Session not found for this job."
        )}
      </div>
    )
  }

  const hasFilters = search || typeFilter

  return (
    <div className="flex flex-col h-full gap-3">
      {/* Metadata header */}
      <div className="flex items-center gap-2 flex-wrap">
        <h2 className="text-lg font-medium">{session.title || session.projectName}</h2>
        <Badge variant="outline" className={statusBadgeColor[session.status] || ""}>
          {session.status}
        </Badge>
        {isLive && (
          <span className="flex items-center gap-1.5 text-xs text-accent-teal">
            <span className="w-1.5 h-1.5 rounded-full bg-accent-teal animate-pulse" />
            Live
          </span>
        )}
        {!isExecuteResult && (
          <button
            onClick={async () => {
              try {
                await api.post(`/claude/sessions/${session.id}/open-in-codered`)
                setCodeRedStatus("sent")
                setTimeout(() => setCodeRedStatus("idle"), 2000)
              } catch {
                setCodeRedStatus("error")
                setTimeout(() => setCodeRedStatus("idle"), 3000)
              }
            }}
            className={`ml-auto inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded-md transition-colors ${
              codeRedStatus === "sent"
                ? "bg-contrast/[0.08] text-accent-teal"
                : codeRedStatus === "error"
                ? "bg-contrast/[0.08] text-accent-red"
                : "bg-contrast/[0.06] text-text-muted hover:bg-contrast/[0.10] hover:text-text-primary"
            }`}
          >
            <i className={`${
              codeRedStatus === "sent" ? "fa-solid fa-check text-accent-teal" : codeRedStatus === "error" ? "fa-solid fa-xmark text-accent-red" : "fa-regular fa-square-terminal text-accent-red"
            } text-[11px]`} />
            {codeRedStatus === "sent" ? "Sent to CodeRed" : codeRedStatus === "error" ? "CodeRed unreachable" : isLive ? "Open in CodeRed" : "Resume in CodeRed"}
          </button>
        )}
      </div>

      <div className="flex items-center gap-3 flex-wrap text-xs text-text-muted">
        <span className="inline-flex items-center gap-1">
          <i className="fa-solid fa-microchip" />
          {shortModel(session.model)}
        </span>
        {session.costUsd != null && (
          <span className="inline-flex items-center gap-1">
            <i className="fa-solid fa-dollar-sign" />
            {session.costUsd.toFixed(4)}
          </span>
        )}
        <span className="inline-flex items-center gap-1" title="Input / Output tokens">
          <i className="fa-solid fa-arrow-down" />
          {formatTokens(session.inputTokens)}
          <span className="text-text-disabled">/</span>
          <i className="fa-solid fa-arrow-up" />
          {formatTokens(session.outputTokens)}
        </span>
        {session.cacheReadInputTokens != null && session.cacheReadInputTokens > 0 && (
          <span className="inline-flex items-center gap-1" title="Cache read tokens">
            <i className="fa-solid fa-bolt" />
            {formatTokens(session.cacheReadInputTokens)}
          </span>
        )}
        {session.effort && (
          <span className="inline-flex items-center gap-1">
            <i className="fa-solid fa-gauge" />
            {session.effort}
          </span>
        )}
        <span className="inline-flex items-center gap-1">
          <i className="fa-solid fa-messages" />
          {session.messageCount}
        </span>
      </div>

      <Separator />

      {/* Toolbar */}
      <div className="flex items-center gap-2 flex-wrap">
        <div className="relative flex-1 min-w-[200px]">
          <i className="fa-solid fa-search absolute left-2.5 top-1/2 -translate-y-1/2 text-text-disabled text-xs" />
          <input
            type="text"
            placeholder="Search events..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="w-full bg-contrast/[0.08] rounded-lg pl-8 pr-3 py-1.5 text-sm text-text-primary placeholder-text-disabled outline-none focus:ring-1 focus:ring-accent-teal/50"
          />
        </div>
        {Object.entries(eventTypeConfig).map(([type, cfg]) => {
          const active = typeFilter?.has(type)
          return (
            <button
              key={type}
              onClick={() => toggleType(type)}
              className={`px-2 py-0.5 rounded text-[11px] font-medium transition-colors border ${
                active
                  ? `${cfg.bg} ${cfg.color} border-current`
                  : "bg-contrast/[0.04] text-text-disabled border-transparent hover:bg-contrast/[0.08]"
              }`}
            >
              {cfg.label}
            </button>
          )
        })}
        {hasFilters && (
          <button onClick={clearFilters} className="text-[11px] text-text-disabled hover:text-text-muted">
            clear
          </button>
        )}
        <span className="text-[11px] text-text-disabled ml-auto">
          {filteredEvents.length === events.length
            ? `${events.length} events`
            : `${filteredEvents.length} / ${events.length}`}
        </span>
      </div>

      {/* Event log */}
      <div className="flex-1 min-h-0 relative">
        <div
          ref={scrollRef}
          onScroll={handleScroll}
          className="absolute inset-0 overflow-auto bg-surface-base rounded-lg"
        >
          {filteredEvents.length === 0 ? (
            <div className="flex items-center justify-center h-32 text-text-disabled text-sm">
              {events.length === 0 ? "No events yet" : "No matching events"}
            </div>
          ) : (
            <div className="divide-y divide-contrast/[0.04]">
              {filteredEvents.map((event, idx) => (
                <EventLogRow
                  key={event.id}
                  event={event}
                  index={idx + 1}
                  expanded={expandedIds.has(event.id)}
                  onToggle={() => toggleExpand(event.id)}
                />
              ))}
            </div>
          )}
        </div>

        {showScrollBtn && (
          <button
            onClick={scrollToBottom}
            className="absolute bottom-3 right-3 w-8 h-8 rounded-full bg-surface-elevated border border-contrast/10 flex items-center justify-center text-text-muted hover:text-text-primary transition-colors shadow-lg"
          >
            <i className="fa-solid fa-arrow-down text-xs" />
          </button>
        )}
      </div>
    </div>
  )
}

function EventLogRow({ event, index, expanded, onToggle }: { event: ClaudeMessageRecord; index: number; expanded: boolean; onToggle: () => void }) {
  const cfg = eventTypeConfig[event.eventType] || eventTypeConfig.text
  const isToolUse = event.eventType === "tool_use"
  const isToolResult = event.eventType === "tool_result"
  const isError = event.eventType === "error"
  const isThinking = event.eventType === "thinking"

  const tColor = isToolUse ? toolColor(event.toolName) : cfg.color
  const tBg = isToolUse ? toolBg(event.toolName) : cfg.bg

  const summary = getSummary(event)

  return (
    <div className="group">
      <button
        onClick={onToggle}
        className={`flex items-start gap-2 w-full text-left px-2.5 py-1.5 transition-colors ${
          expanded ? "bg-contrast/[0.06]" : "hover:bg-contrast/[0.03]"
        }`}
      >
        <span className="font-mono text-[11px] text-contrast/40 w-[28px] shrink-0 pt-px text-right">
          {index}
        </span>

        <span className={`inline-flex items-center px-1.5 py-px rounded text-[10px] font-medium shrink-0 ${tBg} ${tColor}`}>
          {isToolUse ? event.toolName || "tool" : cfg.label}
        </span>

        <span className={`text-xs truncate flex-1 ${isError ? "text-accent-red" : "text-text-muted"}`}>
          {summary}
        </span>

        <i className={`fa-solid fa-chevron-right text-[9px] text-text-disabled transition-transform shrink-0 mt-1 ${
          expanded ? "rotate-90" : ""
        }`} />
      </button>

      {expanded && (
        <div className="px-2.5 pb-3 pt-1 ml-[38px]">
          <ExpandedContent event={event} isThinking={isThinking} isToolUse={isToolUse} isToolResult={isToolResult} isError={isError} />
        </div>
      )}
    </div>
  )
}

function getSummary(event: ClaudeMessageRecord): string {
  if (event.eventType === "tool_use") {
    if (event.toolInput) {
      try {
        const obj = JSON.parse(event.toolInput)
        const keys = Object.keys(obj)
        if (keys.length <= 3) return keys.map(k => `${k}: ${truncate(String(obj[k]), 40)}`).join(", ")
        return `{${keys.length} params}`
      } catch {
        return truncate(event.toolInput, 120)
      }
    }
    return ""
  }
  if (event.eventType === "tool_result") {
    return truncate(event.toolResult || event.content || "", 120)
  }
  if (event.eventType === "thinking") {
    return truncate(event.content || "", 80)
  }
  if (event.eventType === "error") {
    return event.content || ""
  }
  return truncate(event.content || "", 120)
}

function ExpandedContent({ event, isThinking, isToolUse, isToolResult, isError }: {
  event: ClaudeMessageRecord
  isThinking: boolean
  isToolUse: boolean
  isToolResult: boolean
  isError: boolean
}) {
  if (isToolUse) {
    const result = event.toolResult
    const looksLikeJson = result && (result.trimStart().startsWith("{") || result.trimStart().startsWith("["))
    return (
      <div className="space-y-2">
        {event.toolInput && (
          <div className="bg-surface-deep rounded-lg p-3 overflow-auto max-h-64">
            <JsonHighlight json={event.toolInput} />
          </div>
        )}
        {result && (
          <div className="bg-surface-deep rounded-lg p-3 overflow-auto max-h-64">
            {looksLikeJson ? <JsonHighlight json={result} /> : (
              <pre className="text-xs font-mono whitespace-pre-wrap break-all text-text-muted">{result}</pre>
            )}
          </div>
        )}
      </div>
    )
  }

  if (isToolResult) {
    const content = event.toolResult || event.content || ""
    const looksLikeJson = content.trimStart().startsWith("{") || content.trimStart().startsWith("[")
    return (
      <div className="bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
        {looksLikeJson ? (
          <JsonHighlight json={content} />
        ) : (
          <pre className={`text-xs font-mono whitespace-pre-wrap break-all ${
            isError || content.startsWith("Error") ? "text-accent-red/80" : "text-text-muted"
          }`}>
            {content}
          </pre>
        )}
      </div>
    )
  }

  if (isThinking) {
    return (
      <pre className="text-xs font-mono whitespace-pre-wrap break-all text-accent-purple/60 bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
        {event.content}
      </pre>
    )
  }

  if (isError) {
    return (
      <pre className="text-xs font-mono whitespace-pre-wrap break-all text-accent-red/80 bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
        {event.content}
      </pre>
    )
  }

  // text — render markdown
  return (
    <div className="markdown-body text-sm bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
      <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeHighlight]}>
        {event.content || ""}
      </Markdown>
    </div>
  )
}
