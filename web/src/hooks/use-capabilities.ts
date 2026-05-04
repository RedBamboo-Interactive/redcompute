import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import type { CapabilityStatus, WsEvent } from "@/api/types"

interface StatusApiResponse {
  service: string
  uptime: number
  capabilities: CapabilityStatus[]
}

export function useCapabilities() {
  const [capabilities, setCapabilities] = useState<CapabilityStatus[]>([])
  const [uptime, setUptime] = useState("")

  const refresh = useCallback(async () => {
    try {
      const data = await api.get<StatusApiResponse>("/status")
      setCapabilities(data.capabilities)
      setUptime(String(Math.round(data.uptime)) + "s")
    } catch { /* offline */ }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "capability.status") {
      const update = event.data as CapabilityStatus
      setCapabilities(prev =>
        prev.map(c => c.slug === update.slug ? { ...c, ...update } : c)
      )
    }
  }, [])

  return { capabilities, uptime, refresh, handleWsEvent }
}
