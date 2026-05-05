import { useState, useCallback } from "react"
import type { ClaudeSessionInfo, CapabilityStatus } from "@/api/types"
import type { MessageBlock } from "@/hooks/use-claude"
import { SessionSidebar } from "@/components/claude/session-sidebar"
import { ChatArea } from "@/components/claude/chat-area"
import { QueueJobDialog } from "@/components/jobs/queue-job-dialog"

interface Props {
  sessions: ClaudeSessionInfo[]
  activeSession: ClaudeSessionInfo | null
  activeSessionId: string | null
  activeMessages: MessageBlock[]
  isStreaming: boolean
  capabilities: CapabilityStatus[]
  onSelectSession: (id: string) => void
  onSendMessage: (sessionId: string, content: string) => Promise<void>
  onStopSession: (sessionId: string) => Promise<void>
}

export function ClaudePage({
  sessions,
  activeSession,
  activeSessionId,
  activeMessages,
  isStreaming,
  capabilities,
  onSelectSession,
  onSendMessage,
  onStopSession,
}: Props) {
  const [dialogOpen, setDialogOpen] = useState(false)
  const [mobileTab, setMobileTab] = useState<"sessions" | "chat">("chat")

  const handleSend = useCallback((content: string) => {
    if (!activeSessionId) return
    onSendMessage(activeSessionId, content)
  }, [activeSessionId, onSendMessage])

  return (
    <div className="flex flex-col h-full">
      {/* Mobile tabs */}
      <div className="flex md:hidden border-b border-border-subtle">
        <button
          onClick={() => setMobileTab("sessions")}
          className={`flex-1 py-2.5 text-sm font-medium transition-colors ${
            mobileTab === "sessions" ? "text-white border-b-2 border-white" : "text-text-muted"
          }`}
        >
          Sessions
        </button>
        <button
          onClick={() => setMobileTab("chat")}
          className={`flex-1 py-2.5 text-sm font-medium transition-colors ${
            mobileTab === "chat" ? "text-white border-b-2 border-white" : "text-text-muted"
          }`}
        >
          Chat
        </button>
      </div>

      <div className="flex flex-1 min-h-0">
        {/* Sidebar */}
        <div className={`w-60 shrink-0 ${mobileTab === "sessions" ? "block" : "hidden"} md:block`}>
          <SessionSidebar
            sessions={sessions}
            activeSessionId={activeSessionId}
            onSelect={(id) => { onSelectSession(id); setMobileTab("chat") }}
            onNewSession={() => setDialogOpen(true)}
            onStop={onStopSession}
          />
        </div>

        {/* Chat */}
        <div className={`flex-1 flex flex-col min-h-0 ${mobileTab === "chat" ? "flex" : "hidden"} md:flex`}>
          <ChatArea
            session={activeSession}
            messages={activeMessages}
            isStreaming={isStreaming}
            onSend={handleSend}
          />
        </div>
      </div>

      <QueueJobDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        capabilities={capabilities}
        defaultSlug="ai-session"
      />
    </div>
  )
}
