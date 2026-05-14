import { useEffect, useRef } from "react"
import type { LogEntry, TagInfo } from "@/api/types"

export function ConsolePanel({ open, onOpenChange, entries, tags, search, setSearch, tagFilter, setTagFilter, selectedEntry, setSelectedEntry, autoScrollRef }: {
  open: boolean
  onOpenChange: (open: boolean) => void
  entries: LogEntry[]
  tags: TagInfo[]
  search: string
  setSearch: (s: string) => void
  tagFilter: string | null
  setTagFilter: (t: string | null) => void
  selectedEntry: LogEntry | null
  setSelectedEntry: (e: LogEntry | null) => void
  autoScrollRef: React.MutableRefObject<boolean>
}) {
  const bottomRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (open && autoScrollRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: "smooth" })
    }
  }, [entries.length, open, autoScrollRef])

  if (!open) return null

  return (
    <div className="fixed inset-0 z-50 flex justify-end">
      <div className="absolute inset-0 bg-black/40" onClick={() => onOpenChange(false)} />

      <div className="relative w-full max-w-2xl bg-surface-deep border-l border-border-subtle flex flex-col h-full">
        {/* Header */}
        <div className="flex items-center justify-between px-4 py-3 border-b border-overlay-6 shrink-0">
          <span className="text-sm font-semibold text-contrast">Console</span>
          <button
            onClick={() => onOpenChange(false)}
            className="text-text-muted hover:text-contrast p-1.5 rounded hover:bg-overlay-10 transition-colors"
            title="Close console"
          >
            <i className="fa-solid fa-xmark text-sm" />
          </button>
        </div>

        {/* Toolbar */}
        <div className="flex flex-col gap-2 px-3 py-2 border-b border-[#3F4147] shrink-0 md:flex-row md:items-center md:gap-3 md:flex-wrap">
          <div className="flex items-center gap-1.5 bg-overlay-8 rounded px-2 py-1.5 w-full md:max-w-[250px] md:w-auto md:py-1">
            <i className="fa-solid fa-magnifying-glass text-[11px] text-text-disabled" />
            <input
              type="text"
              placeholder="Search logs..."
              value={search}
              onChange={e => setSearch(e.target.value)}
              className="bg-transparent border-none outline-none text-[12px] text-[#DCDDDE] placeholder-text-disabled w-full"
            />
          </div>

          <div className="flex gap-1.5 overflow-x-auto flex-nowrap md:flex-wrap md:flex-1 md:overflow-visible">
            {tags.map(t => (
              <button
                key={t.tag}
                onClick={() => setTagFilter(tagFilter === t.tag ? null : t.tag)}
                className="rounded px-1.5 py-0.5 text-[10px] cursor-pointer transition-colors shrink-0 md:shrink"
                style={{
                  backgroundColor: tagFilter === t.tag ? `${t.color}33` : "rgba(255,255,255,0.08)",
                  color: tagFilter === t.tag ? t.color : "#ADAEB3",
                }}
              >
                {t.tag}
              </button>
            ))}
          </div>

          <div className="flex items-center justify-between md:contents">
            <span className="text-[11px] text-text-disabled">
              {entries.length}/{entries.length}
            </span>
            <button className="flex items-center gap-1 text-text-muted text-[12px] hover:text-contrast transition-colors px-2 py-1 rounded hover:bg-overlay-10">
              <i className="fa-solid fa-trash-can text-xs" />
              <span>Clear</span>
            </button>
          </div>
        </div>

        {/* Log entries */}
        <div className="flex-1 overflow-auto">
          {entries.map(entry => (
            <button
              key={entry.id}
              onClick={() => setSelectedEntry(selectedEntry?.id === entry.id ? null : entry)}
              className={`flex items-start gap-2 w-full text-left px-2 py-1 transition-colors ${
                selectedEntry?.id === entry.id ? "bg-overlay-8" : "hover:bg-overlay-3"
              }`}
            >
              <span className="font-mono text-[11px] text-overlay-40 w-[82px] shrink-0 hidden md:inline">
                {formatTimestamp(entry.timestamp)}
              </span>

              {entry.tag && (
                <span
                  className="rounded px-1.5 py-px text-[10px] font-medium shrink-0"
                  style={{
                    backgroundColor: `${getTagColor(entry.tag, tags)}33`,
                    color: getTagColor(entry.tag, tags),
                  }}
                >
                  {entry.tag}
                </span>
              )}

              <span className={`font-mono text-[12px] flex-1 truncate ${
                entry.isError ? "text-accent-red" : "text-[#DCDDDE]"
              }`}>
                {entry.message}
              </span>

              {entry.isMultiline && (
                <i className="fa-solid fa-chevron-down text-[10px] text-overlay-30 mt-1 shrink-0" />
              )}
            </button>
          ))}
          <div ref={bottomRef} />
        </div>

        {/* Detail panel */}
        {selectedEntry && (
          <>
            <div className="h-px bg-border-subtle shrink-0" />
            <div className="bg-overlay-8 px-3 py-2 min-h-[120px] max-h-[400px] overflow-auto shrink-0">
              <div className="flex items-center gap-2 mb-2">
                <span className="font-mono text-[11px] text-overlay-40">
                  {formatTimestamp(selectedEntry.timestamp)}
                </span>
                {selectedEntry.tag && (
                  <span
                    className="rounded px-1.5 py-px text-[10px] font-medium"
                    style={{
                      backgroundColor: `${getTagColor(selectedEntry.tag, tags)}33`,
                      color: getTagColor(selectedEntry.tag, tags),
                    }}
                  >
                    {selectedEntry.tag}
                  </span>
                )}
                <div className="flex-1" />
                <button onClick={() => setSelectedEntry(null)}
                  className="text-text-disabled hover:text-contrast p-1 rounded hover:bg-overlay-10 transition-colors"
                  title="Close detail panel">
                  <i className="fa-solid fa-xmark text-xs" />
                </button>
              </div>
              <pre className="font-mono text-[12px] text-[#DCDDDE] whitespace-pre-wrap">
                {selectedEntry.fullMessage || selectedEntry.message}
              </pre>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

function formatTimestamp(ts: string): string {
  try {
    const d = new Date(ts)
    const h = String(d.getHours()).padStart(2, "0")
    const m = String(d.getMinutes()).padStart(2, "0")
    const s = String(d.getSeconds()).padStart(2, "0")
    const ms = String(d.getMilliseconds()).padStart(3, "0")
    return `${h}:${m}:${s}.${ms}`
  } catch {
    return ts
  }
}

function getTagColor(tag: string, tags: TagInfo[]): string {
  return tags.find(t => t.tag === tag)?.color || "#6B6F77"
}
