import { useState, useEffect, useCallback } from "react"
import { api } from "@/api/client"
import { useWsSubscribe } from "@redbamboo/utility"
import { mapJob, type ApiJob } from "./use-jobs"
import type { JobRecord } from "@/api/types"

export function useCapabilityJobs(slug: string, limit = 50) {
  const [jobs, setJobs] = useState<JobRecord[]>([])

  useEffect(() => {
    api.get<{ items: ApiJob[]; total: number }>(`/jobs?capability=${slug}&limit=${limit}`)
      .then(data => setJobs(data.items.map(mapJob)))
      .catch(() => {})
  }, [slug, limit])

  useWsSubscribe(useCallback((event) => {
    if (event.type === "job.created") {
      const job = event.data as JobRecord
      if (job.capabilitySlug === slug) {
        setJobs(prev => [job, ...prev].slice(0, limit))
      }
    } else if (event.type === "job.updated") {
      const job = event.data as JobRecord
      if (job.capabilitySlug === slug) {
        setJobs(prev => prev.map(j => j.id === job.id ? job : j))
      }
    }
  }, [slug, limit]))

  return jobs
}
