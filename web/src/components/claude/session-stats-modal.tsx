import { useState } from "react"
import type { ClaudeSessionInfo } from "@/api/types"
import type { MessageBlock } from "@/hooks/use-claude"
import { api } from "@/api/client"
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog"

interface Props {
  open: boolean
  onOpenChange: (open: boolean) => void
  session: ClaudeSessionInfo
  messages: MessageBlock[]
}

const MODEL_OPTIONS = [
  { value: "sonnet", label: "Sonnet" },
  { value: "opus", label: "Opus" },
  { value: "haiku", label: "Haiku" },
]

const EFFORT_OPTIONS = [
  { value: "low", label: "Low" },
  { value: "medium", label: "Medium" },
  { value: "high", label: "High" },
  { value: "xhigh", label: "XHigh" },
  { value: "max", label: "Max" },
]

const DEFAULT_MAX_CONTEXT = 200_000

export function getMaxContext(model?: string): number {
  if (!model) return DEFAULT_MAX_CONTEXT
  if (model.includes("[1m]") || model.includes("1m")) return 1_000_000
  return DEFAULT_MAX_CONTEXT
}

function getTotalInputTokens(session: ClaudeSessionInfo): number {
  return (session.inputTokens || 0) + (session.cacheReadInputTokens || 0) + (session.cacheCreationInputTokens || 0)
}

export function getContextPercent(session: ClaudeSessionInfo): number | null {
  const total = getTotalInputTokens(session)
  if (total === 0) return null
  const max = getMaxContext(session.model)
  return Math.min(100, Math.round((total / max) * 100))
}

function formatDuration(startedAt: string): string {
  const ms = Date.now() - new Date(startedAt).getTime()
  const seconds = Math.floor(ms / 1000)
  if (seconds < 60) return `${seconds}s`
  const minutes = Math.floor(seconds / 60)
  if (minutes < 60) return `${minutes}m ${seconds % 60}s`
  const hours = Math.floor(minutes / 60)
  return `${hours}h ${minutes % 60}m`
}

function formatTokens(n?: number | null): string {
  if (n == null) return "--"
  if (n >= 1000) return `${(n / 1000).toFixed(1)}K`
  return n.toLocaleString()
}

function formatCost(cost?: number | null): string {
  if (cost == null) return "$0.00"
  return `$${cost.toFixed(4)}`
}

function shortModel(model?: string | null): string {
  if (!model) return "--"
  return model.replace(/-\d{8}$/, "")
}

function currentModelAlias(model?: string | null): string {
  if (!model) return ""
  const lower = model.toLowerCase()
  if (lower.includes("opus")) return "opus"
  if (lower.includes("haiku")) return "haiku"
  if (lower.includes("sonnet")) return "sonnet"
  return model
}

function countToolCalls(messages: MessageBlock[]): number {
  let count = 0
  for (const msg of messages) {
    for (const part of msg.parts) {
      if (part.type === "tool_use") count++
    }
  }
  return count
}

function StatRow({ label, value, sub }: { label: string; value: string; sub?: string }) {
  return (
    <div className="flex items-baseline justify-between py-1.5">
      <span className="text-xs text-text-muted">{label}</span>
      <span className="text-sm font-medium">
        {value}
        {sub && <span className="text-xs text-text-muted ml-1">{sub}</span>}
      </span>
    </div>
  )
}

function ConfigSelect({ label, value, options, onChange, disabled }: {
  label: string
  value: string
  options: { value: string; label: string }[]
  onChange: (value: string) => void
  disabled?: boolean
}) {
  return (
    <div className="flex items-center justify-between py-1.5">
      <span className="text-xs text-text-muted">{label}</span>
      <select
        value={value}
        onChange={e => onChange(e.target.value)}
        disabled={disabled}
        className="bg-white/[0.06] border border-white/[0.1] rounded px-2 py-0.5 text-xs text-white outline-none focus:border-white/[0.2] disabled:opacity-50"
      >
        {options.map(o => (
          <option key={o.value} value={o.value}>{o.label}</option>
        ))}
      </select>
    </div>
  )
}

export function SessionStatsModal({ open, onOpenChange, session, messages }: Props) {
  const maxContext = getMaxContext(session.model)
  const pct = getContextPercent(session)
  const toolCalls = countToolCalls(messages)
  const userMessages = messages.filter(m => m.role === "user").length
  const [updating, setUpdating] = useState(false)

  const handleConfigChange = async (config: { model?: string; effort?: string }) => {
    setUpdating(true)
    try {
      await api.post(`/claude/sessions/${session.id}/config`, config)
    } finally {
      setUpdating(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xs">
        <DialogHeader>
          <DialogTitle>Session Info</DialogTitle>
        </DialogHeader>

        <div className="divide-y divide-white/[0.06]">
          {/* Config */}
          <div className="pb-2">
            <ConfigSelect
              label="Model"
              value={currentModelAlias(session.model)}
              options={MODEL_OPTIONS}
              onChange={v => handleConfigChange({ model: v })}
              disabled={updating}
            />
            <ConfigSelect
              label="Effort"
              value={session.effort || "high"}
              options={EFFORT_OPTIONS}
              onChange={v => handleConfigChange({ effort: v })}
              disabled={updating}
            />
            {updating && (
              <div className="flex items-center gap-1.5 text-[10px] text-text-muted mt-1">
                <i className="fa-solid fa-circle-notch fa-spin" />
                <span>Restarting session...</span>
              </div>
            )}
          </div>

          {/* General */}
          <div className="py-2">
            <StatRow label="Model" value={shortModel(session.model)} />
            <StatRow label="Cost" value={formatCost(session.costUsd)} />
            <StatRow label="Duration" value={formatDuration(session.startedAt)} />
            <StatRow label="Status" value={session.status} />
          </div>

          {/* Messages */}
          <div className="py-2">
            <StatRow label="Messages" value={String(session.messageCount || messages.length)} />
            <StatRow label="User messages" value={String(userMessages)} />
            <StatRow label="Tool calls" value={String(toolCalls)} />
          </div>

          {/* Context */}
          <div className="pt-2">
            <StatRow
              label="Context tokens"
              value={formatTokens(getTotalInputTokens(session) || null)}
              sub={getTotalInputTokens(session) ? `/ ${formatTokens(maxContext)}` : undefined}
            />
            <StatRow label="Output tokens" value={formatTokens(session.outputTokens)} />
            {session.cacheReadInputTokens != null && (
              <StatRow label="Cache read" value={formatTokens(session.cacheReadInputTokens)} />
            )}
            {session.cacheCreationInputTokens != null && (
              <StatRow label="Cache write" value={formatTokens(session.cacheCreationInputTokens)} />
            )}

            {/* Context bar */}
            {pct != null && (
              <div className="mt-2">
                <div className="flex items-center justify-between text-[10px] text-text-muted mb-1">
                  <span>Context usage</span>
                  <span>{pct}%</span>
                </div>
                <div className="h-1.5 rounded-full bg-white/[0.06] overflow-hidden">
                  <div
                    className="h-full rounded-full transition-all duration-500"
                    style={{
                      width: `${pct}%`,
                      backgroundColor: pct < 60 ? "#26A69A" : pct < 80 ? "#D4AA4F" : "#E55B5B",
                    }}
                  />
                </div>
              </div>
            )}
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
