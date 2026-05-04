import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import type { Settings } from "@/api/types"

export function useSettings() {
  const [settings, setSettings] = useState<Settings | null>(null)
  const [saving, setSaving] = useState(false)

  const refresh = useCallback(async () => {
    try {
      const data = await api.get<Settings>("/settings")
      setSettings(data)
    } catch { /* offline */ }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  const updateGeneral = useCallback(async (updates: Partial<Pick<Settings, "apiPort" | "jobRetentionDays" | "logLevel" | "autoStartWithWindows">>) => {
    setSaving(true)
    try {
      await api.put("/settings/general", updates)
      await refresh()
    } finally {
      setSaving(false)
    }
  }, [refresh])

  const updateCapability = useCallback(async (slug: string, updates: { enabled?: boolean; activeProvider?: string }) => {
    setSaving(true)
    try {
      await api.put(`/settings/capability/${slug}`, updates)
      await refresh()
    } finally {
      setSaving(false)
    }
  }, [refresh])

  return { settings, saving, refresh, updateGeneral, updateCapability }
}
