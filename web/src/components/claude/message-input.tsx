import { useState, useRef, useCallback } from "react"

interface Props {
  onSend: (content: string) => void
  onInterrupt: () => void
  disabled: boolean
  isStreaming: boolean
}

export function MessageInput({ onSend, onInterrupt, disabled, isStreaming }: Props) {
  const [value, setValue] = useState("")
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const handleSubmit = useCallback(() => {
    if (isStreaming) {
      onInterrupt()
      return
    }
    const trimmed = value.trim()
    if (!trimmed || disabled) return
    onSend(trimmed)
    setValue("")
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto"
    }
  }, [value, disabled, isStreaming, onSend, onInterrupt])

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape" && isStreaming) {
      e.preventDefault()
      onInterrupt()
      return
    }
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault()
      handleSubmit()
    }
  }

  const handleInput = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setValue(e.target.value)
    const el = e.target
    el.style.height = "auto"
    el.style.height = Math.min(el.scrollHeight, 200) + "px"
  }

  const inputDisabled = disabled && !isStreaming

  return (
    <div className="px-3 pt-3 pb-5 shrink-0">
      <div className="max-w-3xl mx-auto flex gap-2 items-center">
        <textarea
          ref={textareaRef}
          value={value}
          onChange={handleInput}
          onKeyDown={handleKeyDown}
          disabled={inputDisabled}
          placeholder={
            inputDisabled
              ? "Session not active"
              : isStreaming
                ? "Press Escape to interrupt, or type a follow-up..."
                : "Send a message... (Enter to send, Shift+Enter for newline)"
          }
          rows={3}
          className="flex-1 resize-none bg-white/[0.06] rounded-lg px-3 py-2 text-sm font-serif placeholder:text-text-muted focus:outline-none focus:ring-1 focus:ring-white/20 disabled:opacity-50 shadow-lg"
        />
        {isStreaming ? (
          <button
            onClick={onInterrupt}
            className="px-3 py-2 rounded-lg bg-amber-500/20 hover:bg-amber-500/30 text-amber-400 transition-colors"
            title="Interrupt (Escape)"
          >
            <i className="fa-solid fa-stop text-sm" />
          </button>
        ) : (
          <button
            onClick={handleSubmit}
            disabled={disabled || !value.trim()}
            className="px-3 py-2 rounded-lg bg-white/10 hover:bg-white/15 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
          >
            <i className="fa-solid fa-paper-plane text-sm" />
          </button>
        )}
      </div>
    </div>
  )
}
