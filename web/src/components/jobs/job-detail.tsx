import { useEffect, useState, useCallback } from "react"
import { AudioPlayer, ImageLightbox } from "@redbamboo/ui"
import { api } from "@/api/client"
import { authUrl } from "@/api/auth"
import type { CapabilityStatus, JobRecord } from "@/api/types"
import { JobDetailShell, ParamChip, formatBytes, parseInput } from "./job-detail-shell"
import { AiSessionDetail } from "./ai-session-detail"

const terminalStatuses = new Set(["Completed", "Failed", "Cancelled"])

function parseClipTitles(resultJson?: string): string[] {
  if (!resultJson) return []
  try {
    const obj = JSON.parse(resultJson) as { clips?: { Title?: string }[] }
    return (obj.clips || []).map(c => c.Title || "")
  } catch {
    return []
  }
}

export function JobDetail({ job, onRerun, capability }: { job: JobRecord; onRerun?: (newJobId: string) => void; capability?: CapabilityStatus }) {
  if (job.capabilitySlug === "ai-session") {
    return <AiSessionDetail job={job} capability={capability} />
  }
  return <MediaJobDetail job={job} onRerun={onRerun} capability={capability} />
}

interface SttOutput {
  text: string
  language?: string
  segments?: { start: number; end: number; text: string }[]
}

function MediaJobDetail({ job, onRerun, capability }: { job: JobRecord; onRerun?: (newJobId: string) => void; capability?: CapabilityStatus }) {
  const [clipCount, setClipCount] = useState(1)
  const [lightbox, setLightbox] = useState(false)
  const [rerunning, setRerunning] = useState(false)
  const [sttOutput, setSttOutput] = useState<SttOutput | null>(null)

  const canRerun = !!capability?.rerunnable && terminalStatuses.has(job.status)

  const handleRerun = useCallback(async () => {
    setRerunning(true)
    try {
      const data = await api.post<{ jobId: string }>(`/jobs/${job.id}/rerun`)
      if (data?.jobId) onRerun?.(data.jobId)
    } catch (err) {
      console.error("Rerun failed:", err)
    } finally {
      setRerunning(false)
    }
  }, [job.id, onRerun])

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

  useEffect(() => {
    if (job.capabilitySlug !== "stt" || job.status !== "Completed" || !job.outputContentType?.includes("json")) return
    setSttOutput(null)
    fetch(authUrl(`/${job.capabilitySlug}/jobs/${job.id}/output`))
      .then(r => r.ok ? r.json() : null)
      .then(data => { if (data && typeof data.text === "string") setSttOutput(data) })
      .catch(() => {})
  }, [job.id, job.capabilitySlug, job.status, job.outputContentType])

  const isAudio = job.outputContentType?.startsWith("audio/")
  const isImage = job.outputContentType?.startsWith("image/")
  const isVideo = job.outputContentType?.startsWith("video/")
  const isJson = !!job.outputContentType?.startsWith("application/json")
  const hasOutput = job.status === "Completed" && job.outputContentType
  const outputUrl = `/${job.capabilitySlug}/jobs/${job.id}/output`

  const { extras } = job.inputJson ? parseInput(job.inputJson) : { extras: {} as Record<string, unknown> }
  const clipTitles = parseClipTitles(job.resultJson)

  const chips = (
    <>
      {job.capabilitySlug === "tts" && (
        <>
          {extras.voice != null && <ParamChip label="Voice" value={String(extras.voice)} />}
          {extras.language != null && <ParamChip label="Lang" value={String(extras.language)} />}
          {extras.emotion != null && String(extras.emotion) !== "neutral" && <ParamChip label="Emotion" value={String(extras.emotion)} />}
          {extras.speed != null && Number(extras.speed) !== 1 && <ParamChip label="Speed" value={`${extras.speed}×`} />}
        </>
      )}
      {job.capabilitySlug === "music-gen" && (
        <>
          {extras.style != null && <ParamChip label="Style" value={String(extras.style)} />}
          {extras.instrumental === true && <ParamChip label="" value="Instrumental" />}
        </>
      )}
      {job.capabilitySlug === "image-gen" && extras.workflow != null && (
        <ParamChip label="Workflow" value={String(extras.workflow)} />
      )}
      {job.capabilitySlug === "stt" && (
        <>
          {extras.language != null && <ParamChip label="Lang" value={String(extras.language)} />}
          {extras.task != null && String(extras.task) !== "transcribe" && <ParamChip label="Task" value={String(extras.task)} />}
          {extras.word_timestamps === true && <ParamChip label="" value="Word timestamps" />}
          {extras.vad_filter === false && <ParamChip label="VAD" value="off" />}
        </>
      )}
    </>
  )

  return (
    <JobDetailShell
      job={job}
      capability={capability}
      actions={canRerun ? (
        <button
          onClick={handleRerun}
          disabled={rerunning}
          className="inline-flex items-center gap-1.5 text-xs px-2.5 py-1 rounded-md transition-colors bg-overlay-6 text-text-muted hover:bg-overlay-10 hover:text-text-primary disabled:opacity-50"
        >
          <i className={`fa-solid ${rerunning ? "fa-spinner fa-spin" : "fa-rotate-right"} text-[11px]`} />
          {rerunning ? "Rerunning..." : "Rerun"}
        </button>
      ) : undefined}
      chips={chips}
    >
      {/* Output */}
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

          {isJson && sttOutput && (
            <div className="bg-surface-base rounded-lg p-4">
              <div className="flex items-start justify-between gap-3 mb-3">
                <span className="text-xs font-medium text-text-muted">Transcription</span>
                {sttOutput.text && (
                  <button
                    onClick={() => navigator.clipboard.writeText(sttOutput.text)}
                    className="inline-flex items-center gap-1.5 text-[11px] text-text-disabled hover:text-text-primary transition-colors shrink-0"
                  >
                    <i className="fa-solid fa-copy" />Copy text
                  </button>
                )}
              </div>
              {sttOutput.text
                ? <p className="text-sm leading-relaxed text-text-primary whitespace-pre-wrap">{sttOutput.text}</p>
                : <p className="text-sm italic text-text-disabled">No speech detected</p>
              }
              {(sttOutput.language || sttOutput.segments) && (
                <div className="mt-3 flex items-center gap-3 text-[11px] text-text-disabled">
                  {sttOutput.language && <span>Detected: {sttOutput.language}</span>}
                  {sttOutput.segments && (
                    <>
                      <span className="text-text-disabled-a50">&middot;</span>
                      <span>{sttOutput.segments.length} segment{sttOutput.segments.length !== 1 ? "s" : ""}</span>
                    </>
                  )}
                </div>
              )}
            </div>
          )}

          <div className="flex items-center gap-3 mt-2.5">
            <a href={authUrl(outputUrl)} download className="inline-flex items-center gap-1.5 text-xs text-accent-teal hover:text-accent-teal-a80 transition-colors">
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

      {/* Progress */}
      {job.status === "Running" && job.progress != null && (
        <div>
          <div className="flex items-center justify-between text-xs text-text-muted mb-1">
            <span>Progress</span>
            <span>{Math.round(job.progress * 100)}%</span>
          </div>
          <div className="h-1.5 rounded-full bg-overlay-10">
            <div
              className="h-full rounded-full bg-accent-gold transition-all duration-500"
              style={{ width: `${Math.round(job.progress * 100)}%` }}
            />
          </div>
        </div>
      )}
    </JobDetailShell>
  )
}
