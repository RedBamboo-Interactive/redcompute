import { ItemList, ItemListRow } from "@redbamboo/ui"
import type { CapabilityStatus, JobRecord } from "@/api/types"

const statusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

export function JobList({ jobs, selectedId, onSelect, hasActiveFilters, hasMore, loading, onLoadMore, capMap }: {
  jobs: JobRecord[]
  selectedId: string | null
  onSelect: (job: JobRecord) => void
  hasActiveFilters?: boolean
  hasMore?: boolean
  loading?: boolean
  onLoadMore?: () => void
  capMap?: Map<string, CapabilityStatus>
}) {
  return (
    <ItemList
      items={jobs}
      keyFn={(j) => j.id}
      emptyMessage={hasActiveFilters ? "No matching jobs" : "No jobs yet"}
      hasMore={hasMore}
      loading={loading}
      onLoadMore={onLoadMore}
      renderItem={(job) => (
        <ItemListRow
          selected={selectedId === job.id}
          onClick={() => onSelect(job)}
          icon={
            <i
              className={`${capMap?.get(job.capabilitySlug)?.icon || "fa-solid fa-cube"} text-xs`}
              style={{ color: statusColor[job.status] || "#6B6F77" }}
            />
          }
          title={job.name || job.capabilitySlug}
          badge={undefined}
          subtitle={
            <div className="flex items-center gap-1.5">
              <span>{job.providerName}</span>
              {job.callerInfo && (
                <>
                  <span className="opacity-50">&middot;</span>
                  <span className="text-text-disabled">{job.callerInfo}</span>
                </>
              )}
            </div>
          }
        />
      )}
    />
  )
}
