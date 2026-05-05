import { useState } from "react"
import type { ClaudeSessionInfo } from "@/api/types"
import type { MessageBlock } from "@/hooks/use-claude"
import { SessionStatsModal, getContextPercent } from "./session-stats-modal"

interface Props {
  session: ClaudeSessionInfo
  messages: MessageBlock[]
}

const SIZE = 28
const STROKE = 3
const RADIUS = (SIZE - STROKE) / 2
const CIRCUMFERENCE = 2 * Math.PI * RADIUS

function ringColor(pct: number): string {
  if (pct < 60) return "#26A69A"
  if (pct < 80) return "#D4AA4F"
  return "#E55B5B"
}

export function ContextIndicator({ session, messages }: Props) {
  const [open, setOpen] = useState(false)
  const pct = getContextPercent(session)
  const offset = pct != null ? CIRCUMFERENCE * (1 - pct / 100) : CIRCUMFERENCE

  return (
    <>
      <button
        onClick={() => setOpen(true)}
        className="relative flex items-center justify-center rounded-full hover:bg-white/[0.06] transition-colors"
        title={pct != null ? `Context: ${pct}%` : "Session info"}
        style={{ width: SIZE + 4, height: SIZE + 4 }}
      >
        <svg width={SIZE} height={SIZE} className="-rotate-90">
          <circle
            cx={SIZE / 2}
            cy={SIZE / 2}
            r={RADIUS}
            fill="none"
            stroke="rgba(255,255,255,0.08)"
            strokeWidth={STROKE}
          />
          <circle
            cx={SIZE / 2}
            cy={SIZE / 2}
            r={RADIUS}
            fill="none"
            stroke={pct != null ? ringColor(pct) : "rgba(255,255,255,0.15)"}
            strokeWidth={STROKE}
            strokeLinecap="round"
            strokeDasharray={CIRCUMFERENCE}
            strokeDashoffset={offset}
            className="transition-all duration-500"
          />
        </svg>
        <span
          className="absolute text-[7px] font-medium"
          style={{ color: pct != null ? ringColor(pct) : "rgba(255,255,255,0.3)" }}
        >
          {pct != null ? pct : "--"}
        </span>
      </button>

      <SessionStatsModal
        open={open}
        onOpenChange={setOpen}
        session={session}
        messages={messages}
      />
    </>
  )
}
