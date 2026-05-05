import { useState, useRef, useCallback } from "react"
import type { PermissionMode, ImageAttachment } from "@/api/types"

interface Props {
  onSend: (content: string, images?: ImageAttachment[]) => void
  onInterrupt: () => void
  disabled: boolean
  isStreaming: boolean
  permissionMode?: PermissionMode
  onTogglePlanMode?: () => void
}

function readImageFile(file: File): Promise<ImageAttachment | null> {
  const mediaType = file.type as ImageAttachment["mediaType"]
  if (!["image/png", "image/jpeg", "image/gif", "image/webp"].includes(mediaType)) return Promise.resolve(null)
  return new Promise((resolve) => {
    const reader = new FileReader()
    reader.onload = () => {
      const result = reader.result as string
      const base64 = result.split(",")[1]
      if (base64) resolve({ mediaType, base64 })
      else resolve(null)
    }
    reader.onerror = () => resolve(null)
    reader.readAsDataURL(file)
  })
}

export function MessageInput({ onSend, onInterrupt, disabled, isStreaming, permissionMode, onTogglePlanMode }: Props) {
  const [value, setValue] = useState("")
  const [images, setImages] = useState<ImageAttachment[]>([])
  const [dragOver, setDragOver] = useState(false)
  const textareaRef = useRef<HTMLTextAreaElement>(null)

  const addImages = useCallback(async (files: File[]) => {
    const results = await Promise.all(files.map(readImageFile))
    const valid = results.filter((r): r is ImageAttachment => r !== null)
    if (valid.length) setImages(prev => [...prev, ...valid])
  }, [])

  const handleSubmit = useCallback(() => {
    if (isStreaming) {
      onInterrupt()
      return
    }
    const trimmed = value.trim()
    if ((!trimmed && images.length === 0) || disabled) return
    onSend(trimmed, images.length > 0 ? images : undefined)
    setValue("")
    setImages([])
    if (textareaRef.current) {
      textareaRef.current.style.height = "auto"
    }
  }, [value, images, disabled, isStreaming, onSend, onInterrupt])

  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Escape" && isStreaming) {
      e.preventDefault()
      onInterrupt()
      return
    }
    if (e.key === "Tab" && e.shiftKey && onTogglePlanMode) {
      e.preventDefault()
      onTogglePlanMode()
      return
    }
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault()
      handleSubmit()
    }
  }

  const handlePaste = useCallback(async (e: React.ClipboardEvent) => {
    const items = Array.from(e.clipboardData.items)
    const imageFiles = items
      .filter(item => item.type.startsWith("image/"))
      .map(item => item.getAsFile())
      .filter((f): f is File => f !== null)
    if (imageFiles.length > 0) {
      e.preventDefault()
      await addImages(imageFiles)
    }
  }, [addImages])

  const handleDrop = useCallback(async (e: React.DragEvent) => {
    e.preventDefault()
    setDragOver(false)
    const files = Array.from(e.dataTransfer.files).filter(f => f.type.startsWith("image/"))
    if (files.length > 0) await addImages(files)
  }, [addImages])

  const handleInput = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    setValue(e.target.value)
    const el = e.target
    el.style.height = "auto"
    el.style.height = Math.min(el.scrollHeight, 200) + "px"
  }

  const removeImage = useCallback((index: number) => {
    setImages(prev => prev.filter((_, i) => i !== index))
  }, [])

  const inputDisabled = disabled && !isStreaming
  const isPlan = permissionMode === "plan"

  return (
    <div className="px-3 pt-3 pb-5 shrink-0">
      <div className="max-w-3xl mx-auto flex gap-2 items-end">
        <div
          className={`flex-1 rounded-lg bg-white/[0.06] shadow-lg transition-colors ${dragOver ? "ring-2 ring-accent-gold/50" : ""}`}
          onDragOver={(e) => { e.preventDefault(); setDragOver(true) }}
          onDragLeave={() => setDragOver(false)}
          onDrop={handleDrop}
        >
          {images.length > 0 && (
            <div className="flex flex-wrap gap-2 px-3 pt-2.5">
              {images.map((img, i) => (
                <div key={i} className="relative group">
                  <img
                    src={`data:${img.mediaType};base64,${img.base64}`}
                    alt=""
                    className="h-16 w-16 object-cover rounded-md border border-white/10"
                  />
                  <button
                    onClick={() => removeImage(i)}
                    className="absolute -top-1.5 -right-1.5 w-5 h-5 rounded-full bg-red-500/80 hover:bg-red-500 text-white text-[10px] flex items-center justify-center opacity-0 group-hover:opacity-100 transition-opacity"
                  >
                    <i className="fa-solid fa-xmark" />
                  </button>
                </div>
              ))}
            </div>
          )}
          <textarea
            ref={textareaRef}
            value={value}
            onChange={handleInput}
            onKeyDown={handleKeyDown}
            onPaste={handlePaste}
            disabled={inputDisabled}
            placeholder={
              inputDisabled
                ? "Session not active"
                : isStreaming
                  ? "Press Escape to interrupt, or type a follow-up..."
                  : "Send a message... (Ctrl+V to paste images)"
            }
            rows={3}
            className="w-full resize-none bg-transparent px-3 py-2 text-sm font-serif placeholder:text-text-muted focus:outline-none disabled:opacity-50"
          />
        </div>
        <div className="flex flex-col gap-1.5 shrink-0">
          {onTogglePlanMode && (
            <button
              onClick={onTogglePlanMode}
              disabled={inputDisabled}
              className={`min-w-16 px-3 py-1.5 rounded-lg text-xs font-medium transition-colors border ${
                isPlan
                  ? "bg-violet-500/20 text-violet-300 hover:bg-violet-500/30 border-violet-500/30"
                  : "bg-white/[0.06] text-text-muted hover:bg-white/10 border-transparent"
              } disabled:opacity-30 disabled:cursor-not-allowed`}
              title="Toggle plan mode (Shift+Tab)"
            >
              <i className={`fa-solid ${isPlan ? "fa-compass-drafting" : "fa-bolt"} mr-1.5`} />
              {isPlan ? "Plan" : "Act"}
            </button>
          )}
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
              disabled={disabled || (!value.trim() && images.length === 0)}
              className="px-3 py-2 rounded-lg bg-white/10 hover:bg-white/15 disabled:opacity-30 disabled:cursor-not-allowed transition-colors"
            >
              <i className="fa-solid fa-paper-plane text-sm" />
            </button>
          )}
        </div>
      </div>
    </div>
  )
}
