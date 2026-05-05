import { CapabilityCard } from "@/components/dashboard/capability-card"
import type { CapabilityStatus, JobRecord } from "@/api/types"

export function DashboardPage({ capabilities, jobs, onRefresh }: {
  capabilities: CapabilityStatus[]
  jobs: JobRecord[]
  onRefresh: () => void
}) {
  return (
    <div className="p-4 md:p-6">
      <span className="group font-bold text-xl tracking-wider flex w-fit items-center gap-2 relative select-none mb-5">
        <span className="absolute -inset-2 bg-primary/15 blur-lg rounded-full transition-all duration-300 group-hover:bg-primary/30 group-hover:blur-xl group-hover:-inset-3" aria-hidden="true" />
        <i className="fa-solid fa-microchip text-lg text-primary relative" aria-hidden="true" />
        <span className="relative"><span className="text-primary">Red</span><span className="text-muted-foreground">Compute</span></span>
      </span>
      {capabilities.length === 0 ? (
        <p className="text-text-muted">No capabilities registered. Check your config.json.</p>
      ) : (
        <div className="flex flex-col gap-4 md:flex-row md:flex-wrap md:gap-x-5 md:gap-y-5">
          {capabilities.map(cap => (
            <CapabilityCard key={cap.slug} cap={cap} jobs={jobs} onRefresh={onRefresh} />
          ))}
        </div>
      )}
    </div>
  )
}
