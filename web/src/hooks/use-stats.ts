import { useState, useEffect, useCallback, useMemo } from "react"
import { api } from "@/api/client"
import { useWsSubscribe } from "@redbamboo/utility"
import { mapJob, type ApiJob } from "./use-jobs"
import type { JobRecord, ClaudeSessionInfo } from "@/api/types"
import {
  type TimeRange, type StatsMetrics, type SessionMetrics,
  filterJobsByTimeRange, computeMetrics, bucketJobs, durationTrend, groupByField,
  filterSessionsByTimeRange, computeSessionMetrics, costTrend,
} from "@/lib/stats-utils"

const FETCH_LIMIT = 5000

const emptyMetrics: StatsMetrics = {
  totalJobs: 0, completedJobs: 0, failedJobs: 0, cancelledJobs: 0,
  successRate: 0, avgDurationMs: null, totalDurationMs: 0,
}

const emptySessionMetrics: SessionMetrics = {
  totalCost: 0, avgCost: null, totalInputTokens: 0, totalOutputTokens: 0, sessionCount: 0,
}

export function useStats(capability: string | null, timeRange: TimeRange) {
  const [allJobs, setAllJobs] = useState<JobRecord[]>([])
  const [totalOnServer, setTotalOnServer] = useState(0)
  const [loading, setLoading] = useState(false)
  const [allSessions, setAllSessions] = useState<ClaudeSessionInfo[]>([])

  useEffect(() => {
    if (!capability) { setAllJobs([]); setTotalOnServer(0); setAllSessions([]); return }
    setLoading(true)

    const jobsPromise = api.get<{ items: ApiJob[]; total: number }>(`/jobs?capability=${capability}&limit=${FETCH_LIMIT}`)
      .then(data => {
        setAllJobs(data.items.map(mapJob))
        setTotalOnServer(data.total)
      })

    const sessionPromise = capability === "ai-session"
      ? api.get<ClaudeSessionInfo[]>(`/claude/sessions?limit=${FETCH_LIMIT}&all=true`)
          .then(sessions => setAllSessions(sessions))
          .catch(() => setAllSessions([]))
      : Promise.resolve(setAllSessions([]))

    Promise.all([jobsPromise, sessionPromise])
      .catch(() => {})
      .finally(() => setLoading(false))
  }, [capability])

  useWsSubscribe(useCallback((event) => {
    if (!capability) return
    if (event.type === "job.created") {
      const job = event.data as JobRecord
      if (job.capabilitySlug === capability) {
        setAllJobs(prev => [job, ...prev])
        setTotalOnServer(prev => prev + 1)
      }
    } else if (event.type === "job.updated") {
      const job = event.data as JobRecord
      if (job.capabilitySlug === capability) {
        setAllJobs(prev => prev.map(j => j.id === job.id ? job : j))
      }
    } else if (event.type === "claude.session.updated" && capability === "ai-session") {
      const session = event.data as ClaudeSessionInfo
      setAllSessions(prev => {
        const exists = prev.some(s => s.id === session.id)
        return exists ? prev.map(s => s.id === session.id ? session : s) : [session, ...prev]
      })
    }
  }, [capability]))

  const filteredJobs = useMemo(() => filterJobsByTimeRange(allJobs, timeRange), [allJobs, timeRange])
  const metrics = useMemo(() => filteredJobs.length > 0 ? computeMetrics(filteredJobs) : emptyMetrics, [filteredJobs])
  const timeBuckets = useMemo(() => bucketJobs(filteredJobs, timeRange), [filteredJobs, timeRange])
  const durationBuckets = useMemo(() => durationTrend(filteredJobs, timeRange), [filteredJobs, timeRange])
  const providers = useMemo(() => groupByField(filteredJobs, "providerName"), [filteredJobs])
  const callers = useMemo(() => groupByField(filteredJobs, "callerInfo"), [filteredJobs])

  const filteredSessions = useMemo(() => filterSessionsByTimeRange(allSessions, timeRange), [allSessions, timeRange])
  const sessionMetrics = useMemo(
    () => filteredSessions.length > 0 ? computeSessionMetrics(filteredSessions) : emptySessionMetrics,
    [filteredSessions],
  )
  const costBuckets = useMemo(() => costTrend(filteredSessions, timeRange), [filteredSessions, timeRange])

  const hasSessionData = allSessions.length > 0

  return {
    loading, allJobs, filteredJobs, metrics, timeBuckets, durationBuckets, providers, callers, totalOnServer,
    hasSessionData, filteredSessions, sessionMetrics, costBuckets,
  }
}
