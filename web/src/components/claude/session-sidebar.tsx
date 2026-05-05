import { ScrollArea } from "@/components/ui/scroll-area"
import type { ClaudeSessionInfo } from "@/api/types"

const statusColor: Record<string, string> = {
  Starting: "#D4AA4F",
  Active: "#26A69A",
  Idle: "#26A69A",
  Stopped: "#727C7D",
  Error: "#E55B5B",
}

interface Props {
  sessions: ClaudeSessionInfo[]
  activeSessionId: string | null
  onSelect: (id: string) => void
  onStop: (id: string) => void
  onDismiss: (id: string) => void
}

export function SessionSidebar({ sessions, activeSessionId, onSelect, onStop, onDismiss }: Props) {
  return (
    <ScrollArea className="h-full">
      <div className="flex flex-col">
        {sessions.map(session => {
          const alive = session.status !== "Stopped" && session.status !== "Error"
          return (
            <button
              key={session.id}
              onClick={() => onSelect(session.id)}
              className={`flex items-center gap-3 px-4 py-3 text-left transition-colors border-b border-white/[0.06] ${
                session.id === activeSessionId ? "bg-white/[0.08]" : "hover:bg-white/[0.04]"
              }`}
            >
              <div className="w-8 h-8 rounded-lg bg-surface-base flex items-center justify-center shrink-0">
                <i className="fa-regular fa-square-terminal text-xs"
                  style={{ color: statusColor[session.status] || "#6B6F77" }} />
              </div>
              <div className="flex-1 min-w-0">
                <div className="flex items-center gap-2">
                  <span className={`text-[13px] font-medium truncate text-white ${!alive ? "opacity-50" : ""}`}>
                    {session.title || session.projectName}
                  </span>
                  {(session.status === "Active" || session.status === "Starting") && (
                    <span className="w-1.5 h-1.5 rounded-full bg-accent-gold animate-pulse shrink-0" />
                  )}
                </div>
                <div className="flex items-center gap-1.5 text-[11px] text-text-muted">
                  <span>{session.projectName}</span>
                  <span className="opacity-50">·</span>
                  <span className="capitalize">{session.status.toLowerCase()}</span>
                </div>
              </div>
              {alive ? (
                <button
                  onClick={(e) => { e.stopPropagation(); onStop(session.id) }}
                  className="text-text-muted hover:text-red-400 transition-colors"
                  title="Stop session"
                >
                  <i className="fa-solid fa-stop text-xs" />
                </button>
              ) : (
                <button
                  onClick={(e) => { e.stopPropagation(); onDismiss(session.id) }}
                  className="text-text-muted hover:text-white transition-colors"
                  title="Remove from list"
                >
                  <i className="fa-solid fa-xmark text-xs" />
                </button>
              )}
            </button>
          )
        })}
        {sessions.length === 0 && (
          <p className="text-text-muted text-sm p-4 text-center">No sessions yet</p>
        )}
      </div>
    </ScrollArea>
  )
}
