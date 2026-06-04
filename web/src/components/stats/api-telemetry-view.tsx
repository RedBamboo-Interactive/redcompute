import { useMemo, useState } from "react"
import type { AppTelemetry, RouteStats } from "@/hooks/use-suite-telemetry"
import type { TimeRange } from "@/lib/stats-utils"
import { RouteDetailModal } from "./route-detail-modal"

type SortKey = "count" | "totalMs" | "avgMs" | "minMs" | "p10Ms" | "p50Ms" | "p70Ms" | "p90Ms" | "p99Ms" | "maxMs" | "errorCount"
type SortDir = "asc" | "desc"

interface FlatRow extends RouteStats {
  app: string
  appColor: string
  appIcon: string
  appPort: number
}

const APP_ICONS: Record<string, string> = {
  RedCompute: "fa-solid fa-microchip",
  CodeRed: "fa-regular fa-square-terminal",
  RedMatter: "fa-solid fa-fire",
  Nova: "fa-solid fa-star",
  RedLeaf: "fa-solid fa-leaf",
}

const COLUMNS: { key: SortKey; label: string; width: string }[] = [
  { key: "count", label: "Count", width: "w-[70px]" },
  { key: "totalMs", label: "Total", width: "w-[80px]" },
  { key: "avgMs", label: "Avg", width: "w-[70px]" },
  { key: "minMs", label: "Min", width: "w-[70px]" },
  { key: "p10Ms", label: "P10", width: "w-[70px]" },
  { key: "p50Ms", label: "P50", width: "w-[70px]" },
  { key: "p70Ms", label: "P70", width: "w-[70px]" },
  { key: "p90Ms", label: "P90", width: "w-[70px]" },
  { key: "p99Ms", label: "P99", width: "w-[70px]" },
  { key: "maxMs", label: "Max", width: "w-[70px]" },
  { key: "errorCount", label: "Errors", width: "w-[70px]" },
]

function formatMs(ms: number): string {
  if (ms < 1) return "<1ms"
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60_000) return `${(ms / 1000).toFixed(1)}s`
  if (ms < 3_600_000) return `${(ms / 60_000).toFixed(1)}m`
  return `${(ms / 3_600_000).toFixed(1)}h`
}

function durationColor(ms: number): string {
  if (ms < 50) return "text-text-muted"
  if (ms < 200) return "text-accent-teal"
  if (ms < 500) return "text-accent-amber"
  return "text-accent-red"
}

export function ApiTelemetryView({ apps, loading, timeRange }: { apps: AppTelemetry[]; loading: boolean; timeRange: TimeRange }) {
  const [sortKey, setSortKey] = useState<SortKey>("p90Ms")
  const [sortDir, setSortDir] = useState<SortDir>("desc")
  const [appFilter, setAppFilter] = useState<string | null>(null)
  const [hideJobs, setHideJobs] = useState(true)
  const [selectedRow, setSelectedRow] = useState<FlatRow | null>(null)

  const onlineApps = useMemo(() => apps.filter(a => a.status === "online"), [apps])

  const rows: FlatRow[] = useMemo(() => {
    const flat: FlatRow[] = []
    for (const app of onlineApps) {
      if (appFilter && app.name !== appFilter) continue
      for (const route of app.stats?.routes ?? []) {
        if (hideJobs && route.kind === "job") continue
        flat.push({ ...route, app: app.name, appColor: app.color, appIcon: APP_ICONS[app.name] ?? "fa-solid fa-cube", appPort: app.port })
      }
    }
    const getValue = (row: FlatRow, key: SortKey): number =>
      key === "totalMs" ? row.count * row.avgMs : row[key] as number
    flat.sort((a, b) => {
      const av = getValue(a, sortKey)
      const bv = getValue(b, sortKey)
      return sortDir === "desc" ? bv - av : av - bv
    })
    return flat
  }, [onlineApps, sortKey, sortDir, appFilter, hideJobs])

  const totalRequests = useMemo(
    () => onlineApps.reduce((sum, a) => sum + (a.stats?.total_requests ?? 0), 0),
    [onlineApps],
  )

  const handleSort = (key: SortKey) => {
    if (sortKey === key) setSortDir(d => (d === "desc" ? "asc" : "desc"))
    else { setSortKey(key); setSortDir("desc") }
  }

  if (loading) {
    return (
      <div className="flex items-center justify-center h-[60vh] text-text-muted text-sm">
        <i className="fa-solid fa-spinner-third fa-spin mr-2" />Loading...
      </div>
    )
  }

  return (
    <div>
      {/* App pills */}
      <div className="flex flex-wrap items-center gap-2 mb-5">
        {apps.map(app => {
          const active = appFilter === app.name
          const offline = app.status !== "online"
          return (
            <button
              key={app.name}
              onClick={() => setAppFilter(active ? null : app.name)}
              disabled={offline}
              className={`flex items-center gap-1.5 px-3 py-1 rounded text-xs font-mono transition-colors cursor-pointer
                ${offline ? "opacity-30 cursor-not-allowed" : ""}
                ${active ? "ring-1 ring-current" : "bg-overlay-6 hover:bg-overlay-10"}`}
              style={active || !offline ? { color: app.color, backgroundColor: active ? `${app.color}20` : undefined } : undefined}
            >
              <i className={`${APP_ICONS[app.name] ?? "fa-solid fa-cube"} text-[10px]`} style={{ color: offline ? undefined : app.color }} />
              {app.name}
              {!offline && (
                <span className="text-text-disabled ml-1">
                  {app.stats?.total_requests?.toLocaleString() ?? 0}
                </span>
              )}
            </button>
          )
        })}
        <span className="text-[11px] text-text-disabled ml-2">
          {totalRequests.toLocaleString()} total requests
        </span>
        <button
          onClick={() => setHideJobs(h => !h)}
          className={`flex items-center gap-1.5 px-2.5 py-1 rounded text-[10px] font-mono transition-colors cursor-pointer ml-auto
            ${hideJobs ? "bg-overlay-6 text-text-muted hover:bg-overlay-10" : "bg-accent-purple/15 text-accent-purple ring-1 ring-accent-purple/30"}`}
        >
          <i className="fa-solid fa-clock-rotate-left text-[9px]" />
          {hideJobs ? "Show jobs" : "Showing jobs"}
        </button>
      </div>

      {/* Table */}
      {rows.length === 0 ? (
        <div className="flex flex-col items-center justify-center h-[40vh] text-text-muted">
          <i className="fa-solid fa-chart-simple text-3xl mb-3 opacity-30" />
          <p className="text-sm">No telemetry data yet</p>
        </div>
      ) : (
        <div className="overflow-x-auto">
          <table className="w-full text-xs font-mono">
            <thead>
              <tr className="text-text-disabled text-[10px] uppercase tracking-wider border-b border-overlay-6">
                <th className="text-left py-2 px-2 font-medium">App</th>
                <th className="text-left py-2 px-2 font-medium">Endpoint</th>
                {COLUMNS.map(col => (
                  <th
                    key={col.key}
                    onClick={() => handleSort(col.key)}
                    className={`text-right py-2 px-2 font-medium cursor-pointer hover:text-text-muted transition-colors ${col.width}`}
                  >
                    {col.label}
                    {sortKey === col.key && (
                      <i className={`fa-solid fa-caret-${sortDir === "desc" ? "down" : "up"} ml-1 text-[8px]`} />
                    )}
                  </th>
                ))}
              </tr>
            </thead>
            <tbody>
              {rows.map((row, i) => (
                <tr key={`${row.app}-${row.method}-${row.routePattern}-${i}`}
                  onClick={() => setSelectedRow(row)}
                  className="border-b border-overlay-4 hover:bg-overlay-4 transition-colors cursor-pointer">
                  <td className="py-1.5 px-2">
                    <span className="flex items-center gap-1.5">
                      <i className={`${row.appIcon} text-[10px] shrink-0`} style={{ color: row.appColor }} />
                      <span style={{ color: row.appColor }}>{row.app}</span>
                    </span>
                  </td>
                  <td className="py-1.5 px-2 text-text-primary max-w-[400px]">
                    <div className="flex items-center gap-2">
                      <span className={`inline-block w-[38px] text-center rounded px-1 py-0.5 text-[9px] font-bold shrink-0 ${
                        row.method === "GET" ? "bg-accent-teal/15 text-accent-teal"
                          : "bg-accent-amber/15 text-accent-amber"
                      }`}>
                        {row.method}
                      </span>
                      <div className="min-w-0">
                        <div className="truncate">
                          {row.routePattern}
                          {row.kind === "job" && (
                            <span className="ml-2 px-1.5 py-0.5 rounded text-[8px] font-bold uppercase bg-accent-purple/15 text-accent-purple">
                              async
                            </span>
                          )}
                        </div>
                        {row.description && (
                          <div className="text-[10px] text-text-disabled truncate">{row.description}</div>
                        )}
                      </div>
                    </div>
                  </td>
                  <td className="py-1.5 px-2 text-right text-text-muted">{row.count.toLocaleString()}</td>
                  <td className="py-1.5 px-2 text-right text-text-secondary">{formatMs(row.count * row.avgMs)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.avgMs)}`}>{formatMs(row.avgMs)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.minMs)}`}>{formatMs(row.minMs)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p10Ms)}`}>{formatMs(row.p10Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p50Ms)}`}>{formatMs(row.p50Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p70Ms)}`}>{formatMs(row.p70Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p90Ms)}`}>{formatMs(row.p90Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p99Ms)}`}>{formatMs(row.p99Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.maxMs)}`}>{formatMs(row.maxMs)}</td>
                  <td className="py-1.5 px-2 text-right">
                    {row.errorCount > 0 ? (
                      <span className="text-accent-red">{row.errorCount}</span>
                    ) : (
                      <span className="text-text-disabled">0</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      {selectedRow && (
        <RouteDetailModal
          route={selectedRow}
          app={selectedRow.app}
          appColor={selectedRow.appColor}
          appIcon={selectedRow.appIcon}
          appPort={selectedRow.appPort}
          timeRange={timeRange}
          onClose={() => setSelectedRow(null)}
        />
      )}
    </div>
  )
}
