import { ScrollArea } from "@redbamboo/ui"
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
    <ScrollArea className="h-full">
      <div className="flex flex-col">
        {jobs.map(job => (
          <button
            key={job.id}
            onClick={() => onSelect(job)}
            className={`flex items-center gap-3 px-4 py-3 text-left transition-colors border-b border-contrast/[0.06] ${
              selectedId === job.id ? "bg-contrast/[0.08]" : "hover:bg-contrast/[0.04]"
            }`}
          >
            {/* Capability icon — color = status */}
            <div className="w-8 h-8 rounded-lg bg-surface-base flex items-center justify-center shrink-0">
              <i className={`${capMap?.get(job.capabilitySlug)?.icon || "fa-solid fa-cube"} text-xs`}
                style={{ color: statusColor[job.status] || "#6B6F77" }} />
            </div>

            {/* Name + meta stacked */}
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <span className="text-[13px] font-medium truncate text-contrast">
                  {job.name || job.capabilitySlug}
                </span>
                {job.status === "Running" && (
                  <span className="w-1.5 h-1.5 rounded-full bg-accent-gold animate-pulse shrink-0" />
                )}
              </div>
              <div className="flex items-center gap-1.5 text-[11px] text-text-muted">
                <span>{job.providerName}</span>
                {job.callerInfo && (
                  <>
                    <span className="opacity-50">·</span>
                    <span className="text-text-disabled">{job.callerInfo}</span>
                  </>
                )}
              </div>
            </div>
          </button>
        ))}
        {hasMore && (
          <button
            onClick={onLoadMore}
            disabled={loading}
            className="flex items-center justify-center gap-2 px-4 py-3 text-[12px] text-accent-teal hover:text-contrast transition-colors border-b border-contrast/[0.06] hover:bg-contrast/[0.04]"
          >
            {loading ? (
              <i className="fa-solid fa-spinner fa-spin text-xs" />
            ) : (
              <i className="fa-solid fa-chevron-down text-xs" />
            )}
            <span>Load more</span>
          </button>
        )}
        {jobs.length === 0 && (
          <p className="text-text-muted text-sm p-4 text-center">
            {hasActiveFilters ? "No matching jobs" : "No jobs yet"}
          </p>
        )}
      </div>
    </ScrollArea>
  )
}
