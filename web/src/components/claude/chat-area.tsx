import { useEffect, useRef, useState, useCallback } from "react"
import type { MessageBlock as MessageBlockType } from "@/hooks/use-claude"
import { MessageBlock } from "./message-block"
import { MessageInput } from "./message-input"
import type { ClaudeSessionInfo } from "@/api/types"

interface Props {
  session: ClaudeSessionInfo | null
  messages: MessageBlockType[]
  isStreaming: boolean
  onSend: (content: string) => void
}

export function ChatArea({ session, messages, isStreaming, onSend }: Props) {
  const scrollRef = useRef<HTMLDivElement>(null)
  const shouldAutoScroll = useRef(true)
  const [showScrollBtn, setShowScrollBtn] = useState(false)

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
          <i className="fa-solid fa-terminal text-3xl mb-3 opacity-30" />
          <p className="text-sm">Select or start a session</p>
        </div>
      </div>
    )
  }

  const canSend = session.status === "Active" || session.status === "Idle"

  return (
    <div className="flex-1 flex flex-col min-h-0 relative">
      {/* Header */}
      <div className="flex items-center gap-3 px-4 py-2.5 border-b border-border-subtle shrink-0">
        <span className="font-medium text-sm">{session.projectName}</span>
        <span className="text-xs text-text-muted">{session.model || ""}</span>
        <span className="text-xs text-text-muted ml-auto">
          {session.messageCount} msg{session.messageCount !== 1 ? "s" : ""}
          {session.costUsd != null && ` · $${session.costUsd.toFixed(3)}`}
        </span>
      </div>

      {/* Messages */}
      <div ref={scrollRef} onScroll={handleScroll} className="flex-1 overflow-y-auto px-4 py-3">
        {messages.length === 0 && (
          <div className="flex items-center justify-center h-full text-text-muted text-sm">
            Send a message to get started
          </div>
        )}
        {messages.map(block => (
          <MessageBlock key={block.id} block={block} />
        ))}
        {isStreaming && (
          <div className="flex items-center gap-2 text-text-muted text-sm py-1">
            <i className="fa-solid fa-circle-notch fa-spin text-xs" />
            <span>Claude is responding...</span>
          </div>
        )}
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

      {/* Input */}
      <MessageInput
        onSend={onSend}
        disabled={!canSend}
        isStreaming={isStreaming}
      />
    </div>
  )
}
