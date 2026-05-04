import { useState } from "react"
import { useNavigate } from "react-router-dom"
import { api } from "@/api/client"
import { MiniFrieze } from "./mini-frieze"
import { QueueJobDialog } from "@/components/jobs/queue-job-dialog"
import type { CapabilityStatus, JobRecord } from "@/api/types"

const capabilityIcons: Record<string, string> = {
  tts: "fa-solid fa-volume-high",
  stt: "fa-solid fa-microphone",
  "image-gen": "fa-solid fa-image",
  "music-gen": "fa-solid fa-music",
  llm: "fa-solid fa-brain",
  "video-gen": "fa-solid fa-video",
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

export function CapabilityCard({ cap, jobs, onRefresh }: {
  cap: CapabilityStatus
  jobs: JobRecord[]
  onRefresh: () => void
}) {
  const [queueOpen, setQueueOpen] = useState(false)
  const navigate = useNavigate()
  const isRunning = cap.status === "Running"
  const iconColor = statusIconColor(cap.status, cap.sleeping)
  const capJobs = jobs.filter(j => j.capabilitySlug === cap.slug)

  async function togglePower() {
    try {
      await api.post(`/control/${isRunning ? "stop" : "start"}/${cap.slug}`)
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
      <div className="w-[310px] rounded-xl shadow-[0_2px_12px_rgba(0,0,0,0.35)]">
        <div className="bg-surface-elevated rounded-xl p-5">
          {/* Action buttons — top right */}
          <div className="flex justify-end gap-0 -mt-1 -mr-1 mb-0">
            <button onClick={togglePower} title={cap.status}
              className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-white/10 transition-colors">
              <i className="fa-solid fa-power-off text-sm"
                style={{
                  color: isRunning ? "#26A69A" : "#72767D",
                  opacity: isRunning ? 1 : 0.4,
                }} />
            </button>
            <button onClick={toggleSleep} title="Sleep/Wake (freeze requests)"
              className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-white/10 transition-colors">
              <i className="fa-solid fa-moon text-xs"
                style={{
                  color: cap.sleeping ? "#7C4DFF" : "#ADAEB3",
                  opacity: cap.sleeping ? 1 : 0.4,
                }} />
            </button>
            <button onClick={() => setQueueOpen(true)} title="Queue Job"
              className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-white/10 transition-colors">
              <i className="fa-solid fa-plus text-xs text-text-muted opacity-60" />
            </button>
            <button title="Settings" onClick={() => navigate("/settings")}
              className="w-[30px] h-[30px] flex items-center justify-center rounded hover:bg-white/10 transition-colors">
              <i className="fa-solid fa-gear text-xs text-text-muted opacity-60" />
            </button>
          </div>

          {/* Centered icon header */}
          <div className="flex justify-center mb-3">
            <div className="w-14 h-14 rounded-full border border-[#3A3A3F] flex items-center justify-center">
              <i className={`${capabilityIcons[cap.slug] || "fa-solid fa-cog"} text-[22px]`}
                style={{ color: iconColor }} />
            </div>
          </div>

          {/* Title + provider */}
          <div className="text-center mb-3.5">
            <div className="text-[15px] font-semibold text-white">{cap.displayName}</div>
            <div className="text-[11px] text-text-muted opacity-70 truncate">
              {cap.provider || "No provider"}
            </div>
          </div>

          {/* Separator — full-width (negative margin to match WPF -20,0) */}
          <div className="h-px bg-border-subtle opacity-50 -mx-5" />

          {/* Mini frieze (activity timeline) */}
          <div className="mt-3.5 mb-1.5">
            <MiniFrieze jobs={capJobs} count={32} />
          </div>

          {/* Job frieze (recent job results) */}
          <div className="mb-0.5">
            <JobFrieze jobs={capJobs} count={32} />
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

function JobFrieze({ jobs, count }: { jobs: JobRecord[]; count: number }) {
  const recent = jobs.slice(0, count).reverse()
  const segments: { color: string; tooltip: string }[] = []

  for (let i = 0; i < count - recent.length; i++) {
    segments.push({ color: "#2A2A2A", tooltip: "" })
  }
  for (const job of recent) {
    const color = jobStatusColor[job.status] || "#2A2A2A"
    const tooltip = job.status === "Completed" && job.durationMs
      ? `Completed (${job.durationMs}ms)`
      : job.status === "Failed"
        ? `Failed: ${job.errorMessage || "unknown"}`
        : job.status
    segments.push({ color, tooltip })
  }

  return (
    <div className="flex flex-wrap justify-center">
      {segments.map((seg, i) => (
        <div key={i} className="w-2 h-2 p-px" title={seg.tooltip || undefined}>
          <div className="w-full h-full rounded-[1px]" style={{ backgroundColor: seg.color }} />
        </div>
      ))}
    </div>
  )
}
