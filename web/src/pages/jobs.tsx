import { useEffect, useMemo, useState } from "react"
import { useSearchParams } from "react-router-dom"
import { JobList } from "@/components/jobs/job-list"
import { JobDetail } from "@/components/jobs/job-detail"
import { ActivityFrieze } from "@/components/jobs/activity-frieze"
import { Tabs, TabsList, TabsTrigger, TabsContent } from "@redbamboo/ui"
import { api } from "@/api/client"
import type { JobRecord } from "@/api/types"
import type { JobFilters } from "@/hooks/use-jobs"

const statusColors: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

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
  sessionStatus?: string
}

function mapApiJob(j: ApiJob): JobRecord {
  return {
    id: j.id, capabilitySlug: j.capability, providerName: j.providerName,
    status: j.status as JobRecord["status"], queuedAt: j.queuedAt,
    startedAt: j.startedAt, completedAt: j.completedAt, durationMs: j.durationMs,
    errorMessage: j.errorMessage, callerInfo: j.callerInfo, name: j.name,
    rationale: j.rationale, inputJson: "{}",
    sessionStatus: j.sessionStatus as JobRecord["sessionStatus"],
  }
}

export function JobsPage({ jobs, total, hasMore, loading, selectedJob, onSelectJob, onLoadMore, filters, onFiltersChange }: {
  jobs: JobRecord[]
  total: number
  hasMore: boolean
  loading: boolean
  selectedJob: JobRecord | null
  onSelectJob: (job: JobRecord) => void
  onLoadMore: () => void
  filters: JobFilters
  onFiltersChange: (f: JobFilters) => void
}) {
  const [searchParams, setSearchParams] = useSearchParams()
  const [activityJobs, setActivityJobs] = useState<JobRecord[]>([])
  const [mobileTab, setMobileTab] = useState(0)

  const search = filters.search || ""
  const capFilter = filters.capability || null
  const sourceFilter = filters.caller || null
  const statusFilter = filters.status || null

  const setSearch = (v: string) => onFiltersChange({ ...filters, search: v || undefined })
  const setCapFilter = (v: string | null) => onFiltersChange({ ...filters, capability: v || undefined })
  const setSourceFilter = (v: string | null) => onFiltersChange({ ...filters, caller: v || undefined })
  const setStatusFilter = (v: string | null) => onFiltersChange({ ...filters, status: v || undefined })

  const capabilities = useMemo(() => {
    const counts = new Map<string, number>()
    for (const j of jobs) {
      counts.set(j.capabilitySlug, (counts.get(j.capabilitySlug) || 0) + 1)
    }
    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([slug, count]) => ({ slug, count }))
  }, [jobs])

  const sources = useMemo(() => {
    const counts = new Map<string, number>()
    for (const j of jobs) {
      const src = j.callerInfo || "unknown"
      counts.set(src, (counts.get(src) || 0) + 1)
    }
    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([name, count]) => ({ name, count }))
  }, [jobs])

  const statuses = useMemo(() => {
    const order = ["Running", "Queued", "Completed", "Failed", "Cancelled"]
    const counts = new Map<string, number>()
    for (const j of jobs) {
      counts.set(j.status, (counts.get(j.status) || 0) + 1)
    }
    return order
      .filter(s => counts.has(s))
      .map(s => ({ status: s, count: counts.get(s)! }))
  }, [jobs])

  useEffect(() => {
    api.get<{ items: ApiJob[]; total: number }>("/jobs?limit=200").then(data => {
      setActivityJobs(data.items.map(mapApiJob))
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

  const hasActiveFilters = !!search || !!capFilter || !!sourceFilter || !!statusFilter

  const listHeader = (
    <div className="flex flex-col border-b border-white/[0.06]">
      <div className="flex items-center justify-between px-4 py-3">
        <span className="text-[14px] font-medium text-white">Recent Jobs</span>
        <div className="flex items-center gap-1">
          {hasActiveFilters && (
            <button
              onClick={() => onFiltersChange({})}
              className="text-accent-teal text-[11px] hover:text-white transition-colors px-1.5 py-0.5 rounded hover:bg-white/10"
              title="Clear filters"
            >
              Clear filters
            </button>
          )}
        </div>
      </div>

      <div className="px-3 pb-2 flex flex-col gap-2">
        <div className="flex items-center gap-1.5 bg-white/[0.08] rounded px-2 py-1.5">
          <i className="fa-solid fa-magnifying-glass text-[11px] text-text-disabled" />
          <input
            type="text"
            placeholder="Search jobs..."
            value={search}
            onChange={e => setSearch(e.target.value)}
            className="bg-transparent border-none outline-none text-[12px] text-[#DCDDDE] placeholder-text-disabled w-full"
          />
          {search && (
            <button onClick={() => setSearch("")} className="text-text-disabled hover:text-white transition-colors">
              <i className="fa-solid fa-xmark text-[10px]" />
            </button>
          )}
        </div>

        {capabilities.length > 1 && (
          <div className="flex items-center gap-1 overflow-x-auto">
            <span className="text-[10px] text-text-disabled shrink-0">Type</span>
            {capabilities.map(c => (
              <button
                key={c.slug}
                onClick={() => setCapFilter(capFilter === c.slug ? null : c.slug)}
                className="rounded px-1.5 py-0.5 text-[10px] cursor-pointer transition-colors shrink-0"
                style={{
                  backgroundColor: capFilter === c.slug ? "rgba(38,166,154,0.2)" : "rgba(255,255,255,0.08)",
                  color: capFilter === c.slug ? "#26A69A" : "#ADAEB3",
                }}
              >
                {c.slug}
              </button>
            ))}
          </div>
        )}

        {sources.length > 1 && (
          <div className="flex items-center gap-1 overflow-x-auto">
            <span className="text-[10px] text-text-disabled shrink-0">Source</span>
            {sources.map(s => (
              <button
                key={s.name}
                onClick={() => setSourceFilter(sourceFilter === s.name ? null : s.name)}
                className="rounded px-1.5 py-0.5 text-[10px] cursor-pointer transition-colors shrink-0"
                style={{
                  backgroundColor: sourceFilter === s.name ? "rgba(212,170,79,0.2)" : "rgba(255,255,255,0.08)",
                  color: sourceFilter === s.name ? "#D4AA4F" : "#ADAEB3",
                }}
              >
                {s.name}
              </button>
            ))}
          </div>
        )}

        {statuses.length > 1 && (
          <div className="flex items-center gap-1 overflow-x-auto">
            <span className="text-[10px] text-text-disabled shrink-0">Status</span>
            {statuses.map(s => {
              const color = statusColors[s.status] || "#6B6F77"
              return (
                <button
                  key={s.status}
                  onClick={() => setStatusFilter(statusFilter === s.status ? null : s.status)}
                  className="rounded px-1.5 py-0.5 text-[10px] cursor-pointer transition-colors shrink-0"
                  style={{
                    backgroundColor: statusFilter === s.status ? `${color}33` : "rgba(255,255,255,0.08)",
                    color: statusFilter === s.status ? color : "#ADAEB3",
                  }}
                >
                  {s.status}
                </button>
              )
            })}
          </div>
        )}
      </div>

      {hasActiveFilters && (
        <div className="px-3 pb-2">
          <span className="text-[11px] text-text-disabled">
            {jobs.length} of {total} jobs
          </span>
        </div>
      )}
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

      <ActivityFrieze jobs={activityJobs} />

      {/* Desktop: side-by-side master-detail */}
      <div className="hidden md:flex gap-4 flex-1 min-h-0">
        <div className="w-80 shrink-0 bg-surface-elevated rounded-lg overflow-hidden flex flex-col">
          {listHeader}
          <div className="flex-1 overflow-hidden">
            <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={handleSelectJob}
              hasActiveFilters={hasActiveFilters} hasMore={hasMore} loading={loading} onLoadMore={onLoadMore} />
          </div>
        </div>
        <div className={`flex-1 ${selectedJob?.capabilitySlug === "ai-session" ? "overflow-hidden" : "overflow-auto"}`}>
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
              <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={handleSelectJob}
                hasActiveFilters={hasActiveFilters} hasMore={hasMore} loading={loading} onLoadMore={onLoadMore} />
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
