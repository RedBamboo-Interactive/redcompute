import type { ClaudeSessionInfo } from "@/api/types"

const statusColors: Record<string, string> = {
  Starting: "bg-yellow-500",
  Active: "bg-green-500 animate-pulse",
  Idle: "bg-green-500",
  Stopped: "bg-zinc-500",
  Error: "bg-red-500",
}

interface Props {
  sessions: ClaudeSessionInfo[]
  activeSessionId: string | null
  onSelect: (id: string) => void
  onNewSession: () => void
  onStop: (id: string) => void
}

export function SessionSidebar({ sessions, activeSessionId, onSelect, onNewSession, onStop }: Props) {
  return (
    <div className="flex flex-col h-full border-r border-border-subtle">
      <div className="p-3 border-b border-border-subtle">
        <button
          onClick={onNewSession}
          className="w-full px-3 py-2 rounded-lg bg-white/10 hover:bg-white/15 text-sm font-medium transition-colors"
        >
          <i className="fa-solid fa-plus mr-2" />
          New Session
        </button>
      </div>
      <div className="flex-1 overflow-y-auto">
        {sessions.length === 0 && (
          <p className="p-4 text-sm text-text-muted">No sessions yet</p>
        )}
        {sessions.map(session => (
          <button
            key={session.id}
            onClick={() => onSelect(session.id)}
            className={`w-full text-left px-3 py-2.5 border-b border-border-subtle transition-colors group ${
              session.id === activeSessionId ? "bg-white/10" : "hover:bg-white/5"
            }`}
          >
            <div className="flex items-center gap-2">
              <span className={`w-2 h-2 rounded-full shrink-0 ${statusColors[session.status] || "bg-zinc-500"}`} />
              <span className="text-sm font-medium truncate flex-1">{session.projectName}</span>
              {session.status !== "Stopped" && session.status !== "Error" && (
                <button
                  onClick={(e) => { e.stopPropagation(); onStop(session.id) }}
                  className="opacity-0 group-hover:opacity-100 text-text-muted hover:text-red-400 transition-opacity"
                  title="Stop session"
                >
                  <i className="fa-solid fa-stop text-xs" />
                </button>
              )}
            </div>
            <div className="flex items-center gap-2 mt-0.5 ml-4">
              <span className="text-xs text-text-muted">{session.model || "starting..."}</span>
              {session.costUsd != null && (
                <span className="text-xs text-text-muted">${session.costUsd.toFixed(3)}</span>
              )}
            </div>
          </button>
        ))}
      </div>
    </div>
  )
}
