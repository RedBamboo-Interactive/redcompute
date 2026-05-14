import { type ReactNode, useEffect, useState } from "react"
import { Badge, Card, CardContent, Separator } from "@redbamboo/ui"
import { api } from "@/api/client"
import type { CapabilityStatus, JobRecord, LogEntry } from "@/api/types"

const statusIconColor: Record<string, string> = {
  Queued: "#D4AA4F", Running: "#D4AA4F", Starting: "#D4AA4F",
  Completed: "#26A69A", Active: "#26A69A", Idle: "#26A69A",
  Failed: "#E55B5B", Error: "#E55B5B",
  Cancelled: "#727C7D", Stopped: "#727C7D",
}

const statusBadgeColor: Record<string, string> = {
  Queued: "bg-accent-gold-a20 text-accent-gold border-accent-gold-a30",
  Running: "bg-accent-gold-a20 text-accent-gold border-accent-gold-a30",
  Starting: "bg-accent-gold-a20 text-accent-gold border-accent-gold-a30",
  Completed: "bg-accent-teal-a20 text-accent-teal border-accent-teal-a30",
  Active: "bg-accent-teal-a20 text-accent-teal border-accent-teal-a30",
  Idle: "bg-accent-teal-a20 text-accent-teal border-accent-teal-a30",
  Failed: "bg-accent-red-a20 text-accent-red border-accent-red-a30",
  Error: "bg-accent-red-a20 text-accent-red border-accent-red-a30",
  Cancelled: "bg-text-disabled-a20 text-text-disabled border-text-disabled-a30",
  Stopped: "bg-text-disabled-a20 text-text-disabled border-text-disabled-a30",
}

export function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`
}

export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function timeAgo(dateStr: string): string {
  const seconds = Math.floor((Date.now() - new Date(dateStr).getTime()) / 1000)
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

function formatTime(dateStr: string): string {
  return new Date(dateStr).toLocaleTimeString("en-US", { hour12: false })
}

interface ParsedInput {
  prompt: string | null
  extras: Record<string, unknown>
}

export function parseInput(json: string): ParsedInput {
  try {
    const obj = JSON.parse(json) as Record<string, unknown>
    const prompt = (typeof obj.prompt === "string" ? obj.prompt : null)
      || (typeof obj.text === "string" ? obj.text : null)
    const extras: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(obj)) {
      if (k !== "prompt" && k !== "text") extras[k] = v
    }
    return { prompt, extras }
  } catch {
    return { prompt: null, extras: {} }
  }
}

export function ParamChip({ label, value, icon }: { label?: string; value: string; icon?: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-xs bg-overlay-6 rounded-lg px-2.5 py-1">
      {icon && <i className={`${icon} text-text-disabled`} />}
      {label && <span className="text-text-disabled">{label}</span>}
      <span className="text-text-primary">{value}</span>
    </span>
  )
}

interface JobDetailShellProps {
  job: JobRecord
  capability?: CapabilityStatus
  title?: string
  status?: { label: string; className?: string }
  live?: boolean
  actions?: ReactNode
  chips?: ReactNode
  children: ReactNode
  showLogs?: boolean
  fillHeight?: boolean
}

export function JobDetailShell({
  job, capability, title, status, live, actions, chips, children,
  showLogs = true, fillHeight = false,
}: JobDetailShellProps) {
  const [logs, setLogs] = useState<LogEntry[]>([])

  useEffect(() => {
    if (!showLogs) return
    api.get<{ entries: { id: number; timestamp: string; tag: string; message: string; isError: boolean }[] }>(`/jobs/${job.id}/logs?limit=50`)
      .then(data => setLogs(data.entries as unknown as LogEntry[]))
      .catch(() => {})
  }, [job.id, showLogs])

  const { prompt } = job.inputJson ? parseInput(job.inputJson) : { prompt: null }
  const statusLabel = status?.label || job.status
  const statusClass = status?.className || statusBadgeColor[statusLabel] || ""
  const displayTitle = title || job.name || capability?.displayName || job.capabilitySlug

  return (
    <div className={`p-5 ${fillHeight ? "flex flex-col h-full gap-6" : "space-y-6"}`}>
      {/* Header block */}
      <div className="space-y-2.5">
        {/* Title row */}
        <div className="flex items-center gap-3">
          {capability?.icon && (
            <i className={`${capability.icon} text-lg`} style={{ color: statusIconColor[statusLabel] || "#6B6F77" }} />
          )}
          <h2 className="text-xl font-semibold">{displayTitle}</h2>
          <Badge variant="outline" className={statusClass}>{statusLabel}</Badge>
          {live && (
            <span className="flex items-center gap-1.5 text-xs text-accent-teal">
              <span className="w-1.5 h-1.5 rounded-full bg-accent-teal animate-pulse" />
              Live
            </span>
          )}
          <span className="ml-auto" />
          {actions}
        </div>

        {/* Metadata row */}
        <div className="flex items-center gap-2 flex-wrap">
          <button
            onClick={() => navigator.clipboard.writeText(job.id)}
            title={job.id}
            className="inline-flex items-center gap-1.5 font-mono text-xs text-text-disabled hover:text-text-muted bg-overlay-6 hover:bg-overlay-10 rounded-lg px-2.5 py-1 transition-colors cursor-pointer"
          >
            #{job.id.slice(0, 8)}
          </button>
          <ParamChip icon="fa-solid fa-server" value={job.providerName} />
          {job.callerInfo && (
            <ParamChip icon="fa-solid fa-arrow-right-to-bracket" value={job.callerInfo} />
          )}
          {job.rationale && (
            <ParamChip icon="fa-solid fa-tag" value={job.rationale} />
          )}
          {chips}
        </div>

        {/* Timing row */}
        <div className="flex items-center gap-2 text-xs text-text-disabled">
          <i className="fa-solid fa-clock text-[10px]" />
          {job.startedAt && <span>{formatTime(job.startedAt)}</span>}
          {job.startedAt && job.completedAt && (
            <span className="text-text-disabled-a50">&rarr;</span>
          )}
          {job.completedAt && <span>{formatTime(job.completedAt)}</span>}
          {job.durationMs != null && (
            <>
              <span className="text-text-disabled-a50">&middot;</span>
              <span>{formatDuration(job.durationMs)}</span>
            </>
          )}
          <span className="text-text-disabled-a50">&middot;</span>
          <span>{timeAgo(job.queuedAt)}</span>
        </div>
      </div>

      {/* Prompt */}
      {prompt && (
        <div className="border-l-2 border-accent-teal-a40 pl-4 py-2">
          <p className="text-sm font-serif leading-relaxed text-text-primary">{prompt}</p>
        </div>
      )}

      {/* Error */}
      {job.errorMessage && (
        <Card className="bg-accent-red-a10 border-accent-red-a30">
          <CardContent className="p-5">
            <p className="text-sm font-medium text-accent-red mb-1.5">Error</p>
            <p className="text-sm text-accent-red-a80">{job.errorMessage}</p>
            {job.errorDetails && (
              <pre className="mt-2 text-xs font-mono text-accent-red-a60 whitespace-pre-wrap max-h-40 overflow-auto">{job.errorDetails}</pre>
            )}
          </CardContent>
        </Card>
      )}

      {/* Type-specific content */}
      {fillHeight ? (
        <div className="flex-1 min-h-0 flex flex-col gap-4">{children}</div>
      ) : (
        children
      )}

      {/* Logs */}
      {showLogs && logs.length > 0 && (
        <>
          <Separator />
          <div>
            <p className="text-sm font-medium mb-3">Logs ({logs.length})</p>
            <div className="bg-surface-base rounded-lg p-3.5 max-h-56 overflow-auto space-y-0.5">
              {logs.map((log, i) => (
                <div key={i} className="flex gap-3 text-xs font-mono px-1.5 py-1">
                  <span className="text-overlay-40 w-[82px] shrink-0">
                    {new Date(log.timestamp).toLocaleTimeString("en-US", { hour12: false, fractionalSecondDigits: 3 })}
                  </span>
                  <span className={log.isError ? "text-accent-red" : "text-text-muted"}>{log.message}</span>
                </div>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  )
}
