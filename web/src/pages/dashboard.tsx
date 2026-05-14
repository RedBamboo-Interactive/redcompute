import { CapabilityCard } from "@/components/dashboard/capability-card"
import { HardwareFooter } from "@/components/dashboard/hardware-footer"
import type { CapabilityStatus, HardwareSnapshot } from "@/api/types"

export function DashboardPage({ capabilities, onRefresh, hardware }: {
  capabilities: CapabilityStatus[]
  onRefresh: () => void
  hardware: HardwareSnapshot | null
}) {
  return (
    <div className="flex flex-col h-full">
      <div className="flex-1 p-4 md:p-6 overflow-auto">
        {capabilities.length === 0 ? (
          <p className="text-text-muted">No capabilities registered. Check your config.json.</p>
        ) : (
          <div className="grid gap-4" style={{ gridTemplateColumns: "repeat(auto-fill, minmax(260px, 1fr))" }}>
            {capabilities.map(cap => (
              <CapabilityCard key={cap.slug} cap={cap} onRefresh={onRefresh} />
            ))}
          </div>
        )}
      </div>
      {hardware?.available && <HardwareFooter hardware={hardware} capabilities={capabilities} />}
    </div>
  )
}
