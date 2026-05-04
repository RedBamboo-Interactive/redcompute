import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import type { Settings, WsEvent, TunnelSettings } from "@/api/types"

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

  const updateGeneral = useCallback(async (updates: Record<string, unknown>) => {
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

  const updateProvider = useCallback(async (slug: string, providerName: string, updates: Record<string, unknown>) => {
    setSaving(true)
    try {
      await api.put(`/settings/capability/${slug}/provider/${providerName}`, updates)
      await refresh()
    } finally {
      setSaving(false)
    }
  }, [refresh])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "tunnel.status") {
      const update = event.data as { status: TunnelSettings["status"]; hostname: string | null; error: string | null }
      setSettings(prev => prev ? {
        ...prev,
        tunnel: { ...prev.tunnel, status: update.status, error: update.error, hostname: update.hostname ?? prev.tunnel.hostname }
      } : prev)
    }
  }, [])

  return { settings, saving, refresh, updateGeneral, updateCapability, updateProvider, handleWsEvent }
}
