import { useEffect, useState } from "react"
import { Badge } from "@/components/ui/badge"
import { Card, CardContent } from "@/components/ui/card"
import { Separator } from "@/components/ui/separator"
import { api } from "@/api/client"
import type { JobRecord, LogEntry } from "@/api/types"

const statusBadgeColor: Record<string, string> = {
  Queued: "bg-accent-gold/20 text-accent-gold border-accent-gold/30",
  Running: "bg-accent-gold/20 text-accent-gold border-accent-gold/30",
  Completed: "bg-accent-teal/20 text-accent-teal border-accent-teal/30",
  Failed: "bg-accent-red/20 text-accent-red border-accent-red/30",
  Cancelled: "bg-text-disabled/20 text-text-disabled border-text-disabled/30",
}

function formatDuration(ms: number): string {
  if (ms < 1000) return `${ms}ms`
  if (ms < 60000) return `${(ms / 1000).toFixed(1)}s`
  return `${Math.floor(ms / 60000)}m ${Math.floor((ms % 60000) / 1000)}s`
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1024 * 1024) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / (1024 * 1024)).toFixed(1)} MB`
}

export function JobDetail({ job }: { job: JobRecord }) {
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [clipCount, setClipCount] = useState(1)

  useEffect(() => {
    api.get<{ entries: { id: number; timestamp: string; tag: string; message: string; isError: boolean }[] }>(`/jobs/${job.id}/logs?limit=50`)
      .then(data => setLogs(data.entries as unknown as LogEntry[]))
      .catch(() => {})
  }, [job.id])

  useEffect(() => {
    if (job.capabilitySlug !== "music-gen" || job.status !== "Completed") {
      setClipCount(1)
      return
    }
    let count = 1
    async function probe() {
      for (let i = 1; i <= 4; i++) {
        try {
          const controller = new AbortController()
          const res = await fetch(`/music-gen/jobs/${job.id}/output?clip=${i}`, {
            signal: controller.signal,
          })
          if (res.ok) {
            count++
            controller.abort()
          } else {
            break
          }
        } catch { break }
      }
      setClipCount(count)
    }
    probe()
  }, [job.id, job.capabilitySlug, job.status])

  const isAudio = job.outputContentType?.startsWith("audio/")
  const isImage = job.outputContentType?.startsWith("image/")
  const isVideo = job.outputContentType?.startsWith("video/")
  const hasOutput = job.status === "Completed" && job.outputContentType

  return (
    <div className="space-y-4">
      <div className="flex items-center gap-3">
        <h2 className="text-lg font-medium">{job.name || job.capabilitySlug}</h2>
        <Badge variant="outline" className={statusBadgeColor[job.status]}>{job.status}</Badge>
      </div>

      {job.rationale && (
        <p className="text-sm text-text-muted italic">{job.rationale}</p>
      )}

      {/* Metadata */}
      <Card className="bg-surface-base border-border-subtle">
        <CardContent className="p-4 space-y-2 text-sm">
          <Row label="ID" value={job.id} mono />
          <Row label="Capability" value={job.capabilitySlug} />
          <Row label="Provider" value={job.providerName} />
          <Row label="Queued" value={new Date(job.queuedAt).toLocaleString()} />
          {job.startedAt && <Row label="Started" value={new Date(job.startedAt).toLocaleString()} />}
          {job.completedAt && <Row label="Completed" value={new Date(job.completedAt).toLocaleString()} />}
          {job.durationMs != null && <Row label="Duration" value={formatDuration(job.durationMs)} />}
          {job.progress != null && job.status === "Running" && (
            <Row label="Progress" value={`${Math.round(job.progress * 100)}%`} />
          )}
          {job.callerInfo && <Row label="Caller" value={job.callerInfo} />}
          {job.outputSizeBytes != null && <Row label="Output Size" value={formatBytes(job.outputSizeBytes)} />}
        </CardContent>
      </Card>

      {/* Error */}
      {job.errorMessage && (
        <Card className="bg-accent-red/10 border-accent-red/30">
          <CardContent className="p-4">
            <p className="text-sm font-medium text-accent-red mb-1">Error</p>
            <p className="text-sm text-accent-red/80">{job.errorMessage}</p>
            {job.errorDetails && (
              <pre className="mt-2 text-xs font-mono text-accent-red/60 whitespace-pre-wrap max-h-40 overflow-auto">{job.errorDetails}</pre>
            )}
          </CardContent>
        </Card>
      )}

      {/* Input */}
      {job.inputJson && job.inputJson !== "{}" && (
        <>
          <Separator />
          <div>
            <p className="text-sm font-medium mb-2">Input</p>
            <pre className="text-xs font-mono bg-surface-base rounded-lg p-3 overflow-auto max-h-48 text-text-muted">
              {formatJson(job.inputJson)}
            </pre>
          </div>
        </>
      )}

      {/* Output */}
      {hasOutput && (
        <>
          <Separator />
          <div>
            <p className="text-sm font-medium mb-2">Output</p>

            {/* Audio — render all variations for music-gen */}
            {isAudio && (
              <div className="space-y-2">
                {Array.from({ length: clipCount }, (_, i) => {
                  const url = job.capabilitySlug === "music-gen"
                    ? `/music-gen/jobs/${job.id}/output?clip=${i}`
                    : `/${job.capabilitySlug}/jobs/${job.id}/output`
                  const label = clipCount > 1 ? `Variation ${i + 1}` : null
                  return (
                    <div key={i} className="bg-white/[0.06] rounded-md p-3">
                      {label && <p className="text-[11px] text-text-muted opacity-70 mb-1.5">{label}</p>}
                      <audio controls src={url} className="w-full h-8" />
                    </div>
                  )
                })}
              </div>
            )}

            {isImage && (
              <img src={`/${job.capabilitySlug}/jobs/${job.id}/output`} alt="Generated output" className="max-w-full rounded-lg" />
            )}

            {isVideo && (
              <video controls src={`/${job.capabilitySlug}/jobs/${job.id}/output`} className="max-w-full rounded-lg" />
            )}

            <a href={`/${job.capabilitySlug}/jobs/${job.id}/output`} download className="inline-block mt-2 text-xs text-accent-teal hover:underline">
              <i className="fa-solid fa-download mr-1" />Download output
            </a>
          </div>
        </>
      )}

      {/* Job logs */}
      {logs.length > 0 && (
        <>
          <Separator />
          <div>
            <p className="text-sm font-medium mb-2">Logs ({logs.length})</p>
            <div className="bg-surface-base rounded-lg p-2 max-h-48 overflow-auto space-y-px">
              {logs.map((log, i) => (
                <div key={i} className="flex gap-2 text-xs font-mono px-1 py-0.5">
                  <span className="text-white/40 w-[82px] shrink-0">
                    {new Date(log.timestamp).toLocaleTimeString("en-US", { hour12: false, fractionalSecondDigits: 3 })}
                  </span>
                  <span className={log.isError ? "text-accent-red" : "text-text-muted"}>{log.message}</span>
                </div>
              ))}
            </div>
          </div>
        </>
      )}
    </div>
  )
}

function Row({ label, value, mono }: { label: string; value: string; mono?: boolean }) {
  return (
    <div className="flex justify-between">
      <span className="text-text-muted">{label}</span>
      <span className={mono ? "font-mono text-xs" : ""}>{value}</span>
    </div>
  )
}

function formatJson(s: string): string {
  try { return JSON.stringify(JSON.parse(s), null, 2) }
  catch { return s }
}
