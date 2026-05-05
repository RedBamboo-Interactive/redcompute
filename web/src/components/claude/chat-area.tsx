import { useEffect, useRef, useState, useCallback } from "react"
import type { MessageBlock as MessageBlockType } from "@/hooks/use-claude"
import { MessageBlock } from "./message-block"
import { MessageInput } from "./message-input"
import type { ClaudeSessionInfo, ImageAttachment } from "@/api/types"
import { ContextIndicator } from "./context-indicator"

interface Props {
  session: ClaudeSessionInfo | null
  messages: MessageBlockType[]
  isStreaming: boolean
  onSend: (content: string, images?: ImageAttachment[]) => void
  onStop: () => void
  onInterrupt: () => void
  onResume?: () => void
  onTogglePlanMode?: () => void
  onExecutePlan?: () => void
}

export function ChatArea({ session, messages, isStreaming, onSend, onStop, onInterrupt, onResume, onTogglePlanMode, onExecutePlan }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const shouldAutoScroll = useRef(true)
  const [showScrollBtn, setShowScrollBtn] = useState(false)
  const [interruptPending, setInterruptPending] = useState(false)
  const [interruptTimedOut, setInterruptTimedOut] = useState(false)

  const handleInterrupt = useCallback(() => {
    onInterrupt()
    setInterruptPending(true)
    setInterruptTimedOut(false)
  }, [onInterrupt])

  useEffect(() => {
    if (!isStreaming) {
      setInterruptPending(false)
      setInterruptTimedOut(false)
    }
  }, [isStreaming])

  useEffect(() => {
    if (!interruptPending) return
    const timer = setTimeout(() => setInterruptTimedOut(true), 5000)
    return () => clearTimeout(timer)
  }, [interruptPending])

  useEffect(() => {
    if (shouldAutoScroll.current && scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight
    }
  }, [messages])

  const handleScroll = useCallback(() => {
    const el = scrollRef.current
    if (!el) return
    const atBottom = el.scrollHeight - el.scrollTop - el.clientHeight < 60
    shouldAutoScroll.current = atBottom
    setShowScrollBtn(!atBottom)
  }, [])

  const scrollToBottom = useCallback(() => {
    shouldAutoScroll.current = true
    setShowScrollBtn(false)
    scrollRef.current?.scrollTo({ top: scrollRef.current.scrollHeight, behavior: "smooth" })
  }, [])

  if (!session) {
    return (
      <div className="flex-1 flex items-center justify-center text-text-muted">
        <div className="text-center">
          <i className="fa-regular fa-square-terminal text-3xl mb-3 opacity-30" />
          <p className="text-sm">Select or start a session</p>
        </div>
      </div>
    )
  }

  const canSend = session.status === "Active" || session.status === "Idle"

  return (
    <div className="flex-1 flex flex-col min-h-0 relative">
      {/* Session header */}
      <div className="shrink-0">
        <div className="max-w-3xl mx-auto flex items-center gap-3 px-4 py-2.5">
          <span className="font-medium text-sm">{session.title || session.projectName}</span>
          {session.title && <span className="text-xs text-text-muted">{session.projectName}</span>}
          {session.permissionMode === "plan" && (
            <span className="text-[10px] font-medium text-violet-300 bg-violet-500/20 px-1.5 py-0.5 rounded border border-violet-500/30">
              PLAN MODE
            </span>
          )}
          <span className="flex-1" />
          <ContextIndicator session={session} messages={messages} />
        </div>
      </div>

      {/* Messages */}
      <div ref={scrollRef} onScroll={handleScroll} className="flex-1 overflow-y-auto py-3">
        <div className="max-w-3xl mx-auto px-4">
          {messages.length === 0 && (
            <div className="flex items-center justify-center h-full text-text-muted text-sm">
              Send a message to get started
            </div>
          )}
          {messages.map(block => (
            <MessageBlock
              key={block.id}
              block={block}
              permissionMode={session?.permissionMode}
              onExecutePlan={onExecutePlan}
            />
          ))}
          {isStreaming && (
            <div className="flex items-center gap-2 text-text-muted text-sm py-1">
              <i className="fa-solid fa-circle-notch fa-spin text-xs" />
              <span>{interruptPending ? "Interrupting..." : "Claude is responding..."}</span>
            </div>
          )}
          {interruptTimedOut && isStreaming && (
            <div className="flex items-center gap-2 text-amber-400 text-sm py-1">
              <i className="fa-solid fa-triangle-exclamation text-xs" />
              <span>Interrupt not acknowledged.</span>
              <button onClick={onStop} className="underline hover:text-red-400">
                Force stop session
              </button>
            </div>
          )}
        </div>
      </div>

      {/* Scroll to bottom */}
      {showScrollBtn && (
        <button
          onClick={scrollToBottom}
          className="absolute bottom-20 right-6 w-8 h-8 rounded-full bg-white/10 hover:bg-white/20 flex items-center justify-center transition-colors shadow-lg border border-border-subtle"
          title="Scroll to bottom"
        >
          <i className="fa-solid fa-arrow-down text-xs" />
        </button>
      )}

      {/* Stopped banner with resume */}
      {(session.status === "Stopped" || session.status === "Error") && session.claudeSessionId && onResume && (
        <div className="shrink-0 border-t border-white/[0.06]">
          <div className="max-w-3xl mx-auto px-4 py-3 flex items-center justify-center gap-3">
            <span className="text-text-muted text-sm">Session {session.status === "Error" ? "ended with error" : "stopped"}</span>
            <button
              onClick={onResume}
              className="flex items-center gap-1.5 px-3 py-1.5 rounded-md bg-accent-gold/20 text-accent-gold text-sm font-medium hover:bg-accent-gold/30 transition-colors"
            >
              <i className="fa-solid fa-rotate-right text-xs" />
              Resume
            </button>
          </div>
        </div>
      )}

      {/* Input */}
      <MessageInput
        onSend={onSend}
        onInterrupt={handleInterrupt}
        disabled={!canSend}
        isStreaming={isStreaming}
        permissionMode={session?.permissionMode}
        onTogglePlanMode={onTogglePlanMode}
      />
    </div>
  )
}
