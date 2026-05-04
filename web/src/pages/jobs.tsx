import { useEffect, useState } from "react"
import { useSearchParams } from "react-router-dom"
import { JobList } from "@/components/jobs/job-list"
import { JobDetail } from "@/components/jobs/job-detail"
import { ActivityFrieze } from "@/components/jobs/activity-frieze"
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs"
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
  const [searchParams, setSearchParams] = useSearchParams()
  const [activityJobs, setActivityJobs] = useState<JobRecord[]>([])
  const [mobileTab, setMobileTab] = useState(0)

  useEffect(() => {
    api.get<ApiJob[]>("/jobs?limit=200").then(data => {
      setActivityJobs(data.map(mapApiJob))
    }).catch(() => {})
  }, [jobs.length])

  useEffect(() => {
    const selectId = searchParams.get("select")
    if (selectId) {
      const job = jobs.find(j => j.id === selectId)
      if (job) { onSelectJob(job); setMobileTab(1) }
      setSearchParams({}, { replace: true })
    }
  }, [searchParams, jobs, onSelectJob, setSearchParams])

  function handleSelectJob(job: JobRecord) {
    onSelectJob(job)
    setMobileTab(1)
  }

  const listHeader = (
    <div className="flex items-center justify-between px-4 py-3 border-b border-white/[0.06]">
      <span className="text-[14px] font-medium text-white">Recent Jobs</span>
      <button className="flex items-center gap-1 text-text-muted text-[12px] hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10"
        title="Clear all jobs">
        <i className="fa-solid fa-trash-can text-xs" />
        <span>Clear</span>
      </button>
    </div>
  )

  const detailContent = selectedJob ? (
    <JobDetail job={selectedJob} />
  ) : (
    <div className="flex items-center justify-center h-full text-text-muted text-sm">
      Select a job to view details
    </div>
  )

  return (
    <div className="flex flex-col gap-2 h-full p-4 md:p-6">
      <h1 className="text-[20px] font-semibold text-white opacity-95">Job Monitor</h1>

      <ActivityFrieze jobs={activityJobs} />

      {/* Desktop: side-by-side master-detail */}
      <div className="hidden md:flex gap-4 flex-1 min-h-0">
        <div className="w-80 shrink-0 bg-surface-elevated rounded-lg overflow-hidden flex flex-col">
          {listHeader}
          <div className="flex-1 overflow-hidden">
            <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={handleSelectJob} />
          </div>
        </div>
        <div className="flex-1 overflow-auto">
          {detailContent}
        </div>
      </div>

      {/* Mobile: tabbed layout */}
      <Tabs value={mobileTab} onValueChange={v => setMobileTab(v as number)} className="flex-1 min-h-0 md:hidden">
        <TabsList className="w-full">
          <TabsTrigger value={0}>Jobs</TabsTrigger>
          <TabsTrigger value={1}>Detail</TabsTrigger>
        </TabsList>
        <TabsContent value={0} className="min-h-0 overflow-hidden">
          <div className="bg-surface-elevated rounded-lg overflow-hidden flex flex-col h-full">
            {listHeader}
            <div className="flex-1 overflow-hidden">
              <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={handleSelectJob} />
            </div>
          </div>
        </TabsContent>
        <TabsContent value={1} className="min-h-0 overflow-auto">
          {detailContent}
        </TabsContent>
      </Tabs>
    </div>
  )
}
