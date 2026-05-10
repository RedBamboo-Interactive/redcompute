import { useState, useEffect } from "react"
import type { JobRecord } from "@/api/types"

const statusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

const IDLE = "idle"
const SESSION_IDLE = "session-idle"

const statusPriority: Record<string, number> = {
  Failed: 6, Running: 5, Queued: 4, Completed: 3, Cancelled: 2,
}

export function MiniFrieze({ jobs, count = 32 }: { jobs: JobRecord[]; count?: number }) {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 5000)
    return () => clearInterval(id)
  }, [])

  void tick
  const quantumMs = 5000
  const now = Date.now()
  const windowMs = count * quantumMs
  const windowStart = now - windowMs

  const segments: { color: string; tooltip: string }[] = []

  for (let q = 0; q < count; q++) {
    const qStart = windowStart + q * quantumMs
    const qEnd = qStart + quantumMs

    let bestColor = IDLE
    let bestTooltip = ""
    let bestPriority = -1

    for (const job of jobs) {
      const isIdleSession = job.capabilitySlug === "ai-session" && job.sessionStatus === "Idle"
      const jobStart = new Date(job.startedAt || job.queuedAt).getTime()
      const jobEnd = job.completedAt ? new Date(job.completedAt).getTime() : now

      if (jobStart < qEnd && jobEnd > qStart) {
        const p = isIdleSession ? 1 : (statusPriority[job.status] ?? 0)
        if (p > bestPriority) {
          bestPriority = p
          bestColor = isIdleSession ? SESSION_IDLE : (statusColor[job.status] || IDLE)
          bestTooltip = job.status === "Running"
            ? `Running: ${job.capabilitySlug}`
            : job.status === "Completed"
              ? `Completed (${job.durationMs ?? 0}ms)`
              : job.status === "Failed"
                ? `Failed: ${job.errorMessage || "unknown"}`
                : job.status
        }
      }
    }

    segments.push({ color: bestColor, tooltip: bestTooltip })
  }

  return (
    <div className="flex flex-wrap justify-center">
      {segments.map((seg, i) => (
        <div key={i} style={{ width: 8, height: 8 }} className="p-px" title={seg.tooltip || undefined}>
          <div className={`w-full h-full rounded-[1px] ${seg.color === IDLE ? "activity-idle" : seg.color === SESSION_IDLE ? "activity-session-idle" : ""}`}
            style={seg.color !== IDLE && seg.color !== SESSION_IDLE ? { backgroundColor: seg.color } : undefined} />
        </div>
      ))}
    </div>
  )
}
