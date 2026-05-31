import { useMemo, useState } from "react"
import { useSearchParams } from "react-router-dom"
import { Select, SelectTrigger, SelectValue, SelectContent, SelectItem, FilterPillGroup } from "@redbamboo/ui"
import { useStats } from "@/hooks/use-stats"
import { useSuiteTelemetry } from "@/hooks/use-suite-telemetry"
import { MetricCard } from "@/components/stats/metric-card"
import { ApiTelemetryView } from "@/components/stats/api-telemetry-view"
import { JobsOverTimeChart, StatusDonut, DurationChart, SourceBarChart, CostChart, CostBySourcePie } from "@/components/stats/charts"
import {
  formatDurationCompact, formatCost, formatTokens,
  computeMetrics, bucketJobs, durationTrend, groupByField, groupCostBySource,
  type TimeRange, type GroupCount,
} from "@/lib/stats-utils"
import { useAppState } from "@/contexts/app-state"

type StatsView = "jobs" | "telemetry"

const TIME_RANGES: { value: TimeRange; label: string }[] = [
  { value: "24h", label: "24h" },
  { value: "7d", label: "7d" },
  { value: "30d", label: "30d" },
  { value: "all", label: "All" },
]

const VIEW_TABS: { value: StatsView; label: string; icon: string }[] = [
  { value: "jobs", label: "Jobs", icon: "fa-solid fa-briefcase" },
  { value: "telemetry", label: "API Telemetry", icon: "fa-solid fa-gauge-high" },
]

export function StatsPage() {
  const { caps } = useAppState()
  const capabilities = caps.capabilities
  const [searchParams, setSearchParams] = useSearchParams()

  const view = (searchParams.get("view") as StatsView) || "jobs"
  const capability = searchParams.get("capability") || null
  const timeRange = (searchParams.get("range") as TimeRange) || "7d"

  const setView = (v: StatsView) => {
    setSearchParams(prev => {
      const next = new URLSearchParams(prev)
      next.set("view", v)
      return next
    }, { replace: true })
  }

  const setCapability = (slug: string) => {
    setSearchParams(prev => {
      const next = new URLSearchParams(prev)
      next.set("capability", slug)
      return next
    }, { replace: true })
  }

  const setTimeRange = (range: TimeRange) => {
    setSearchParams(prev => {
      const next = new URLSearchParams(prev)
      next.set("range", range)
      return next
    }, { replace: true })
  }

  const stats = useStats(capability, timeRange)
  const telemetry = useSuiteTelemetry(timeRange)
  const [providerFilter, setProviderFilter] = useState<string | null>(null)
  const [callerFilter, setCallerFilter] = useState<string | null>(null)

  const secondaryFiltered = useMemo(() => {
    if (!providerFilter && !callerFilter) return null
    let jobs = stats.filteredJobs
    if (providerFilter) jobs = jobs.filter(j => j.providerName === providerFilter)
    if (callerFilter) jobs = jobs.filter(j => (j.callerInfo || "unknown") === callerFilter)
    return jobs
  }, [stats.filteredJobs, providerFilter, callerFilter])

  const activeJobs = secondaryFiltered ?? stats.filteredJobs

  const metrics = useMemo(
    () => secondaryFiltered ? computeMetrics(secondaryFiltered) : stats.metrics,
    [secondaryFiltered, stats.metrics],
  )

  const timeBuckets = useMemo(
    () => secondaryFiltered ? bucketJobs(secondaryFiltered, timeRange) : stats.timeBuckets,
    [secondaryFiltered, stats.timeBuckets, timeRange],
  )

  const durationBuckets = useMemo(
    () => secondaryFiltered ? durationTrend(secondaryFiltered, timeRange) : stats.durationBuckets,
    [secondaryFiltered, stats.durationBuckets, timeRange],
  )

  const callers: GroupCount[] = useMemo(
    () => secondaryFiltered ? groupByField(secondaryFiltered, "callerInfo") : stats.callers,
    [secondaryFiltered, stats.callers],
  )

  const costBySource = useMemo(
    () => secondaryFiltered ? groupCostBySource(secondaryFiltered) : stats.costBySource,
    [secondaryFiltered, stats.costBySource],
  )

  const statusCounts: GroupCount[] = useMemo(() => {
    const order = ["Completed", "Failed", "Cancelled", "Queued", "Running"]
    const counts = new Map<string, number>()
    for (const j of activeJobs) counts.set(j.status, (counts.get(j.status) || 0) + 1)
    return order.filter(s => counts.has(s)).map(s => ({ name: s, count: counts.get(s)! }))
  }, [activeJobs])

  const truncated = stats.totalOnServer > stats.allJobs.length

  return (
    <div className="flex flex-col h-full">
      <div className="flex-1 p-4 md:p-6 overflow-auto">

        {/* View toggle + time range */}
        <div className="flex flex-wrap items-center gap-3 mb-6">
          <div className="flex gap-0.5 bg-overlay-4 rounded-lg p-0.5">
            {VIEW_TABS.map(tab => (
              <button
                key={tab.value}
                onClick={() => setView(tab.value)}
                className={`flex items-center gap-1.5 px-3 py-1.5 rounded-md text-xs font-medium transition-colors cursor-pointer ${
                  view === tab.value
                    ? "bg-surface-elevated text-text-primary shadow-sm"
                    : "text-text-muted hover:text-text-primary"
                }`}
              >
                <i className={`${tab.icon} text-[10px]`} />
                {tab.label}
              </button>
            ))}
          </div>

          <div className="flex gap-1">
            {TIME_RANGES.map(r => (
              <button
                key={r.value}
                onClick={() => setTimeRange(r.value)}
                className={`px-3 py-1 rounded text-xs font-mono transition-colors cursor-pointer ${
                  timeRange === r.value
                    ? "bg-accent-teal-a20 text-accent-teal"
                    : "bg-overlay-6 text-text-muted hover:bg-overlay-10"
                }`}
              >
                {r.label}
              </button>
            ))}
          </div>

          {/* Job-specific filters */}
          {view === "jobs" && (
            <>
              <Select value={capability ?? undefined} onValueChange={v => setCapability(v as string)}>
                <SelectTrigger size="sm" className="min-w-[180px]">
                  <SelectValue placeholder="Select capability..." />
                </SelectTrigger>
                <SelectContent>
                  {capabilities.map(cap => (
                    <SelectItem key={cap.slug} value={cap.slug}>
                      <span className="flex items-center gap-2">
                        <i className={cap.icon || "fa-solid fa-cube"} style={{ color: cap.color, fontSize: 11 }} />
                        {cap.displayName}
                      </span>
                    </SelectItem>
                  ))}
                </SelectContent>
              </Select>

              <FilterPillGroup
                label="Provider"
                options={stats.providers.map(p => ({ value: p.name, count: p.count }))}
                value={providerFilter}
                onChange={setProviderFilter}
                activeColor="rgba(124,77,255,0.2)"
                activeTextColor="#7C4DFF"
              />
              <FilterPillGroup
                label="Source"
                options={stats.callers.map(c => ({ value: c.name, count: c.count }))}
                value={callerFilter}
                onChange={setCallerFilter}
                activeColor="rgba(212,170,79,0.2)"
                activeTextColor="#D4AA4F"
              />
            </>
          )}
        </div>

        {/* Telemetry view */}
        {view === "telemetry" && (
          <ApiTelemetryView apps={telemetry.apps} loading={telemetry.loading} timeRange={timeRange} />
        )}

        {/* Jobs view */}
        {view === "jobs" && (
          <>
            {/* Empty state: no capability selected */}
            {!capability && (
              <div className="flex flex-col items-center justify-center h-[60vh] text-text-muted">
                <i className="fa-solid fa-chart-simple text-3xl mb-3 opacity-30" />
                <p className="text-sm">Select a capability to see its stats</p>
              </div>
            )}

            {/* Loading state */}
            {capability && stats.loading && (
              <div className="flex items-center justify-center h-[60vh] text-text-muted text-sm">
                <i className="fa-solid fa-spinner-third fa-spin mr-2" />Loading...
              </div>
            )}

            {/* Stats content */}
            {capability && !stats.loading && (
              <>
                {truncated && (
                  <div className="text-[11px] text-text-disabled mb-4 px-1">
                    Showing stats for {stats.allJobs.length.toLocaleString()} of {stats.totalOnServer.toLocaleString()} total jobs
                  </div>
                )}

                {/* Metric cards */}
                <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-4 gap-4 mb-6">
                  <MetricCard
                    label="Total Jobs"
                    value={metrics.totalJobs.toLocaleString()}
                    icon="fa-solid fa-hashtag"
                  />
                  <MetricCard
                    label="Success Rate"
                    value={metrics.completedJobs + metrics.failedJobs > 0 ? `${metrics.successRate.toFixed(1)}%` : "—"}
                    icon="fa-solid fa-circle-check"
                    color={metrics.successRate >= 90 ? "#26A69A" : metrics.successRate >= 70 ? "#D4AA4F" : "#E55B5B"}
                  />
                  <MetricCard
                    label="Avg Duration"
                    value={metrics.avgDurationMs != null ? formatDurationCompact(metrics.avgDurationMs) : "—"}
                    icon="fa-solid fa-clock"
                  />
                  <MetricCard
                    label="Total Compute"
                    value={metrics.totalDurationMs > 0 ? formatDurationCompact(metrics.totalDurationMs) : "—"}
                    icon="fa-solid fa-sigma"
                  />
                </div>

                {/* Cost metrics */}
                <div className={`grid grid-cols-1 sm:grid-cols-2 gap-4 mb-6 ${stats.hasSessionData ? "lg:grid-cols-4" : "lg:grid-cols-2"}`}>
                  <MetricCard
                    label="Total Cost"
                    value={metrics.totalCost > 0 ? formatCost(metrics.totalCost) : "—"}
                    icon="fa-solid fa-dollar-sign"
                    color="#D4AA4F"
                  />
                  <MetricCard
                    label="Avg Cost / Job"
                    value={metrics.avgCost != null ? formatCost(metrics.avgCost) : "—"}
                    icon="fa-solid fa-receipt"
                    color="#D4AA4F"
                  />
                  {stats.hasSessionData && (
                    <>
                      <MetricCard
                        label="Input Tokens"
                        value={stats.sessionMetrics.totalInputTokens > 0 ? formatTokens(stats.sessionMetrics.totalInputTokens) : "—"}
                        icon="fa-solid fa-arrow-down"
                      />
                      <MetricCard
                        label="Output Tokens"
                        value={stats.sessionMetrics.totalOutputTokens > 0 ? formatTokens(stats.sessionMetrics.totalOutputTokens) : "—"}
                        icon="fa-solid fa-arrow-up"
                      />
                    </>
                  )}
                </div>

                {/* Charts */}
                <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4 mb-4">
                  <JobsOverTimeChart data={timeBuckets} />
                  <StatusDonut data={statusCounts} total={metrics.totalJobs} />
                </div>
                <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4 mb-4">
                  <DurationChart data={durationBuckets} />
                  <SourceBarChart data={callers} label="Top Sources" />
                </div>
                <div className="grid grid-cols-1 lg:grid-cols-[2fr_1fr] gap-4">
                  <CostChart data={stats.costBuckets} />
                  <CostBySourcePie data={costBySource} totalCost={metrics.totalCost} />
                </div>
              </>
            )}
          </>
        )}
      </div>
    </div>
  )
}
