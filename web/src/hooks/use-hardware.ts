import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import type { HardwareSnapshot, WsEvent } from "@/api/types"

export function useHardware() {
  const [hardware, setHardware] = useState<HardwareSnapshot | null>(null)

  const refresh = useCallback(async () => {
    try {
      const data = await api.get<HardwareSnapshot>("/hardware")
      setHardware(data)
    } catch { /* offline */ }
  }, [])

  useEffect(() => { refresh() }, [refresh])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "hardware.snapshot") {
      setHardware({ ...(event.data as HardwareSnapshot), available: true })
    }
  }, [])

  return { hardware, refresh, handleWsEvent }
}
