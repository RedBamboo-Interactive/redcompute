import type { JobRecord, ClaudeSessionInfo } from "@/api/types"

export type TimeRange = "24h" | "7d" | "30d" | "all"

export interface StatsMetrics {
  totalJobs: number
  completedJobs: number
  failedJobs: number
  cancelledJobs: number
  successRate: number
  avgDurationMs: number | null
  totalDurationMs: number
  totalCost: number
  avgCost: number | null
  jobsWithCost: number
}

export interface TimeBucket {
  date: string
  label: string
  completed: number
  failed: number
  cancelled: number
}

export interface DurationBucket {
  date: string
  label: string
  avgMs: number
  count: number
}

export interface GroupCount {
  name: string
  count: number
}

export function getTimeRangeStart(range: TimeRange): Date | null {
  if (range === "all") return null
  const now = new Date()
  const ms = { "24h": 86400000, "7d": 604800000, "30d": 2592000000 }[range]
  return new Date(now.getTime() - ms)
}

export function filterJobsByTimeRange(jobs: JobRecord[], range: TimeRange): JobRecord[] {
  const start = getTimeRangeStart(range)
  if (!start) return jobs
  const cutoff = start.getTime()
  return jobs.filter(j => new Date(j.queuedAt).getTime() >= cutoff)
}

export function computeMetrics(jobs: JobRecord[]): StatsMetrics {
  let completed = 0, failed = 0, cancelled = 0, totalDur = 0, durCount = 0
  let totalCost = 0, costCount = 0

  for (const j of jobs) {
    if (j.costUsd != null) { totalCost += j.costUsd; costCount++ }
    if (j.status === "Completed") {
      completed++
      if (j.durationMs != null) { totalDur += j.durationMs; durCount++ }
    } else if (j.status === "Failed") {
      failed++
      if (j.durationMs != null) { totalDur += j.durationMs; durCount++ }
    } else if (j.status === "Cancelled") {
      cancelled++
    }
  }

  const decided = completed + failed
  return {
    totalJobs: jobs.length,
    completedJobs: completed,
    failedJobs: failed,
    cancelledJobs: cancelled,
    successRate: decided > 0 ? (completed / decided) * 100 : 0,
    avgDurationMs: durCount > 0 ? totalDur / durCount : null,
    totalDurationMs: totalDur,
    totalCost,
    avgCost: costCount > 0 ? totalCost / costCount : null,
    jobsWithCost: costCount,
  }
}

function createBuckets(range: TimeRange): { start: Date; step: number; count: number; labelFn: (d: Date) => string } {
  const now = new Date()

  if (range === "24h") {
    const start = new Date(now.getTime() - 86400000)
    start.setMinutes(0, 0, 0)
    return { start, step: 3600000, count: 24, labelFn: d => `${d.getHours()}:00` }
  }
  if (range === "7d") {
    const start = new Date(now)
    start.setDate(start.getDate() - 6)
    start.setHours(0, 0, 0, 0)
    const days = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"]
    return { start, step: 86400000, count: 7, labelFn: d => `${days[d.getDay()]} ${d.getDate()}` }
  }
  if (range === "30d") {
    const start = new Date(now)
    start.setDate(start.getDate() - 29)
    start.setHours(0, 0, 0, 0)
    const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
    return { start, step: 86400000, count: 30, labelFn: d => `${months[d.getMonth()]} ${d.getDate()}` }
  }

  // "all" — weekly buckets based on job range
  const months = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"]
  const weekMs = 604800000
  const start = new Date(now.getTime() - weekMs * 12)
  start.setHours(0, 0, 0, 0)
  start.setDate(start.getDate() - start.getDay())
  return { start, step: weekMs, count: 13, labelFn: d => `${months[d.getMonth()]} ${d.getDate()}` }
}

function findBucketIndex(ts: number, start: number, step: number, count: number): number {
  const idx = Math.floor((ts - start) / step)
  if (idx < 0 || idx >= count) return -1
  return idx
}

export function bucketJobs(jobs: JobRecord[], range: TimeRange): TimeBucket[] {
  const { start, step, count, labelFn } = createBuckets(range)
  const startMs = start.getTime()

  const buckets: TimeBucket[] = Array.from({ length: count }, (_, i) => {
    const d = new Date(startMs + i * step)
    return { date: d.toISOString(), label: labelFn(d), completed: 0, failed: 0, cancelled: 0 }
  })

  for (const j of jobs) {
    const ts = new Date(j.completedAt || j.queuedAt).getTime()
    const idx = findBucketIndex(ts, startMs, step, count)
    if (idx < 0) continue
    if (j.status === "Completed") buckets[idx].completed++
    else if (j.status === "Failed") buckets[idx].failed++
    else if (j.status === "Cancelled") buckets[idx].cancelled++
  }

  return buckets
}

export function durationTrend(jobs: JobRecord[], range: TimeRange): DurationBucket[] {
  const { start, step, count, labelFn } = createBuckets(range)
  const startMs = start.getTime()

  const sums: { total: number; count: number }[] = Array.from({ length: count }, () => ({ total: 0, count: 0 }))

  for (const j of jobs) {
    if (j.status !== "Completed" || j.durationMs == null) continue
    const ts = new Date(j.completedAt || j.queuedAt).getTime()
    const idx = findBucketIndex(ts, startMs, step, count)
    if (idx < 0) continue
    sums[idx].total += j.durationMs
    sums[idx].count++
  }

  return sums.map((s, i) => {
    const d = new Date(startMs + i * step)
    return {
      date: d.toISOString(),
      label: labelFn(d),
      avgMs: s.count > 0 ? s.total / s.count : 0,
      count: s.count,
    }
  })
}

export function groupByField(jobs: JobRecord[], field: "providerName" | "callerInfo"): GroupCount[] {
  const counts = new Map<string, number>()
  for (const j of jobs) {
    const val = (field === "callerInfo" ? j.callerInfo : j.providerName) || "unknown"
    counts.set(val, (counts.get(val) || 0) + 1)
  }
  return [...counts.entries()]
    .sort((a, b) => b[1] - a[1])
    .map(([name, count]) => ({ name, count }))
}

// --- Session cost/token aggregation ---

export interface SessionMetrics {
  totalCost: number
  avgCost: number | null
  totalInputTokens: number
  totalOutputTokens: number
  sessionCount: number
}

export interface CostBucket {
  date: string
  label: string
  cost: number
  count: number
}

export function filterSessionsByTimeRange(sessions: ClaudeSessionInfo[], range: TimeRange): ClaudeSessionInfo[] {
  const start = getTimeRangeStart(range)
  if (!start) return sessions
  const cutoff = start.getTime()
  return sessions.filter(s => new Date(s.startedAt).getTime() >= cutoff)
}

export function computeSessionMetrics(sessions: ClaudeSessionInfo[]): SessionMetrics {
  let totalCost = 0, totalIn = 0, totalOut = 0, costCount = 0

  for (const s of sessions) {
    if (s.costUsd != null) { totalCost += s.costUsd; costCount++ }
    if (s.inputTokens != null) totalIn += s.inputTokens
    if (s.outputTokens != null) totalOut += s.outputTokens
  }

  return {
    totalCost,
    avgCost: costCount > 0 ? totalCost / costCount : null,
    totalInputTokens: totalIn,
    totalOutputTokens: totalOut,
    sessionCount: sessions.length,
  }
}

export function costTrend(sessions: ClaudeSessionInfo[], range: TimeRange): CostBucket[] {
  const { start, step, count, labelFn } = createBuckets(range)
  const startMs = start.getTime()

  const buckets: CostBucket[] = Array.from({ length: count }, (_, i) => {
    const d = new Date(startMs + i * step)
    return { date: d.toISOString(), label: labelFn(d), cost: 0, count: 0 }
  })

  for (const s of sessions) {
    if (s.costUsd == null) continue
    const ts = new Date(s.startedAt).getTime()
    const idx = findBucketIndex(ts, startMs, step, count)
    if (idx < 0) continue
    buckets[idx].cost += s.costUsd
    buckets[idx].count++
  }

  return buckets
}

export function formatCost(usd: number): string {
  if (usd < 0.01) return `$${usd.toFixed(4)}`
  if (usd < 1) return `$${usd.toFixed(2)}`
  return `$${usd.toFixed(2)}`
}

export function formatTokens(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}k`
  return String(n)
}

export function costTrendFromJobs(jobs: JobRecord[], range: TimeRange): CostBucket[] {
  const { start, step, count, labelFn } = createBuckets(range)
  const startMs = start.getTime()

  const buckets: CostBucket[] = Array.from({ length: count }, (_, i) => {
    const d = new Date(startMs + i * step)
    return { date: d.toISOString(), label: labelFn(d), cost: 0, count: 0 }
  })

  for (const j of jobs) {
    if (j.costUsd == null) continue
    const ts = new Date(j.completedAt || j.queuedAt).getTime()
    const idx = findBucketIndex(ts, startMs, step, count)
    if (idx < 0) continue
    buckets[idx].cost += j.costUsd
    buckets[idx].count++
  }

  return buckets
}

export function formatDurationCompact(ms: number): string {
  if (ms < 1000) return `${Math.round(ms)}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  if (ms < 3600000) {
    const m = Math.floor(ms / 60000)
    const s = Math.floor((ms % 60000) / 1000)
    return s > 0 ? `${m}m ${s}s` : `${m}m`
  }
  const h = Math.floor(ms / 3600000)
  const m = Math.floor((ms % 3600000) / 60000)
  return m > 0 ? `${h}h ${m}m` : `${h}h`
}
