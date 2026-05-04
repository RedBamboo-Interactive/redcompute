import { useEffect, useRef } from "react"
import type { LogEntry, TagInfo } from "@/api/types"

export function LogsPage({ entries, tags, search, setSearch, tagFilter, setTagFilter, selectedEntry, setSelectedEntry, autoScrollRef }: {
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
    if (autoScrollRef.current) {
      bottomRef.current?.scrollIntoView({ behavior: "smooth" })
    }
  }, [entries.length, autoScrollRef])

  return (
    <div className="flex flex-col h-[calc(100vh-3rem)] p-6">
      <h1 className="text-[20px] font-semibold text-white opacity-95 mb-2">Logs</h1>
      <div className="bg-surface-elevated rounded-lg flex-1 flex flex-col overflow-hidden">
        {/* Toolbar — matches WPF ConsoleTabContent toolbar */}
        <div className="flex items-center gap-3 px-3 py-2 border-b border-[#3F4147] flex-wrap">
          {/* Search box */}
          <div className="flex items-center gap-1.5 bg-white/[0.08] rounded px-2 py-1 max-w-[250px]">
            <i className="fa-solid fa-magnifying-glass text-[11px] text-text-disabled" />
            <input
              type="text"
              placeholder="Search logs..."
              value={search}
              onChange={e => setSearch(e.target.value)}
              className="bg-transparent border-none outline-none text-[12px] text-[#DCDDDE] placeholder-text-disabled w-full"
            />
          </div>

          {/* Tag filter chips */}
          <div className="flex gap-1 flex-wrap flex-1">
            {tags.map(t => (
              <button
                key={t.tag}
                onClick={() => setTagFilter(tagFilter === t.tag ? null : t.tag)}
                className="rounded px-1.5 py-0.5 text-[10px] cursor-pointer transition-colors"
                style={{
                  backgroundColor: tagFilter === t.tag ? `${t.color}33` : "rgba(255,255,255,0.08)",
                  color: tagFilter === t.tag ? t.color : "#ADAEB3",
                }}
              >
                {t.tag}
              </button>
            ))}
          </div>

          {/* Entry count */}
          <span className="text-[11px] text-text-disabled">
            {entries.length}/{entries.length}
          </span>

          {/* Clear button */}
          <button className="flex items-center gap-1 text-text-muted text-[12px] hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10">
            <i className="fa-solid fa-trash-can text-xs" />
            <span>Clear</span>
          </button>
        </div>

        {/* Log entries list */}
        <div className="flex-1 overflow-auto">
          {entries.map(entry => (
            <button
              key={entry.id}
              onClick={() => setSelectedEntry(selectedEntry?.id === entry.id ? null : entry)}
              className={`flex items-start gap-2 w-full text-left px-2 py-1 transition-colors ${
                selectedEntry?.id === entry.id ? "bg-white/[0.08]" : "hover:bg-white/[0.03]"
              }`}
            >
              {/* Timestamp — Consolas, 11px, muted */}
              <span className="font-mono text-[11px] text-white/40 w-[82px] shrink-0">
                {formatTimestamp(entry.timestamp)}
              </span>

              {/* Tag badge — colored background */}
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

              {/* Message — Consolas, 12px */}
              <span className={`font-mono text-[12px] flex-1 truncate ${
                entry.isError ? "text-accent-red" : "text-[#DCDDDE]"
              }`}>
                {entry.message}
              </span>

              {/* Expand chevron for multiline */}
              {entry.isMultiline && (
                <i className="fa-solid fa-chevron-down text-[10px] text-white/30 mt-1 shrink-0" />
              )}
            </button>
          ))}
          <div ref={bottomRef} />
        </div>

        {/* Detail panel — shown when entry is selected */}
        {selectedEntry && (
          <>
            <div className="h-px bg-[#3F4147]" />
            <div className="bg-white/[0.08] px-3 py-2 min-h-[120px] max-h-[400px] overflow-auto">
              <div className="flex items-center gap-2 mb-2">
                <span className="font-mono text-[11px] text-white/40">
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
                  className="text-text-disabled hover:text-white p-1 rounded hover:bg-white/10 transition-colors"
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
  return tags.find(t => t.tag === tag)?.color || "#72767D"
}
