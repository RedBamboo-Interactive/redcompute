import { useCallback, useEffect, useState } from "react"
import { api } from "@/api/client"
import type { TimeRange } from "@/lib/stats-utils"

export interface RouteStats {
  method: string
  routePattern: string
  count: number
  errorCount: number
  avgMs: number
  minMs: number
  maxMs: number
  p50Ms: number
  p70Ms: number
  p90Ms: number
  p99Ms: number
  avgResponseSize: number | null
  lastSeen: string | null
  kind: string | null
  description: string | null
}

export interface AppTelemetry {
  name: string
  port: number
  color: string
  status: "online" | "offline" | "error"
  stats: {
    routes: RouteStats[]
    total_requests: number
    since: string | null
  } | null
}

interface SuiteResponse {
  apps: AppTelemetry[]
}

function timeRangeToSince(range: TimeRange): string | null {
  if (range === "all") return null
  const now = new Date()
  const hours = range === "24h" ? 24 : range === "7d" ? 168 : 720
  return new Date(now.getTime() - hours * 3600_000).toISOString()
}

export function useSuiteTelemetry(timeRange: TimeRange) {
  const [apps, setApps] = useState<AppTelemetry[]>([])
  const [loading, setLoading] = useState(true)

  const refresh = useCallback(async () => {
    try {
      const since = timeRangeToSince(timeRange)
      const q = since ? `?since=${encodeURIComponent(since)}` : ""
      const data = await api.get<SuiteResponse>(`/api/telemetry/suite${q}`)
      setApps(data.apps)
    } catch {
      setApps([])
    } finally {
      setLoading(false)
    }
  }, [timeRange])

  useEffect(() => {
    setLoading(true)
    refresh()
    const interval = setInterval(refresh, 30_000)
    return () => clearInterval(interval)
  }, [refresh])

  return { apps, loading, refresh }
}
