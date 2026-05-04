import { useMemo, useState, useEffect } from "react"
import type { JobRecord } from "@/api/types"

const statusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

const IDLE = "#2A2A2A"
const QUANTUM_MS = 5000

export function ActivityFrieze({ jobs, widthQuanta = 60 }: { jobs: JobRecord[]; widthQuanta?: number }) {
  const [tick, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 5000)
    return () => clearInterval(id)
  }, [])

  const quanta = useMemo(() => {
    const windowMs = widthQuanta * QUANTUM_MS
    const now = Date.now()
    const start = now - windowMs
    const result: { color: string; tooltip: string }[] = []

    for (let i = 0; i < widthQuanta; i++) {
      const qStart = start + i * QUANTUM_MS
      const qEnd = qStart + QUANTUM_MS
      const overlapping: { color: string; name: string }[] = []

      for (const job of jobs) {
        const jobStart = new Date(job.startedAt || job.queuedAt).getTime()
        const jobEnd = job.completedAt ? new Date(job.completedAt).getTime() : now
        if (jobStart < qEnd && jobEnd > qStart) {
          overlapping.push({
            color: statusColor[job.status] || IDLE,
            name: job.name || job.capabilitySlug,
          })
        }
      }

      if (overlapping.length === 0) {
        result.push({ color: IDLE, tooltip: "" })
      } else {
        result.push({
          color: overlapping[0].color,
          tooltip: overlapping.map(o => o.name).join(", "),
        })
      }
    }

    return result
  }, [jobs, widthQuanta, tick])

  const totalSec = Math.round((widthQuanta * QUANTUM_MS) / 1000)
  const durationText = totalSec >= 60 ? `${Math.round(totalSec / 60)}m` : `${totalSec}s`

  return (
    <div className="bg-surface-elevated rounded-lg p-3">
      <div className="flex items-center justify-between mb-1 px-0.5">
        <div className="flex items-center gap-1.5">
          <i className="fa-solid fa-wave-square text-white opacity-50 text-xs" />
          <span className="text-[11px] font-semibold text-white opacity-40 tracking-wide">ACTIVITY</span>
        </div>
        <span className="text-[11px] font-mono text-white opacity-40">{durationText}</span>
      </div>
      <div className="flex flex-wrap">
        {quanta.map((q, i) => (
          <div key={i} className="w-2.5 h-2.5 p-px" title={q.tooltip || undefined}>
            <div className="w-full h-full rounded-[2px] transition-all duration-100 hover:opacity-70 hover:scale-[1.3]"
              style={{ backgroundColor: q.color }} />
          </div>
        ))}
      </div>
    </div>
  )
}
