import { useState, useRef, useEffect, useCallback } from "react"
import { useNavigate } from "react-router-dom"
import { api } from "@/api/client"
import { MiniFrieze } from "./mini-frieze"
import { QueueJobDialog } from "@/components/jobs/queue-job-dialog"
import { useCapabilityJobs } from "@/hooks/use-capability-jobs"
import type { CapabilityStatus, JobRecord } from "@/api/types"

function getCapIcon(cap: CapabilityStatus): string {
  return cap.icon || "fa-solid fa-cube"
}

function statusIconColor(status: string, sleeping: boolean): string {
  if (sleeping) return "#7C4DFF"
  switch (status) {
    case "Running": return "#26A69A"
    case "Starting": return "#FFB74D"
    case "Error": return "#FF5252"
    default: return "#9098A0"
  }
}

const SQUARE_PX = 8

export function CapabilityCard({ cap, onRefresh }: {
  cap: CapabilityStatus
  onRefresh: () => void
}) {
  const [queueOpen, setQueueOpen] = useState(false)
  const navigate = useNavigate()
  const isRunning = cap.status === "Running"
  const iconColor = statusIconColor(cap.status, cap.sleeping)
  const capJobs = useCapabilityJobs(cap.slug)
  const hasMultipleProviders = (cap.providers?.length ?? 0) > 1

  const friezeRef = useRef<HTMLDivElement>(null)
  const [friezeCols, setFriezeCols] = useState(0)

  const measure = useCallback(() => {
    if (!friezeRef.current) return
    setFriezeCols(Math.floor(friezeRef.current.clientWidth / SQUARE_PX))
  }, [])

  useEffect(() => {
    measure()
    const ro = new ResizeObserver(measure)
    if (friezeRef.current) ro.observe(friezeRef.current)
    return () => ro.disconnect()
  }, [measure])

  async function togglePower(providerName?: string) {
    try {
      const status = providerName
        ? cap.providers?.find(p => p.name === providerName)?.status
        : cap.status
      const running = status === "Running"
      const path = providerName
        ? `/control/${running ? "stop" : "start"}/${cap.slug}/${providerName}`
        : `/control/${isRunning ? "stop" : "start"}/${cap.slug}`
      await api.post(path)
      onRefresh()
    } catch { /* */ }
  }

  async function toggleSleep() {
    try {
      await api.post(`/control/${cap.sleeping ? "wake" : "sleep"}/${cap.slug}`)
      onRefresh()
    } catch { /* */ }
  }

  return (
    <>
      <div className="rounded-xl shadow-[0_1px_6px_rgba(0,0,0,0.15)]">
        <div className="bg-surface-elevated rounded-xl p-5">
          {/* Action buttons — top right */}
          <div className="flex justify-end gap-0 -mt-1 -mr-1 mb-0">
            <button onClick={toggleSleep} title="Sleep/Wake (freeze requests)"
              className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-overlay-10 transition-colors">
              <i className="fa-solid fa-moon text-xs"
                style={{
                  color: cap.sleeping ? "#7C4DFF" : "#ADAEB3",
                  opacity: cap.sleeping ? 1 : 0.4,
                }} />
            </button>
            {!hasMultipleProviders && (
              <button onClick={() => togglePower()} title={cap.status}
                className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-overlay-10 transition-colors">
                <i className="fa-solid fa-power-off text-sm"
                  style={{
                    color: isRunning ? "#26A69A" : "#6B6F77",
                    opacity: isRunning ? 1 : 0.4,
                  }} />
              </button>
            )}
          </div>

          {/* Centered icon — shows + overlay on hover to queue a job */}
          <div className="flex justify-center mb-3">
            <button
              onClick={() => setQueueOpen(true)}
              title="Queue a new job"
              className="group relative w-14 h-14 rounded-full border border-[#3A3A3F] flex items-center justify-center hover:border-overlay-20 hover:bg-overlay-4 hover:scale-110 active:scale-95 transition-all duration-300 ease-out cursor-pointer"
            >
              <i className={`${getCapIcon(cap)} text-[22px] group-hover:opacity-0 group-hover:scale-75 transition-all duration-300`}
                style={{ color: iconColor }} />
              <i className="fa-solid fa-plus text-overlay-80 text-lg absolute opacity-0 scale-50 group-hover:opacity-100 group-hover:scale-100 transition-all duration-300" />
            </button>
          </div>

          {/* Title + provider(s) */}
          <div className="text-center mb-3.5">
            <div className="text-[15px] font-semibold text-contrast">{cap.displayName}</div>
            {hasMultipleProviders ? (
              <div className="mt-1.5 space-y-1">
                {cap.providers!.map(p => {
                  const pRunning = p.status === "Running"
                  const isDefault = p.name === cap.defaultProvider
                  return (
                    <div key={p.name} className="flex items-center justify-center gap-1.5 text-[11px]">
                      <button onClick={() => togglePower(p.name)} title={`${pRunning ? "Stop" : "Start"} ${p.name}`}
                        className="w-[18px] h-[18px] flex items-center justify-center rounded hover:bg-overlay-10 transition-colors">
                        <i className="fa-solid fa-power-off text-[9px]"
                          style={{ color: pRunning ? "#26A69A" : "#6B6F77", opacity: pRunning ? 1 : 0.4 }} />
                      </button>
                      <span className="text-text-muted opacity-70">{p.name}</span>
                      {isDefault && <span className="text-[9px] text-text-muted opacity-50">default</span>}
                    </div>
                  )
                })}
              </div>
            ) : (
              <div className="text-[11px] text-text-muted opacity-70 truncate">
                {cap.provider || "No provider"}
              </div>
            )}
          </div>

          {/* Separator */}
          <div className="h-px bg-border-subtle opacity-50 -mx-5" />

          {/* Friezes — adaptive to card width */}
          <div ref={friezeRef} className="mt-3.5">
            <div className="mb-1.5">
              <MiniFrieze jobs={capJobs} count={friezeCols} />
            </div>
            <JobFrieze jobs={capJobs} count={friezeCols} onSelectJob={id => navigate(`/jobs?select=${id}`)} />
          </div>
        </div>
      </div>

      <QueueJobDialog
        open={queueOpen}
        onOpenChange={setQueueOpen}
        capabilities={[cap]}
        defaultSlug={cap.slug}
      />
    </>
  )
}

const jobStatusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

function JobFrieze({ jobs, count, onSelectJob }: { jobs: JobRecord[]; count: number; onSelectJob: (id: string) => void }) {
  const recent = jobs.slice(0, count).reverse()
  const segments: { color: string; className?: string; tooltip: string; jobId?: string }[] = []

  for (let i = 0; i < count - recent.length; i++) {
    segments.push({ color: "", className: "activity-idle", tooltip: "" })
  }
  for (const job of recent) {
    const color = jobStatusColor[job.status] || ""
    const className = color ? undefined : "activity-idle"
    const tooltip = job.status === "Completed" && job.durationMs
      ? `Completed (${job.durationMs}ms)`
      : job.status === "Failed"
        ? `Failed: ${job.errorMessage || "unknown"}`
        : job.status
    segments.push({ color, className, tooltip, jobId: job.id })
  }

  return (
    <div className="flex flex-wrap justify-center">
      {segments.map((seg, i) => (
        <div
          key={i}
          style={{ width: SQUARE_PX, height: SQUARE_PX }}
          className={`p-px ${seg.jobId ? "cursor-pointer" : ""}`}
          title={seg.tooltip || undefined}
          onClick={seg.jobId ? () => onSelectJob(seg.jobId!) : undefined}
        >
          <div
            className={`w-full h-full rounded-[1px] transition-transform duration-200 ${seg.jobId ? "hover:scale-150" : ""} ${seg.className || ""}`}
            style={seg.color ? { backgroundColor: seg.color, transitionTimingFunction: "cubic-bezier(0.34, 1.56, 0.64, 1)" } : { transitionTimingFunction: "cubic-bezier(0.34, 1.56, 0.64, 1)" }}
          />
        </div>
      ))}
    </div>
  )
}
