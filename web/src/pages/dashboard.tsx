import { CapabilityCard } from "@/components/dashboard/capability-card"
import type { CapabilityStatus } from "@/api/types"

export function DashboardPage({ capabilities, onRefresh }: {
  capabilities: CapabilityStatus[]
  onRefresh: () => void
}) {
  return (
    <div className="p-4 md:p-6">
      {capabilities.length === 0 ? (
        <p className="text-text-muted">No capabilities registered. Check your config.json.</p>
      ) : (
        <div className="flex flex-col gap-4 md:flex-row md:flex-wrap md:gap-x-5 md:gap-y-5">
          {capabilities.map(cap => (
            <CapabilityCard key={cap.slug} cap={cap} onRefresh={onRefresh} />
          ))}
        </div>
      )}
    </div>
  )
}
