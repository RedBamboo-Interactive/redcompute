import { useState, useEffect, useCallback, useRef } from "react"
import { api } from "@/api/client"
import type { LogEntry, TagInfo, WsEvent } from "@/api/types"

export function useLogs() {
  const [entries, setEntries] = useState<LogEntry[]>([])
  const [totalCount, setTotalCount] = useState(0)
  const [tags, setTags] = useState<TagInfo[]>([])
  const [search, setSearch] = useState("")
  const [tagFilter, setTagFilter] = useState<string | null>(null)
  const [selectedEntry, setSelectedEntry] = useState<LogEntry | null>(null)
  const autoScrollRef = useRef(true)

  const refresh = useCallback(async () => {
    try {
      const params = new URLSearchParams({ limit: "200" })
      if (search) params.set("search", search)
      if (tagFilter) params.set("tag", tagFilter)
      const data = await api.get<{ entries: LogEntry[]; totalCount: number }>(`/logs?${params}`)
      setEntries(data.entries.reverse())
      setTotalCount(data.totalCount)
    } catch { /* offline */ }
  }, [search, tagFilter])

  const refreshTags = useCallback(async () => {
    try {
      const data = await api.get<{ tags: TagInfo[] }>("/logs/tags")
      setTags(data.tags)
    } catch { /* offline */ }
  }, [])

  useEffect(() => { refresh() }, [refresh])
  useEffect(() => { refreshTags() }, [refreshTags])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "log.entry") {
      const entry = event.data as LogEntry
      if (tagFilter && entry.tag !== tagFilter && entry.tagCategory !== tagFilter) return
      if (search && !entry.message.toLowerCase().includes(search.toLowerCase())) return
      setEntries(prev => [...prev, entry].slice(-500))
      setTotalCount(prev => prev + 1)
    }
  }, [tagFilter, search])

  return {
    entries, totalCount, tags, search, setSearch,
    tagFilter, setTagFilter, selectedEntry, setSelectedEntry,
    autoScrollRef, refresh, refreshTags, handleWsEvent,
  }
}
