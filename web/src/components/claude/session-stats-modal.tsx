import type { ClaudeSessionInfo } from "@/api/types"
import type { MessageBlock } from "@/hooks/use-claude"
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

const MODEL_MAX_CONTEXT: Record<string, number> = {
  "claude-sonnet-4-5-20250514": 200_000,
  "claude-sonnet-4-6-20250514": 200_000,
  "claude-opus-4-20250514": 200_000,
  "claude-opus-4-6-20250514": 200_000,
  "claude-opus-4-7-20250514": 200_000,
  "claude-haiku-4-5-20251001": 200_000,
  "claude-3-5-sonnet-20241022": 200_000,
  "claude-3-5-haiku-20241022": 200_000,
  "claude-3-opus-20240229": 200_000,
}
const DEFAULT_MAX_CONTEXT = 200_000

export function getMaxContext(model?: string): number {
  if (!model) return DEFAULT_MAX_CONTEXT
  if (MODEL_MAX_CONTEXT[model]) return MODEL_MAX_CONTEXT[model]
  for (const [key, val] of Object.entries(MODEL_MAX_CONTEXT)) {
    if (model.startsWith(key.replace(/-\d{8}$/, ""))) return val
  }
  return DEFAULT_MAX_CONTEXT
}

export function getContextPercent(session: ClaudeSessionInfo): number | null {
  if (!session.inputTokens) return null
  const max = getMaxContext(session.model)
  return Math.min(100, Math.round((session.inputTokens / max) * 100))
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

export function SessionStatsModal({ open, onOpenChange, session, messages }: Props) {
  const maxContext = getMaxContext(session.model)
  const pct = getContextPercent(session)
  const toolCalls = countToolCalls(messages)
  const userMessages = messages.filter(m => m.role === "user").length

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="max-w-xs">
        <DialogHeader>
          <DialogTitle>Session Info</DialogTitle>
        </DialogHeader>

        <div className="divide-y divide-white/[0.06]">
          {/* General */}
          <div className="pb-2">
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
              label="Input tokens"
              value={formatTokens(session.inputTokens)}
              sub={session.inputTokens ? `/ ${formatTokens(maxContext)}` : undefined}
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
