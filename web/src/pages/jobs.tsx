import { useEffect, useState } from "react"
import { JobList } from "@/components/jobs/job-list"
import { JobDetail } from "@/components/jobs/job-detail"
import { ActivityFrieze } from "@/components/jobs/activity-frieze"
import { api } from "@/api/client"
import type { JobRecord } from "@/api/types"

interface ApiJob {
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
}

function mapApiJob(j: ApiJob): JobRecord {
  return {
    id: j.id, capabilitySlug: j.capability, providerName: j.providerName,
    status: j.status as JobRecord["status"], queuedAt: j.queuedAt,
    startedAt: j.startedAt, completedAt: j.completedAt, durationMs: j.durationMs,
    errorMessage: j.errorMessage, callerInfo: j.callerInfo, name: j.name,
    rationale: j.rationale, inputJson: "{}",
  }
}

export function JobsPage({ jobs, selectedJob, onSelectJob }: {
  jobs: JobRecord[]
  selectedJob: JobRecord | null
  onSelectJob: (job: JobRecord) => void
}) {
  const [activityJobs, setActivityJobs] = useState<JobRecord[]>([])

  useEffect(() => {
    api.get<ApiJob[]>("/jobs?limit=200").then(data => {
      setActivityJobs(data.map(mapApiJob))
    }).catch(() => {})
  }, [jobs.length])

  return (
    <div className="flex flex-col gap-2 h-[calc(100vh-3rem)] p-6">
      <h1 className="text-[20px] font-semibold text-white opacity-95">Job Monitor</h1>

      {/* Activity timeline frieze */}
      <ActivityFrieze jobs={activityJobs} />

      {/* Master-detail layout */}
      <div className="flex gap-4 flex-1 min-h-0">
        {/* Left panel: Job list */}
        <div className="w-80 shrink-0 bg-surface-elevated rounded-lg overflow-hidden flex flex-col">
          <div className="flex items-center justify-between px-4 py-3 border-b border-white/[0.06]">
            <span className="text-[14px] font-medium text-white">Recent Jobs</span>
            <button className="flex items-center gap-1 text-text-muted text-[12px] hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10"
              title="Clear all jobs">
              <i className="fa-solid fa-trash-can text-xs" />
              <span>Clear</span>
            </button>
          </div>
          <div className="flex-1 overflow-hidden">
            <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={onSelectJob} />
          </div>
        </div>

        {/* Right panel: Job detail */}
        <div className="flex-1 overflow-auto">
          {selectedJob ? (
            <JobDetail job={selectedJob} />
          ) : (
            <div className="flex items-center justify-center h-full text-text-muted text-sm">
              Select a job to view details
            </div>
          )}
        </div>
      </div>
    </div>
  )
}
