import { ScrollArea } from "@/components/ui/scroll-area"
import type { JobRecord } from "@/api/types"

const statusColor: Record<string, string> = {
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
  Completed: "#26A69A",
  Failed: "#E55B5B",
  Cancelled: "#727C7D",
}

const capIcons: Record<string, string> = {
  tts: "fa-solid fa-volume-high",
  stt: "fa-solid fa-microphone",
  "image-gen": "fa-solid fa-image",
  "music-gen": "fa-solid fa-music",
  llm: "fa-solid fa-brain",
  "video-gen": "fa-solid fa-video",
  "ai-session": "fa-regular fa-square-terminal",
}

function timeAgo(dateStr: string): string {
  const seconds = Math.floor((Date.now() - new Date(dateStr).getTime()) / 1000)
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return `${Math.floor(seconds / 86400)}d ago`
}

export function JobList({ jobs, selectedId, onSelect }: {
  jobs: JobRecord[]
  selectedId: string | null
  onSelect: (job: JobRecord) => void
}) {
  return (
    <ScrollArea className="h-full">
      <div className="flex flex-col">
        {jobs.map(job => (
          <button
            key={job.id}
            onClick={() => onSelect(job)}
            className={`flex items-center gap-3 px-4 py-3 text-left transition-colors border-b border-white/[0.06] ${
              selectedId === job.id ? "bg-white/[0.08]" : "hover:bg-white/[0.04]"
            }`}
          >
            {/* Capability icon — color = status */}
            <div className="w-8 h-8 rounded-lg bg-surface-base flex items-center justify-center shrink-0">
              <i className={`${capIcons[job.capabilitySlug] || "fa-solid fa-cog"} text-xs`}
                style={{ color: statusColor[job.status] || "#6B6F77" }} />
            </div>

            {/* Name + meta stacked */}
            <div className="flex-1 min-w-0">
              <div className="flex items-center gap-2">
                <span className="text-[13px] font-medium truncate text-white">
                  {job.name || job.capabilitySlug}
                </span>
                {job.status === "Running" && (
                  <span className="w-1.5 h-1.5 rounded-full bg-accent-gold animate-pulse shrink-0" />
                )}
              </div>
              <div className="flex items-center gap-1.5 text-[11px] text-text-muted">
                <span>{job.providerName}</span>
                <span className="opacity-50">·</span>
                <span>{timeAgo(job.queuedAt)}</span>
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
        {jobs.length === 0 && (
          <p className="text-text-muted text-sm p-4 text-center">No jobs yet</p>
        )}
      </div>
    </ScrollArea>
  )
}
