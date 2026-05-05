import { useState, useCallback } from "react"
import type { ClaudeSessionInfo, CapabilityStatus, PermissionMode, ImageAttachment } from "@/api/types"
import type { MessageBlock } from "@/hooks/use-claude"
import { SessionSidebar } from "@/components/claude/session-sidebar"
import { ChatArea } from "@/components/claude/chat-area"
import { QueueJobDialog } from "@/components/jobs/queue-job-dialog"
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs"

interface Props {
  sessions: ClaudeSessionInfo[]
  activeSession: ClaudeSessionInfo | null
  activeSessionId: string | null
  activeMessages: MessageBlock[]
  isStreaming: boolean
  capabilities: CapabilityStatus[]
  onSelectSession: (id: string) => void
  onSendMessage: (sessionId: string, content: string, images?: ImageAttachment[]) => Promise<void>
  onInterruptSession: (sessionId: string) => Promise<void>
  onStopSession: (sessionId: string) => Promise<void>
  onResumeSession: (sessionId: string) => Promise<unknown>
  onDismissSession: (sessionId: string) => void
  onSetPermissionMode: (sessionId: string, mode: PermissionMode) => Promise<void>
  onExecutePlan: (sessionId: string) => Promise<void>
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
  onInterruptSession,
  onStopSession,
  onResumeSession,
  onDismissSession,
  onSetPermissionMode,
  onExecutePlan,
}: Props) {
  const [dialogOpen, setDialogOpen] = useState(false)
  const [mobileTab, setMobileTab] = useState(1)

  const handleSend = useCallback((content: string, images?: ImageAttachment[]) => {
    if (!activeSessionId) return
    onSendMessage(activeSessionId, content, images)
  }, [activeSessionId, onSendMessage])

  const handleInterrupt = useCallback(() => {
    if (!activeSessionId) return
    onInterruptSession(activeSessionId)
  }, [activeSessionId, onInterruptSession])

  const handleStop = useCallback(() => {
    if (!activeSessionId) return
    onStopSession(activeSessionId)
  }, [activeSessionId, onStopSession])

  const handleTogglePlanMode = useCallback(() => {
    if (!activeSessionId || !activeSession) return
    const next = activeSession.permissionMode === "plan" ? "bypassPermissions" : "plan"
    onSetPermissionMode(activeSessionId, next as PermissionMode)
  }, [activeSessionId, activeSession, onSetPermissionMode])

  const handleExecutePlan = useCallback(() => {
    if (!activeSessionId) return
    onExecutePlan(activeSessionId)
  }, [activeSessionId, onExecutePlan])

  const handleResume = useCallback(() => {
    if (!activeSessionId) return
    onResumeSession(activeSessionId)
  }, [activeSessionId, onResumeSession])

  const sidebarHeader = (
    <div className="flex items-center justify-between px-4 py-3 border-b border-white/[0.06]">
      <span className="text-[14px] font-medium text-white">Sessions</span>
      <button
        onClick={() => setDialogOpen(true)}
        className="flex items-center gap-1 text-text-muted text-[12px] hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10"
        title="New session"
      >
        <i className="fa-solid fa-plus text-xs" />
        <span>New</span>
      </button>
    </div>
  )

  return (
    <div className="flex flex-col gap-2 h-full p-4 md:p-6">
      <h1 className="text-[20px] font-semibold text-white opacity-95">AI Sessions</h1>

      {/* Desktop: side-by-side */}
      <div className="hidden md:flex gap-4 flex-1 min-h-0">
        <div className="w-80 shrink-0 bg-surface-elevated rounded-lg overflow-hidden flex flex-col">
          {sidebarHeader}
          <div className="flex-1 overflow-hidden">
            <SessionSidebar
              sessions={sessions}
              activeSessionId={activeSessionId}
              onSelect={(id) => { onSelectSession(id); setMobileTab(1) }}
              onStop={onStopSession}
              onDismiss={onDismissSession}
            />
          </div>
        </div>
        <div className="flex-1 overflow-hidden flex flex-col min-h-0">
          <ChatArea
            session={activeSession}
            messages={activeMessages}
            isStreaming={isStreaming}
            onSend={handleSend}
            onStop={handleStop}
            onInterrupt={handleInterrupt}
            onResume={handleResume}
            onTogglePlanMode={handleTogglePlanMode}
            onExecutePlan={handleExecutePlan}
          />
        </div>
      </div>

      {/* Mobile: tabbed layout */}
      <Tabs value={mobileTab} onValueChange={v => setMobileTab(v as number)} className="flex-1 min-h-0 md:hidden">
        <TabsList className="w-full">
          <TabsTrigger value={0}>Sessions</TabsTrigger>
          <TabsTrigger value={1}>Chat</TabsTrigger>
        </TabsList>
        <TabsContent value={0} className="min-h-0 overflow-hidden flex flex-col">
          <div className="bg-surface-elevated rounded-lg overflow-hidden flex flex-col flex-1 min-h-0">
            {sidebarHeader}
            <div className="flex-1 overflow-hidden">
              <SessionSidebar
                sessions={sessions}
                activeSessionId={activeSessionId}
                onSelect={(id) => { onSelectSession(id); setMobileTab(1) }}
                onStop={onStopSession}
                onDismiss={onDismissSession}
              />
            </div>
          </div>
        </TabsContent>
        <TabsContent value={1} className="min-h-0 overflow-hidden flex flex-col">
          <ChatArea
            session={activeSession}
            messages={activeMessages}
            isStreaming={isStreaming}
            onSend={handleSend}
            onStop={handleStop}
            onInterrupt={handleInterrupt}
            onResume={handleResume}
            onTogglePlanMode={handleTogglePlanMode}
            onExecutePlan={handleExecutePlan}
          />
        </TabsContent>
      </Tabs>

      <QueueJobDialog
        open={dialogOpen}
        onOpenChange={setDialogOpen}
        capabilities={capabilities}
        defaultSlug="ai-session"
      />
    </div>
  )
}
