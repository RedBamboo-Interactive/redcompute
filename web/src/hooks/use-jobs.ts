import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import type { JobRecord, WsEvent } from "@/api/types"

export interface ApiJob {
  id: string
  capability: string
  providerName: string
  status: string
  queuedAt: string
  startedAt?: string
  completedAt?: string
  durationMs?: number
  errorMessage?: string
  callerInfo?: string
  name?: string
  rationale?: string
  progress?: number
  input?: string
  outputLocation?: string
  outputSizeBytes?: number
  outputContentType?: string
  resultMetadata?: string
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
    errorMessage: j.errorMessage,
    callerInfo: j.callerInfo,
    name: j.name,
    rationale: j.rationale,
    progress: j.progress,
    inputJson: j.input || "{}",
    outputLocation: j.outputLocation,
    outputSizeBytes: j.outputSizeBytes,
    outputContentType: j.outputContentType,
    resultJson: j.resultMetadata,
    errorDetails: j.errorDetails,
  }
}

export function useJobs(limit = 50) {
  const [jobs, setJobs] = useState<JobRecord[]>([])
  const [selectedJob, setSelectedJob] = useState<JobRecord | null>(null)

  const refresh = useCallback(async () => {
    try {
      const data = await api.get<ApiJob[]>(`/jobs?limit=${limit}`)
      setJobs(data.map(mapJob))
    } catch { /* offline */ }
  }, [limit])

  useEffect(() => { refresh() }, [refresh])

  const handleWsEvent = useCallback((event: WsEvent) => {
    if (event.type === "job.created") {
      const job = event.data as JobRecord
      setJobs(prev => [job, ...prev].slice(0, limit))
    } else if (event.type === "job.updated") {
      const job = event.data as JobRecord
      setJobs(prev => prev.map(j => j.id === job.id ? job : j))
      setSelectedJob(prev => prev?.id === job.id ? job : prev)
    }
  }, [limit])

  const selectJob = useCallback(async (job: JobRecord) => {
    try {
      const detail = await api.get<ApiJob>(`/jobs/${job.id}`)
      setSelectedJob(mapJob(detail))
    } catch {
      setSelectedJob(job)
    }
  }, [])

  return { jobs, selectedJob, setSelectedJob: selectJob, refresh, handleWsEvent }
}
