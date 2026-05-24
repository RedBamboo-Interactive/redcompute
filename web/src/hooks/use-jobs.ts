import { useState, useEffect, useCallback, useRef } from "react"
import { api } from "@/api/client"
import type { JobRecord, WsEvent, ClaudeSessionInfo } from "@/api/types"

export interface ApiJob {
  id: string
  capability: string
  providerName: string
  status: string
  queuedAt: string
  startedAt?: string
  completedAt?: string
  durationMs?: number
  costUsd?: number
  errorMessage?: string
  callerInfo?: string
  name?: string
  rationale?: string
  sessionStatus?: string
  progress?: number
  input?: string
  outputLocation?: string
  outputSizeBytes?: number
  outputContentType?: string
  resultJson?: string
  errorDetails?: string
}

export function mapJob(j: ApiJob): JobRecord {
  return {
    id: j.id,
    capabilitySlug: j.capability,
    providerName: j.providerName,
    status: j.status as JobRecord["status"],
    queuedAt: j.queuedAt,
    startedAt: j.startedAt,
    completedAt: j.completedAt,
    durationMs: j.durationMs,
    costUsd: j.costUsd,
    errorMessage: j.errorMessage,
    callerInfo: j.callerInfo,
    name: j.name,
    rationale: j.rationale,
    progress: j.progress,
    inputJson: j.input || "{}",
    outputLocation: j.outputLocation,
    outputSizeBytes: j.outputSizeBytes,
    outputContentType: j.outputContentType,
    resultJson: j.resultJson,
    errorDetails: j.errorDetails,
    sessionStatus: j.sessionStatus as JobRecord["sessionStatus"],
  }
}

export interface JobFilters {
  search?: string
  capability?: string
  caller?: string
  status?: string
}

interface JobsResponse {
  items: ApiJob[]
  total: number
}

export function useJobs(filters: JobFilters = {}, pageSize = 50) {
  const [jobs, setJobs] = useState<JobRecord[]>([])
  const [total, setTotal] = useState(0)
  const [loading, setLoading] = useState(false)
  const [selectedJob, setSelectedJob] = useState<JobRecord | null>(null)
  const [debouncedSearch, setDebouncedSearch] = useState(filters.search || "")

  useEffect(() => {
    const timer = setTimeout(() => setDebouncedSearch(filters.search || ""), 300)
    return () => clearTimeout(timer)
  }, [filters.search])

  const filtersRef = useRef(filters)
  filtersRef.current = filters
  const debouncedSearchRef = useRef(debouncedSearch)
  debouncedSearchRef.current = debouncedSearch

  function buildQuery(offset: number): string {
    const params = new URLSearchParams()
    params.set("limit", String(pageSize))
    params.set("offset", String(offset))
    if (debouncedSearchRef.current) params.set("search", debouncedSearchRef.current)
    if (filtersRef.current.capability) params.set("capability", filtersRef.current.capability)
    if (filtersRef.current.caller) params.set("caller", filtersRef.current.caller)
    if (filtersRef.current.status) params.set("status", filtersRef.current.status)
    return `/jobs?${params.toString()}`
  }

  const refresh = useCallback(async () => {
    setLoading(true)
    try {
      const data = await api.get<JobsResponse>(buildQuery(0))
      setJobs(data.items.map(mapJob))
      setTotal(data.total)
    } catch { /* offline */ }
    setLoading(false)
  }, [debouncedSearch, filters.capability, filters.caller, filters.status, pageSize])

  useEffect(() => { refresh() }, [refresh])

  const loadMore = useCallback(async () => {
    setLoading(true)
    try {
      const data = await api.get<JobsResponse>(buildQuery(jobs.length))
      setJobs(prev => [...prev, ...data.items.map(mapJob)])
      setTotal(data.total)
    } catch { /* offline */ }
    setLoading(false)
  }, [jobs.length, debouncedSearch, filters.capability, filters.caller, filters.status, pageSize])

  const hasMore = jobs.length < total

  function jobMatchesFilters(job: JobRecord): boolean {
    const f = filtersRef.current
    if (f.capability && job.capabilitySlug !== f.capability) return false
    if (f.caller && (job.callerInfo || "") !== f.caller) return false
    if (f.status && job.status !== f.status) return false
    if (debouncedSearchRef.current) {
      const q = debouncedSearchRef.current.toLowerCase()
      const matches =
        (job.name || job.capabilitySlug).toLowerCase().includes(q) ||
        job.providerName.toLowerCase().includes(q) ||
        (job.callerInfo || "").toLowerCase().includes(q)
      if (!matches) return false
    }
    return true
  }

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "job.created") {
      const job = event.data as JobRecord
      if (jobMatchesFilters(job)) {
        setJobs(prev => [job, ...prev])
        setTotal(prev => prev + 1)
      }
    } else if (event.type === "job.updated") {
      const job = event.data as JobRecord
      setJobs(prev => {
        const exists = prev.some(j => j.id === job.id)
        if (exists) {
          if (!jobMatchesFilters(job)) {
            setTotal(t => t - 1)
            return prev.filter(j => j.id !== job.id)
          }
          return prev.map(j => j.id === job.id ? { ...job, sessionStatus: j.sessionStatus } : j)
        }
        return prev
      })
      setSelectedJob(prev => prev?.id === job.id ? { ...job, sessionStatus: prev.sessionStatus } : prev)
    } else if (event.type === "session.updated") {
      const session = event.data as ClaudeSessionInfo
      if (session.jobId) {
        const jobId = session.jobId
        setJobs(prev => prev.map(j => j.id === jobId ? { ...j, sessionStatus: session.status } : j))
        setSelectedJob(prev => prev?.id === jobId ? { ...prev, sessionStatus: session.status } : prev)
      }
    }
  }, [filters.capability, filters.caller, filters.status, debouncedSearch])

  const selectJob = useCallback(async (job: JobRecord) => {
    try {
      const detail = await api.get<ApiJob>(`/jobs/${job.id}`)
      setSelectedJob(mapJob(detail))
    } catch {
      setSelectedJob(job)
    }
  }, [])

  return { jobs, total, hasMore, loading, selectedJob, setSelectedJob: selectJob, refresh, loadMore, handleWsEvent }
}
