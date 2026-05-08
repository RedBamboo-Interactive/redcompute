import { useMemo, useState, useEffect, useRef, useCallback } from "react"
import type { JobRecord } from "@/api/types"

const statusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

const IDLE = "#2A2A2A"
const SESSION_IDLE = "#1A3A36"
const QUANTUM_MS = 5000
const SQUARE_PX = 10
const ROWS = 3

export function ActivityFrieze({ jobs }: { jobs: JobRecord[] }) {
  const containerRef = useRef<HTMLDivElement>(null)
  const [cols, setCols] = useState(0)

  const measure = useCallback(() => {
    if (!containerRef.current) return
    const w = containerRef.current.clientWidth
    setCols(Math.floor(w / SQUARE_PX))
  }, [])

  useEffect(() => {
    measure()
    const ro = new ResizeObserver(measure)
    if (containerRef.current) ro.observe(containerRef.current)
    return () => ro.disconnect()
  }, [measure])

  const totalQuanta = cols * ROWS

  const [tick, setTick] = useState(0)
  useEffect(() => {
    const id = setInterval(() => setTick(t => t + 1), 5000)
    return () => clearInterval(id)
  }, [])

  const quanta = useMemo(() => {
    if (totalQuanta === 0) return []
    const windowMs = totalQuanta * QUANTUM_MS
    const now = Date.now()
    const start = now - windowMs
    const result: { color: string; tooltip: string }[] = []

    for (let i = 0; i < totalQuanta; i++) {
      const qStart = start + i * QUANTUM_MS
      const qEnd = qStart + QUANTUM_MS
      const overlapping: { color: string; name: string }[] = []

      for (const job of jobs) {
        const isIdleSession = job.capabilitySlug === "ai-session" && job.sessionStatus === "Idle"
        const jobStart = new Date(job.startedAt || job.queuedAt).getTime()
        const jobEnd = job.completedAt ? new Date(job.completedAt).getTime() : now
        if (jobStart < qEnd && jobEnd > qStart) {
          overlapping.push({
            color: isIdleSession ? SESSION_IDLE : (statusColor[job.status] || IDLE),
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
  }, [jobs, totalQuanta, tick])

  const totalSec = Math.round((totalQuanta * QUANTUM_MS) / 1000)
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
      <div ref={containerRef} className="flex flex-wrap">
        {quanta.map((q, i) => (
          <div key={i} style={{ width: SQUARE_PX, height: SQUARE_PX }} className="p-px" title={q.tooltip || undefined}>
            <div className="w-full h-full rounded-[2px] transition-all duration-100 hover:opacity-70 hover:scale-[1.3]"
              style={{ backgroundColor: q.color }} />
          </div>
        ))}
      </div>
    </div>
  )
}
