import { useState, useRef, useCallback } from "react"

interface Props {
  onSend: (content: string) => void
  disabled: boolean
  isStreaming: boolean
}

export function MessageInput({ onSend, disabled, isStreaming }: Props) {
  const [value, setValue] = useState("")
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const handleSubmit = useCallback(() => {
    const trimmed = value.trim()
    if (!trimmed || disabled) return
    onSend(trimmed)
    setValue("")
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto"
    }
  }, [value, disabled, onSend])

  const handleKeyDown = (e: React.KeyboardEvent) => {
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

  return (
    <div className="border-t border-border-subtle p-3">
      <div className="max-w-3xl mx-auto flex gap-2 items-end">
        <textarea
          ref={textareaRef}
          value={value}
          onChange={handleInput}
          onKeyDown={handleKeyDown}
          disabled={disabled}
          placeholder={disabled ? "Session not active" : "Send a message... (Enter to send, Shift+Enter for newline)"}
          rows={1}
          className="flex-1 resize-none bg-white/5 border border-border-subtle rounded-lg px-3 py-2 text-sm placeholder:text-text-muted focus:outline-none focus:border-white/30 disabled:opacity-50"
        />
        <button
          onClick={handleSubmit}
          disabled={disabled || !value.trim()}
          className="px-3 py-2 rounded-lg bg-white/10 hover:bg-white/15 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
        >
          {isStreaming ? (
            <i className="fa-solid fa-circle-notch fa-spin text-sm" />
          ) : (
            <i className="fa-solid fa-paper-plane text-sm" />
          )}
        </button>
      </div>
    </div>
  )
}
