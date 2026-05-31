import { useMemo, useState, useEffect } from "react"
import { useSearchParams } from "react-router-dom"
import { JobList } from "@/components/jobs/job-list"
import { JobDetail } from "@/components/jobs/job-detail"
import { MasterDetailLayout, PanelHeader, FilterBar, FilterPillGroup } from "@redbamboo/ui"
import type { CapabilityStatus, JobRecord } from "@/api/types"
import { useAppState } from "@/contexts/app-state"

const statusColors: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}


export function JobsPage() {
  const { jobs: jobsState, jobFilters: filters, setJobFilters: onFiltersChange, caps } = useAppState()
  const { jobs, total, hasMore, loading, selectedJob, setSelectedJob: onSelectJob } = jobsState
  const onLoadMore = jobsState.loadMore
  const capsList = caps.capabilities

  const capMap = useMemo(() => {
    const m = new Map<string, CapabilityStatus>()
    for (const c of capsList) m.set(c.slug, c)
    return m
  }, [capsList])

  const [searchParams, setSearchParams] = useSearchParams()
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
      .map(([slug, count]) => ({ value: slug, count }))
  }, [jobs])

  const sources = useMemo(() => {
    const counts = new Map<string, number>()
    for (const j of jobs) {
      const src = j.callerInfo || "unknown"
      counts.set(src, (counts.get(src) || 0) + 1)
    }
    return [...counts.entries()]
      .sort((a, b) => b[1] - a[1])
      .map(([name, count]) => ({ value: name, count }))
  }, [jobs])

  const statuses = useMemo(() => {
    const order = ["Running", "Queued", "Completed", "Failed", "Cancelled"]
    const counts = new Map<string, number>()
    for (const j of jobs) {
      counts.set(j.status, (counts.get(j.status) || 0) + 1)
    }
    return order
      .filter(s => counts.has(s))
      .map(s => ({ value: s, count: counts.get(s)!, color: statusColors[s] }))
  }, [jobs])

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

  function handleRerun(newJobId: string) {
    const stub: JobRecord = {
      id: newJobId, capabilitySlug: selectedJob!.capabilitySlug, providerName: selectedJob!.providerName,
      status: "Running", queuedAt: new Date().toISOString(), inputJson: "{}",
    }
    onSelectJob(stub)
  }

  const listHeader = (
    <>
      <PanelHeader title="Recent Jobs">
        {hasActiveFilters && (
          <button
            onClick={() => onFiltersChange({})}
            className="text-accent-teal text-[11px] hover:text-contrast transition-colors px-1.5 py-0.5 rounded hover:bg-overlay-10"
          >
            Clear filters
          </button>
        )}
      </PanelHeader>
      <FilterBar
        search={search}
        onSearch={setSearch}
        placeholder="Search jobs..."
        summary={hasActiveFilters ? `${jobs.length} of ${total} jobs` : undefined}
      >
        <FilterPillGroup
          label="Type"
          options={capabilities}
          value={capFilter}
          onChange={setCapFilter}
          activeColor="rgba(38,166,154,0.2)"
        />
        <FilterPillGroup
          label="Source"
          options={sources}
          value={sourceFilter}
          onChange={setSourceFilter}
          activeColor="rgba(212,170,79,0.2)"
          activeTextColor="#D4AA4F"
        />
        <FilterPillGroup
          label="Status"
          options={statuses}
          value={statusFilter}
          onChange={setStatusFilter}
        />
      </FilterBar>
    </>
  )

  const detailContent = selectedJob ? (
    <JobDetail job={selectedJob} onRerun={handleRerun} capability={capMap.get(selectedJob.capabilitySlug)} />
  ) : (
    <div className="flex items-center justify-center h-full text-text-muted text-sm">
      Select a job to view details
    </div>
  )

  return (
    <MasterDetailLayout
      layoutKey="redcompute-jobs"
      mobileLabels={["Jobs", "Detail"]}
      mobileTab={mobileTab}
      onMobileTabChange={setMobileTab}
      sidebar={
        <>
          {listHeader}
          <div className="flex-1 overflow-hidden">
            <JobList jobs={jobs} selectedId={selectedJob?.id || null} onSelect={handleSelectJob}
              hasActiveFilters={hasActiveFilters} hasMore={hasMore} loading={loading} onLoadMore={onLoadMore} capMap={capMap} />
          </div>
        </>
      }
      detail={
        <div className={`h-full ${selectedJob?.capabilitySlug === "ai-session" ? "overflow-hidden" : "overflow-auto"}`}>
          {detailContent}
        </div>
      }
    />
  )
}
