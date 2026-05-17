import { useState, useRef, useEffect, useCallback } from "react"
import { JsonHighlight } from "@redbamboo/utility"
import { api } from "@/api/client"
import { useSessionEvents } from "@/hooks/use-session-events"
import type { CapabilityStatus, JobRecord, ClaudeMessageRecord } from "@/api/types"
import { JobDetailShell, ParamChip, parseInput } from "./job-detail-shell"
import Markdown from "react-markdown"
import remarkGfm from "remark-gfm"
import rehypeHighlight from "rehype-highlight"

const readOnlyTools = new Set([
  "Read", "Glob", "Grep", "Agent", "WebSearch", "WebFetch", "Explore",
  "ToolSearch", "AskUserQuestion", "Monitor", "CronList",
])

const eventTypeConfig: Record<string, { label: string; color: string; bg: string }> = {
  text: { label: "text", color: "text-accent-teal", bg: "bg-accent-teal-a20" },
  thinking: { label: "thinking", color: "text-accent-purple", bg: "bg-accent-purple-a20" },
  tool_use: { label: "tool", color: "text-accent-gold", bg: "bg-accent-gold-a20" },
  tool_result: { label: "result", color: "text-accent-teal", bg: "bg-accent-teal-a20" },
  error: { label: "error", color: "text-accent-red", bg: "bg-accent-red-a20" },
  system: { label: "system", color: "text-text-disabled", bg: "bg-overlay-6" },
  prompt: { label: "prompt", color: "text-blue-400", bg: "bg-blue-400-a20" },
}

function toolColor(toolName?: string): string {
  if (!toolName) return "text-accent-teal"
  return readOnlyTools.has(toolName) ? "text-accent-teal" : "text-accent-gold"
}

function toolBg(toolName?: string): string {
  if (!toolName) return "bg-accent-teal-a20"
  return readOnlyTools.has(toolName) ? "bg-accent-teal-a20" : "bg-accent-gold-a20"
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
  if (model.includes("codex-mini")) return "Codex Mini"
  if (model.includes("gpt-5.5")) return "GPT-5.5"
  if (model.includes("gpt-5.4-mini")) return "GPT-5.4m"
  if (model.includes("gpt-5.4")) return "GPT-5.4"
  if (model.includes("gpt-5.3")) return "GPT-5.3"
  return model.split("-").slice(0, 2).join("-")
}

function truncate(s: string, max: number): string {
  if (s.length <= max) return s
  return s.slice(0, max) + "..."
}

export function AiSessionDetail({ job, capability }: { job: JobRecord; capability?: CapabilityStatus }) {
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
  const { extras: inputExtras } = job.inputJson ? parseInput(job.inputJson) : { extras: {} as Record<string, unknown> }

  const sessionChips = (
    <>
      <ParamChip icon="fa-solid fa-microchip" value={shortModel(session.model)} />
      {session.costUsd != null && <ParamChip icon="fa-solid fa-dollar-sign" value={session.costUsd.toFixed(4)} />}
      <ParamChip icon="fa-solid fa-arrow-down" value={formatTokens(session.inputTokens)} />
      <ParamChip icon="fa-solid fa-arrow-up" value={formatTokens(session.outputTokens)} />
      {session.cacheReadInputTokens != null && session.cacheReadInputTokens > 0 && (
        <ParamChip icon="fa-solid fa-bolt" value={formatTokens(session.cacheReadInputTokens)} />
      )}
      {session.effort && <ParamChip icon="fa-solid fa-gauge" value={session.effort} />}
      <ParamChip icon="fa-solid fa-messages" value={String(session.messageCount)} />
      {typeof inputExtras.container === "string" && (
        <ParamChip icon="fa-solid fa-cube" label="Docker" value={truncate(String(inputExtras.container), 20)} />
      )}
      {typeof inputExtras.workingDir === "string" && (
        <ParamChip icon="fa-solid fa-folder" value={String(inputExtras.workingDir)} />
      )}
      {!inputExtras.workingDir && session.projectPath && (
        <ParamChip icon="fa-solid fa-folder" value={session.projectPath} />
      )}
      {session.permissionMode && session.permissionMode !== "bypassPermissions" && (
        <ParamChip icon="fa-solid fa-shield" value={session.permissionMode} />
      )}
      {typeof inputExtras.maxTurns === "number" && inputExtras.maxTurns > 1 && (
        <ParamChip icon="fa-solid fa-repeat" label="Turns" value={String(inputExtras.maxTurns)} />
      )}
      {typeof inputExtras.timeout === "number" && (
        <ParamChip icon="fa-solid fa-hourglass" label="Timeout" value={`${inputExtras.timeout}s`} />
      )}
    </>
  )

  const codeRedButton = !isExecuteResult ? (
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
      className={`inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded-md transition-colors ${
        codeRedStatus === "sent"
          ? "bg-overlay-8 text-accent-teal"
          : codeRedStatus === "error"
          ? "bg-overlay-8 text-accent-red"
          : "bg-overlay-6 text-text-muted hover:bg-overlay-10 hover:text-text-primary"
      }`}
    >
      <i className={`${
        codeRedStatus === "sent" ? "fa-solid fa-check text-accent-teal" : codeRedStatus === "error" ? "fa-solid fa-xmark text-accent-red" : "fa-regular fa-square-terminal text-accent-red"
      } text-[11px]`} />
      {codeRedStatus === "sent" ? "Sent to CodeRed" : codeRedStatus === "error" ? "CodeRed unreachable" : isLive ? "Open in CodeRed" : "Resume in CodeRed"}
    </button>
  ) : undefined

  return (
    <JobDetailShell
      job={job}
      capability={capability}
      title={session.title || session.projectName}
      status={{ label: session.status }}
      live={isLive}
      actions={codeRedButton}
      chips={sessionChips}
      showLogs={false}
      showPrompt={false}
      fillHeight
    >
      {/* Toolbar */}
      <div className="flex items-center gap-2 flex-wrap">
        <div className="relative flex-1 min-w-[200px]">
          <i className="fa-solid fa-search absolute left-2.5 top-1/2 -translate-y-1/2 text-text-disabled text-xs" />
          <input
            type="text"
            placeholder="Search events..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="w-full bg-overlay-8 rounded-lg pl-8 pr-3 py-1.5 text-sm text-text-primary placeholder-text-disabled outline-none focus:ring-1 focus:ring-accent-teal-a50"
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
                  : "bg-overlay-4 text-text-disabled border-transparent hover:bg-overlay-8"
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
            <div className="divide-y divide-overlay-4">
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
            className="absolute bottom-3 right-3 w-8 h-8 rounded-full bg-surface-elevated border border-overlay-10 flex items-center justify-center text-text-muted hover:text-text-primary transition-colors shadow-lg"
          >
            <i className="fa-solid fa-arrow-down text-xs" />
          </button>
        )}
      </div>
    </JobDetailShell>
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
          expanded ? "bg-overlay-6" : "hover:bg-overlay-3"
        }`}
      >
        <span className="font-mono text-[11px] text-overlay-40 w-[28px] shrink-0 pt-px text-right">
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
            isError || content.startsWith("Error") ? "text-accent-red-a80" : "text-text-muted"
          }`}>
            {content}
          </pre>
        )}
      </div>
    )
  }

  if (isThinking) {
    return (
      <pre className="text-xs font-mono whitespace-pre-wrap break-all text-accent-purple-a60 bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
        {event.content}
      </pre>
    )
  }

  if (isError) {
    return (
      <pre className="text-xs font-mono whitespace-pre-wrap break-all text-accent-red-a80 bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
        {event.content}
      </pre>
    )
  }

  return (
    <div className="markdown-body text-sm bg-surface-deep rounded-lg p-3 overflow-auto max-h-96">
      <Markdown remarkPlugins={[remarkGfm]} rehypePlugins={[rehypeHighlight]}>
        {event.content || ""}
      </Markdown>
    </div>
  )
}
