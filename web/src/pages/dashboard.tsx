import { CapabilityCard } from "@/components/dashboard/capability-card"
import type { CapabilityStatus, JobRecord } from "@/api/types"

export function DashboardPage({ capabilities, jobs, onRefresh }: {
  capabilities: CapabilityStatus[]
  jobs: JobRecord[]
  onRefresh: () => void
}) {
  return (
    <div className="p-6">
      <h1 className="text-[20px] font-semibold text-white opacity-95 mb-5">Capabilities</h1>
      {capabilities.length === 0 ? (
        <p className="text-text-muted">No capabilities registered. Check your config.json.</p>
      ) : (
        <div className="flex flex-wrap gap-x-5 gap-y-5">
          {capabilities.map(cap => (
            <CapabilityCard key={cap.slug} cap={cap} jobs={jobs} onRefresh={onRefresh} />
          ))}
        </div>
      )}
    </div>
  )
}
