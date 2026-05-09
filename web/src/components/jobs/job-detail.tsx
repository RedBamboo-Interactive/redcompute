import { useEffect, useState } from "react"
import { Badge, Card, CardContent, Separator } from "@redbamboo/ui"
import { api } from "@/api/client"
import { authUrl } from "@/api/auth"
import type { JobRecord, LogEntry } from "@/api/types"
import { AudioPlayer } from "./audio-player"
import { ImageLightbox } from "./image-lightbox"
import { AiSessionDetail } from "./ai-session-detail"

const statusBadgeColor: Record<string, string> = {
  Queued: "bg-accent-gold/20 text-accent-gold border-accent-gold/30",
  Running: "bg-accent-gold/20 text-accent-gold border-accent-gold/30",
  Completed: "bg-accent-teal/20 text-accent-teal border-accent-teal/30",
  Failed: "bg-accent-red/20 text-accent-red border-accent-red/30",
  Cancelled: "bg-text-disabled/20 text-text-disabled border-text-disabled/30",
}

const capLabels: Record<string, string> = {
  tts: "Text-to-Speech",
  stt: "Speech-to-Text",
  "image-gen": "Image Generation",
  "music-gen": "Music Generation",
  "video-gen": "Video Generation",
  llm: "LLM",
  "ai-session": "AI Session",
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

interface ParsedInput {
  prompt: string | null
  extras: Record<string, unknown>
}

function parseInput(json: string): ParsedInput {
  try {
    const obj = JSON.parse(json) as Record<string, unknown>
    const prompt = (typeof obj.prompt === "string" ? obj.prompt : null)
      || (typeof obj.text === "string" ? obj.text : null)
    const extras: Record<string, unknown> = {}
    for (const [k, v] of Object.entries(obj)) {
      if (k !== "prompt" && k !== "text") extras[k] = v
    }
    return { prompt, extras }
  } catch {
    return { prompt: null, extras: {} }
  }
}

function parseClipTitles(resultJson?: string): string[] {
  if (!resultJson) return []
  try {
    const obj = JSON.parse(resultJson) as { clips?: { Title?: string }[] }
    return (obj.clips || []).map(c => c.Title || "")
  } catch {
    return []
  }
}

export function JobDetail({ job }: { job: JobRecord }) {
  if (job.capabilitySlug === "ai-session") {
    return <AiSessionDetail job={job} />
  }
  return <MediaJobDetail job={job} />
}

function MediaJobDetail({ job }: { job: JobRecord }) {
  const [logs, setLogs] = useState<LogEntry[]>([])
  const [clipCount, setClipCount] = useState(1)
  const [lightbox, setLightbox] = useState(false)
  const [showParams, setShowParams] = useState(false)

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
          const res = await fetch(authUrl(`/music-gen/jobs/${job.id}/output?clip=${i}`), {
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
  const outputUrl = `/${job.capabilitySlug}/jobs/${job.id}/output`

  const { prompt, extras } = job.inputJson ? parseInput(job.inputJson) : { prompt: null, extras: {} }
  const hasExtras = Object.keys(extras).length > 0
  const clipTitles = parseClipTitles(job.resultJson)

  return (
    <div className="space-y-4">
      {/* Header */}
      <div className="flex items-center gap-3">
        <h2 className="text-lg font-medium">{job.name || capLabels[job.capabilitySlug] || job.capabilitySlug}</h2>
        <Badge variant="outline" className={statusBadgeColor[job.status]}>{job.status}</Badge>
        {job.durationMs != null && (
          <span className="text-xs text-text-muted ml-auto">{formatDuration(job.durationMs)}</span>
        )}
      </div>

      {job.rationale && (
        <p className="text-sm text-text-muted italic">{job.rationale}</p>
      )}

      {/* Prompt — the most important context */}
      {prompt && (
        <div className="border-l-2 border-accent-teal/40 pl-3.5 py-0.5">
          <p className="text-sm font-serif leading-relaxed text-text-primary">{prompt}</p>
        </div>
      )}

      {/* TTS extras inline */}
      {job.capabilitySlug === "tts" && !!(extras.voice || extras.language || extras.emotion) && (
        <div className="flex flex-wrap gap-2">
          {extras.voice != null && <ParamChip label="Voice" value={String(extras.voice)} />}
          {extras.language != null && <ParamChip label="Lang" value={String(extras.language)} />}
          {extras.emotion != null && String(extras.emotion) !== "neutral" && <ParamChip label="Emotion" value={String(extras.emotion)} />}
          {extras.speed != null && Number(extras.speed) !== 1 && <ParamChip label="Speed" value={`${extras.speed}×`} />}
        </div>
      )}

      {/* Music-gen extras inline */}
      {job.capabilitySlug === "music-gen" && !!(extras.style || extras.instrumental != null) && (
        <div className="flex flex-wrap gap-2">
          {extras.style != null && <ParamChip label="Style" value={String(extras.style)} />}
          {extras.instrumental === true && <ParamChip label="" value="Instrumental" />}
        </div>
      )}

      {/* Image-gen extras inline */}
      {job.capabilitySlug === "image-gen" && extras.workflow != null && (
        <div className="flex flex-wrap gap-2">
          <ParamChip label="Workflow" value={String(extras.workflow)} />
        </div>
      )}

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

      {/* Output — front and center */}
      {hasOutput && (
        <div>
          {isAudio && (
            <div className="space-y-2">
              {Array.from({ length: clipCount }, (_, i) => {
                const url = job.capabilitySlug === "music-gen"
                  ? authUrl(`/music-gen/jobs/${job.id}/output?clip=${i}`)
                  : authUrl(outputUrl)
                const label = clipTitles[i] || (clipCount > 1 ? `Variation ${i + 1}` : null)
                return <AudioPlayer key={i} src={url} label={label} />
              })}
            </div>
          )}

          {isImage && (
            <>
              <img
                src={authUrl(outputUrl)}
                alt="Generated output"
                className="max-w-full rounded-lg cursor-pointer hover:brightness-105 transition-all"
                onClick={() => setLightbox(true)}
              />
              {lightbox && (
                <ImageLightbox src={authUrl(outputUrl)} alt="Generated output" onClose={() => setLightbox(false)} />
              )}
            </>
          )}

          {isVideo && (
            <video controls src={authUrl(outputUrl)} className="max-w-full rounded-lg" />
          )}

          {/* Actions */}
          <div className="flex items-center gap-3 mt-2.5">
            <a href={authUrl(outputUrl)} download className="inline-flex items-center gap-1.5 text-xs text-accent-teal hover:text-accent-teal/80 transition-colors">
              <i className="fa-solid fa-download" />Download
            </a>
            <a href={authUrl(outputUrl)} target="_blank" rel="noopener noreferrer" className="inline-flex items-center gap-1.5 text-xs text-text-muted hover:text-text-primary transition-colors">
              <i className="fa-solid fa-arrow-up-right-from-square" />Open
            </a>
            <button
              onClick={() => navigator.clipboard.writeText(new URL(outputUrl, window.location.origin).href)}
              className="inline-flex items-center gap-1.5 text-xs text-text-muted hover:text-text-primary transition-colors"
            >
              <i className="fa-solid fa-link" />Copy link
            </button>
            {job.outputSizeBytes != null && (
              <span className="text-[11px] text-text-disabled ml-auto">{formatBytes(job.outputSizeBytes)}</span>
            )}
          </div>
        </div>
      )}

      {/* Running progress */}
      {job.status === "Running" && job.progress != null && (
        <div>
          <div className="flex items-center justify-between text-xs text-text-muted mb-1">
            <span>Progress</span>
            <span>{Math.round(job.progress * 100)}%</span>
          </div>
          <div className="h-1.5 rounded-full bg-white/10">
            <div
              className="h-full rounded-full bg-accent-gold transition-all duration-500"
              style={{ width: `${Math.round(job.progress * 100)}%` }}
            />
          </div>
        </div>
      )}

      <Separator />

      {/* Metadata — secondary */}
      <div>
        <button
          onClick={() => setShowParams(!showParams)}
          className="flex items-center gap-2 text-sm text-text-muted hover:text-text-primary transition-colors w-full"
        >
          <i className={`fa-solid fa-chevron-right text-[10px] transition-transform ${showParams ? "rotate-90" : ""}`} />
          <span className="font-medium">Details</span>
          <span className="text-xs text-text-disabled">
            {job.providerName} · {new Date(job.queuedAt).toLocaleString()}
          </span>
        </button>

        {showParams && (
          <Card className="bg-surface-base border-border-subtle mt-2">
            <CardContent className="p-4 space-y-2 text-sm">
              <Row label="ID" value={job.id} mono />
              <Row label="Capability" value={capLabels[job.capabilitySlug] || job.capabilitySlug} />
              <Row label="Provider" value={job.providerName} />
              <Row label="Queued" value={new Date(job.queuedAt).toLocaleString()} />
              {job.startedAt && <Row label="Started" value={new Date(job.startedAt).toLocaleString()} />}
              {job.completedAt && <Row label="Completed" value={new Date(job.completedAt).toLocaleString()} />}
              {job.durationMs != null && <Row label="Duration" value={formatDuration(job.durationMs)} />}
              {job.callerInfo && <Row label="Caller" value={job.callerInfo} />}
              {job.outputSizeBytes != null && <Row label="Output Size" value={formatBytes(job.outputSizeBytes)} />}

              {hasExtras && (
                <>
                  <Separator />
                  <p className="text-xs font-medium text-text-muted pt-1">Parameters</p>
                  <pre className="text-xs font-mono bg-surface-deep rounded-lg p-3 overflow-auto max-h-48 text-text-muted">
                    {JSON.stringify(extras, null, 2)}
                  </pre>
                </>
              )}
            </CardContent>
          </Card>
        )}
      </div>

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

function ParamChip({ label, value }: { label: string; value: string }) {
  return (
    <span className="inline-flex items-center gap-1.5 text-[11px] bg-white/[0.06] rounded-md px-2 py-1">
      <span className="text-text-disabled">{label}</span>
      <span className="text-text-primary">{value}</span>
    </span>
  )
}
