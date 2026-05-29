import { useMemo, useState } from "react"
import type { AppTelemetry, RouteStats } from "@/hooks/use-suite-telemetry"

type SortKey = "count" | "avgMs" | "p50Ms" | "p70Ms" | "p90Ms" | "p99Ms" | "errorCount"
type SortDir = "asc" | "desc"

interface FlatRow extends RouteStats {
  app: string
  appColor: string
  appIcon: string
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
  { key: "avgMs", label: "Avg", width: "w-[70px]" },
  { key: "p50Ms", label: "P50", width: "w-[70px]" },
  { key: "p70Ms", label: "P70", width: "w-[70px]" },
  { key: "p90Ms", label: "P90", width: "w-[70px]" },
  { key: "p99Ms", label: "P99", width: "w-[70px]" },
  { key: "errorCount", label: "Errors", width: "w-[70px]" },
]

function formatMs(ms: number): string {
  if (ms < 1) return "<1ms"
  if (ms < 1000) return `${Math.round(ms)}ms`
  return `${(ms / 1000).toFixed(1)}s`
}

function durationColor(ms: number): string {
  if (ms < 50) return "text-text-muted"
  if (ms < 200) return "text-accent-teal"
  if (ms < 500) return "text-accent-amber"
  return "text-accent-red"
}

export function ApiTelemetryView({ apps, loading }: { apps: AppTelemetry[]; loading: boolean }) {
  const [sortKey, setSortKey] = useState<SortKey>("p90Ms")
  const [sortDir, setSortDir] = useState<SortDir>("desc")
  const [appFilter, setAppFilter] = useState<string | null>(null)

  const onlineApps = useMemo(() => apps.filter(a => a.status === "online"), [apps])

  const rows: FlatRow[] = useMemo(() => {
    const flat: FlatRow[] = []
    for (const app of onlineApps) {
      if (appFilter && app.name !== appFilter) continue
      for (const route of app.stats?.routes ?? []) {
        flat.push({ ...route, app: app.name, appColor: app.color, appIcon: APP_ICONS[app.name] ?? "fa-solid fa-cube" })
      }
    }
    flat.sort((a, b) => {
      const av = a[sortKey] as number
      const bv = b[sortKey] as number
      return sortDir === "desc" ? bv - av : av - bv
    })
    return flat
  }, [onlineApps, sortKey, sortDir, appFilter])

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
                  className="border-b border-overlay-4 hover:bg-overlay-4 transition-colors">
                  <td className="py-1.5 px-2">
                    <span className="flex items-center gap-1.5">
                      <i className={`${row.appIcon} text-[10px] shrink-0`} style={{ color: row.appColor }} />
                      <span style={{ color: row.appColor }}>{row.app}</span>
                    </span>
                  </td>
                  <td className="py-1.5 px-2 text-text-primary max-w-[300px] truncate">
                    <span className={`inline-block w-[38px] text-center rounded px-1 py-0.5 text-[9px] font-bold mr-2 ${
                      row.method === "GET" ? "bg-accent-teal/15 text-accent-teal"
                        : "bg-accent-amber/15 text-accent-amber"
                    }`}>
                      {row.method}
                    </span>
                    {row.routePattern}
                  </td>
                  <td className="py-1.5 px-2 text-right text-text-muted">{row.count.toLocaleString()}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.avgMs)}`}>{formatMs(row.avgMs)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p50Ms)}`}>{formatMs(row.p50Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p70Ms)}`}>{formatMs(row.p70Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p90Ms)}`}>{formatMs(row.p90Ms)}</td>
                  <td className={`py-1.5 px-2 text-right ${durationColor(row.p99Ms)}`}>{formatMs(row.p99Ms)}</td>
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
    </div>
  )
}
