import { useEffect, useMemo, useState } from "react"
import { ModalBase, ModalHeader, ModalSection } from "@redbamboo/ui"
import {
  ResponsiveContainer, ScatterChart, Scatter, XAxis, YAxis, CartesianGrid,
  Tooltip as RechartsTooltip,
} from "recharts"
import { api } from "@/api/client"
import type { RouteStats } from "@/hooks/use-suite-telemetry"
import type { TimeRange } from "@/lib/stats-utils"
import { formatDurationCompact } from "@/lib/stats-utils"

interface TelemetryEntry {
  id: number
  timestamp: string
  method: string
  path: string
  route_pattern: string | null
  status_code: number
  duration_ms: number
  response_size: number | null
  error: string | null
}

interface EntriesResponse {
  entries: TelemetryEntry[]
  count: number
}

export interface RouteDetailModalProps {
  route: RouteStats
  app: string
  appColor: string
  appIcon: string
  appPort: number
  timeRange: TimeRange
  onClose: () => void
}

interface ScatterPoint {
  time: number
  durationMs: number
  isError: boolean
}

const TEAL = "#26A69A"
const RED = "#E55B5B"
const AMBER = "#D4AA4F"
const GRID_COLOR = "rgba(255,255,255,0.06)"
const AXIS_COLOR = "#9B9CA2"

function timeRangeToSince(range: TimeRange): string | null {
  if (range === "all") return null
  const hours = range === "24h" ? 24 : range === "7d" ? 168 : 720
  return new Date(Date.now() - hours * 3600_000).toISOString()
}

function timeAgo(iso: string): string {
  const diff = Date.now() - new Date(iso).getTime()
  const mins = Math.floor(diff / 60_000)
  if (mins < 1) return "just now"
  if (mins < 60) return `${mins}m ago`
  const hours = Math.floor(mins / 60)
  if (hours < 24) return `${hours}h ago`
  return `${Math.floor(hours / 24)}d ago`
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

function statusColor(code: number): string {
  if (code < 300) return "text-accent-teal"
  if (code < 400) return "text-accent-amber"
  return "text-accent-red"
}

function statusBg(code: number): string {
  if (code < 300) return "bg-accent-teal/15 text-accent-teal"
  if (code < 400) return "bg-accent-amber/15 text-accent-amber"
  return "bg-accent-red/15 text-accent-red"
}

function durationColor(ms: number): string {
  if (ms < 50) return "text-text-muted"
  if (ms < 200) return "text-accent-teal"
  if (ms < 500) return "text-accent-amber"
  return "text-accent-red"
}

function ScatterTooltipContent({ active, payload }: { active?: boolean; payload?: Array<{ payload: ScatterPoint }> }) {
  if (!active || !payload?.length) return null
  const p = payload[0].payload
  return (
    <div className="bg-[#2E3038] border border-[#43454F] rounded-lg px-3 py-2 text-xs shadow-lg">
      <div className="text-text-muted">{new Date(p.time).toLocaleString()}</div>
      <div className="font-mono text-contrast mt-1">{formatDurationCompact(p.durationMs)}</div>
      {p.isError && <div className="text-accent-red mt-0.5">Error</div>}
    </div>
  )
}

function MiniMetric({ label, value, color }: { label: string; value: string; color?: string }) {
  return (
    <div className="bg-overlay-4 rounded-lg px-3 py-2.5 min-w-0">
      <div className="text-[18px] font-semibold font-mono leading-none truncate" style={color ? { color } : undefined}>
        {value}
      </div>
      <div className="text-[10px] text-text-disabled mt-1">{label}</div>
    </div>
  )
}

export function RouteDetailModal({ route, app, appColor, appIcon, appPort, timeRange, onClose }: RouteDetailModalProps) {
  const [entries, setEntries] = useState<TelemetryEntry[]>([])
  const [loadingEntries, setLoadingEntries] = useState(true)

  useEffect(() => {
    let cancelled = false
    setLoadingEntries(true)

    const since = timeRangeToSince(timeRange)
    const params = new URLSearchParams({
      port: String(appPort),
      route: route.routePattern,
      method: route.method,
      limit: "500",
    })
    if (since) params.set("since", since)

    api.get<EntriesResponse>(`/api/telemetry/suite/entries?${params}`)
      .then(data => { if (!cancelled) setEntries(data.entries ?? []) })
      .catch(() => { if (!cancelled) setEntries([]) })
      .finally(() => { if (!cancelled) setLoadingEntries(false) })

    return () => { cancelled = true }
  }, [appPort, route.routePattern, route.method, timeRange])

  const scatterData: ScatterPoint[] = useMemo(
    () => entries.map(e => ({
      time: new Date(e.timestamp).getTime(),
      durationMs: e.duration_ms,
      isError: e.status_code >= 400,
    })).sort((a, b) => a.time - b.time),
    [entries],
  )

  const recentEntries = useMemo(
    () => [...entries].sort((a, b) => new Date(b.timestamp).getTime() - new Date(a.timestamp).getTime()).slice(0, 20),
    [entries],
  )

  const statusCodes = useMemo(() => {
    const counts = new Map<number, number>()
    for (const e of entries) counts.set(e.status_code, (counts.get(e.status_code) ?? 0) + 1)
    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([code, count]) => ({ code, count }))
  }, [entries])

  const errorRate = route.count > 0 ? (route.errorCount / route.count * 100) : 0
  const totalMs = route.count * route.avgMs

  const percentiles = [
    { label: "Min", value: route.minMs },
    { label: "P10", value: route.p10Ms },
    { label: "P50", value: route.p50Ms },
    { label: "P70", value: route.p70Ms },
    { label: "P90", value: route.p90Ms },
    { label: "P99", value: route.p99Ms },
    { label: "Max", value: route.maxMs },
  ]

  const maxVal = route.maxMs || 1
  const segments = [
    { from: route.minMs, to: route.p10Ms, color: `${TEAL}40` },
    { from: route.p10Ms, to: route.p50Ms, color: `${TEAL}60` },
    { from: route.p50Ms, to: route.p70Ms, color: `${TEAL}90` },
    { from: route.p70Ms, to: route.p90Ms, color: AMBER },
    { from: route.p90Ms, to: route.p99Ms, color: `${RED}90` },
    { from: route.p99Ms, to: route.maxMs, color: RED },
  ]

  const methodBadgeCls = route.method === "GET"
    ? "bg-accent-teal/15 text-accent-teal"
    : route.method === "DELETE"
      ? "bg-accent-red/15 text-accent-red"
      : "bg-accent-amber/15 text-accent-amber"

  return (
    <ModalBase dataModal="route-detail" ariaLabel={`${route.method} ${route.routePattern}`} onClose={onClose} size="xl">
      <ModalHeader
        icon={<i className={`${appIcon} text-base`} style={{ color: appColor }} />}
        title={
          <div className="flex items-center gap-2">
            <span style={{ color: appColor }}>{app}</span>
            <span className={`inline-block px-1.5 py-0.5 rounded text-[10px] font-bold ${methodBadgeCls}`}>
              {route.method}
            </span>
            <span className="text-sm font-mono text-text-primary">{route.routePattern}</span>
          </div>
        }
        subtitle={route.description ?? undefined}
        onClose={onClose}
      />

      <div className="px-5 pb-5 space-y-5">
        {/* Summary metrics */}
        <ModalSection section="metrics">
          <div className="grid grid-cols-2 sm:grid-cols-5 gap-2">
            <MiniMetric label="Requests" value={route.count.toLocaleString()} />
            <MiniMetric label="Total Time" value={formatDurationCompact(totalMs)} />
            <MiniMetric label="Avg Duration" value={formatDurationCompact(route.avgMs)} color={route.avgMs >= 500 ? RED : route.avgMs >= 200 ? AMBER : undefined} />
            <MiniMetric label="Error Rate" value={errorRate > 0 ? `${errorRate.toFixed(1)}%` : "0%"} color={errorRate > 5 ? RED : errorRate > 0 ? AMBER : TEAL} />
            <MiniMetric label="Avg Size" value={route.avgResponseSize ? formatBytes(route.avgResponseSize) : "—"} />
          </div>
        </ModalSection>

        {/* Percentile distribution */}
        <ModalSection section="percentiles" heading="Latency Distribution">
          <div className="bg-overlay-4 rounded-lg p-4">
            {/* Visual bar */}
            <div className="flex h-5 rounded overflow-hidden mb-3">
              {segments.map((seg, i) => {
                const width = ((seg.to - seg.from) / maxVal) * 100
                return (
                  <div
                    key={i}
                    style={{ width: `${Math.max(width, 1)}%`, backgroundColor: seg.color }}
                    className="transition-all"
                  />
                )
              })}
            </div>
            {/* Labels */}
            <div className="flex justify-between">
              {percentiles.map(p => (
                <div key={p.label} className="text-center">
                  <div className="text-[10px] text-text-disabled">{p.label}</div>
                  <div className={`text-xs font-mono ${durationColor(p.value)}`}>{formatDurationCompact(p.value)}</div>
                </div>
              ))}
            </div>
          </div>
        </ModalSection>

        {/* Response time scatter */}
        <ModalSection section="timeline" heading="Response Times">
          {loadingEntries ? (
            <div className="flex items-center justify-center h-[180px] text-text-muted text-xs">
              <i className="fa-solid fa-spinner-third fa-spin mr-2" />Loading...
            </div>
          ) : scatterData.length === 0 ? (
            <div className="flex items-center justify-center h-[180px] text-text-disabled text-xs">
              No individual request data available
            </div>
          ) : (
            <ResponsiveContainer width="100%" height={180}>
              <ScatterChart margin={{ top: 8, right: 8, bottom: 0, left: 0 }}>
                <CartesianGrid stroke={GRID_COLOR} strokeDasharray="3 3" />
                <XAxis
                  dataKey="time"
                  type="number"
                  domain={["dataMin", "dataMax"]}
                  tickFormatter={v => {
                    const d = new Date(v)
                    return timeRange === "24h"
                      ? d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })
                      : d.toLocaleDateString([], { month: "short", day: "numeric" })
                  }}
                  tick={{ fill: AXIS_COLOR, fontSize: 10 }}
                  tickLine={false}
                  axisLine={false}
                />
                <YAxis
                  dataKey="durationMs"
                  type="number"
                  tickFormatter={v => formatDurationCompact(v)}
                  tick={{ fill: AXIS_COLOR, fontSize: 10 }}
                  tickLine={false}
                  axisLine={false}
                  width={50}
                />
                <RechartsTooltip content={<ScatterTooltipContent />} />
                <Scatter data={scatterData.filter(p => !p.isError)} fill={TEAL} fillOpacity={0.6} r={3} />
                <Scatter data={scatterData.filter(p => p.isError)} fill={RED} fillOpacity={0.8} r={4} />
              </ScatterChart>
            </ResponsiveContainer>
          )}
        </ModalSection>

        {/* Status codes + Recent requests side by side */}
        <div className="grid grid-cols-1 sm:grid-cols-[auto_1fr] gap-4">
          {/* Status codes */}
          {statusCodes.length > 0 && (
            <ModalSection section="status-codes" heading="Status Codes">
              <div className="flex flex-col gap-1.5 min-w-[140px]">
                {statusCodes.map(({ code, count }) => {
                  const pct = entries.length > 0 ? count / entries.length : 0
                  return (
                    <div key={code} className="flex items-center gap-2 text-xs">
                      <span className={`px-1.5 py-0.5 rounded text-[10px] font-bold font-mono ${statusBg(code)}`}>
                        {code}
                      </span>
                      <div className="flex-1 h-1.5 bg-overlay-6 rounded overflow-hidden min-w-[60px]">
                        <div
                          className="h-full rounded transition-all"
                          style={{
                            width: `${Math.max(pct * 100, 2)}%`,
                            backgroundColor: code < 300 ? TEAL : code < 400 ? AMBER : RED,
                          }}
                        />
                      </div>
                      <span className="font-mono text-text-muted w-8 text-right">{count}</span>
                    </div>
                  )
                })}
              </div>
            </ModalSection>
          )}

          {/* Recent requests */}
          <ModalSection section="recent" heading="Recent Requests">
            {loadingEntries ? (
              <div className="text-xs text-text-muted">Loading...</div>
            ) : recentEntries.length === 0 ? (
              <div className="text-xs text-text-disabled">No requests</div>
            ) : (
              <div className="overflow-x-auto max-h-[240px] overflow-y-auto">
                <table className="w-full text-[11px] font-mono">
                  <thead>
                    <tr className="text-text-disabled text-[9px] uppercase tracking-wider">
                      <th className="text-left py-1 px-1.5 font-medium">When</th>
                      <th className="text-left py-1 px-1.5 font-medium">Path</th>
                      <th className="text-right py-1 px-1.5 font-medium">Status</th>
                      <th className="text-right py-1 px-1.5 font-medium">Duration</th>
                      <th className="text-right py-1 px-1.5 font-medium">Size</th>
                    </tr>
                  </thead>
                  <tbody>
                    {recentEntries.map(e => (
                      <tr key={e.id} className="border-t border-overlay-4 hover:bg-overlay-4">
                        <td className="py-1 px-1.5 text-text-disabled whitespace-nowrap">{timeAgo(e.timestamp)}</td>
                        <td className="py-1 px-1.5 text-text-primary max-w-[200px] truncate">{e.path}</td>
                        <td className={`py-1 px-1.5 text-right ${statusColor(e.status_code)}`}>{e.status_code}</td>
                        <td className={`py-1 px-1.5 text-right ${durationColor(e.duration_ms)}`}>{formatDurationCompact(e.duration_ms)}</td>
                        <td className="py-1 px-1.5 text-right text-text-disabled">{e.response_size != null ? formatBytes(e.response_size) : "—"}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            )}
          </ModalSection>
        </div>

        {/* Last seen */}
        {route.lastSeen && (
          <div className="text-[10px] text-text-disabled text-right">
            Last request: {timeAgo(route.lastSeen)}
          </div>
        )}
      </div>
    </ModalBase>
  )
}
