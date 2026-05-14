import { useState, useEffect, useRef, useCallback } from "react"
import { useNavigate } from "react-router-dom"
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogClose, Button, Input, Switch } from "@redbamboo/ui"
import { api } from "@/api/client"
import type { CapabilityStatus, ParameterSchema } from "@/api/types"

interface DiscoverResponse {
  capabilities: {
    slug: string
    endpoints: { method: string; path: string; parameters?: Record<string, ParameterSchema> }[]
  }[]
}

export function QueueJobDialog({ open, onOpenChange, capabilities, defaultSlug }: {
  open: boolean
  onOpenChange: (open: boolean) => void
  capabilities: CapabilityStatus[]
  defaultSlug?: string
}) {
  const navigate = useNavigate()
  const runningCaps = capabilities.filter(c => c.status === "Running")
  const [selectedSlug, setSelectedSlug] = useState("")
  const [endpointPath, setEndpointPath] = useState("")
  const [params, setParams] = useState<Record<string, ParameterSchema>>({})
  const [values, setValues] = useState<Record<string, unknown>>({})
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [recording, setRecording] = useState(false)
  const [recordingTime, setRecordingTime] = useState(0)
  const mediaRecorderRef = useRef<MediaRecorder | null>(null)
  const chunksRef = useRef<Blob[]>([])
  const timerRef = useRef<ReturnType<typeof setInterval> | null>(null)

  const stopRecording = useCallback(() => {
    if (mediaRecorderRef.current?.state === "recording") {
      mediaRecorderRef.current.stop()
    }
    if (timerRef.current) {
      clearInterval(timerRef.current)
      timerRef.current = null
    }
    setRecording(false)
  }, [])

  useEffect(() => {
    if (!open) stopRecording()
  }, [open, stopRecording])

  useEffect(() => {
    if (!open) return
    setError(null)
    setSubmitting(false)
    setParams({})
    setValues({})
    setEndpointPath("")
    const initial = defaultSlug || runningCaps[0]?.slug || ""
    setSelectedSlug(initial)
  }, [open, defaultSlug, runningCaps.length])

  useEffect(() => {
    if (!open || !selectedSlug) return
    api.get<DiscoverResponse>("/discover").then(data => {
      const cap = data.capabilities.find(c => c.slug === selectedSlug)
      const primaryEndpoint = cap?.endpoints.find(e => e.method === "POST" && e.parameters && Object.keys(e.parameters).length > 0)
      if (primaryEndpoint?.parameters) {
        setEndpointPath(primaryEndpoint.path)
        const visible: Record<string, ParameterSchema> = {}
        for (const [key, schema] of Object.entries(primaryEndpoint.parameters)) {
          if (key.endsWith("_base64") || key.endsWith("_content_type")) continue
          visible[key] = schema
        }
        setParams(visible)
        const defaults: Record<string, unknown> = {}
        for (const [key, schema] of Object.entries(visible)) {
          if (schema.default !== undefined) defaults[key] = schema.default
        }
        setValues(defaults)
      }
    }).catch(() => {})
  }, [open, selectedSlug])

  async function submit() {
    if (!selectedSlug || !endpointPath) return
    setSubmitting(true)
    setError(null)
    try {
      const body: Record<string, unknown> = {}
      for (const [key, val] of Object.entries(values)) {
        if (val instanceof File) {
          const buf = await val.arrayBuffer()
          const bytes = new Uint8Array(buf)
          let binary = ""
          for (let i = 0; i < bytes.length; i++) binary += String.fromCharCode(bytes[i])
          body[key + "_base64"] = btoa(binary)
          body[key + "_content_type"] = val.type || "audio/wav"
        } else {
          body[key] = val
        }
      }
      body.rationale = "Queued from dashboard"
      const result = await api.post<{ jobId?: string; sessionId?: string }>(endpointPath + "?async=true", body)
      onOpenChange(false)
      if (selectedSlug === "ai-session") {
        navigate("/claude")
      } else {
        navigate("/jobs", result?.jobId ? { state: { focusJobId: result.jobId } } : undefined)
      }
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unknown error")
    } finally {
      setSubmitting(false)
    }
  }

  const selectedCap = capabilities.find(c => c.slug === selectedSlug)
  const title = selectedCap?.displayName ?? selectedSlug

  function formatLabel(key: string): string {
    return key.replace(/_/g, " ").replace(/\b\w/g, c => c.toUpperCase())
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="bg-surface-elevated border-border-subtle max-w-lg w-[calc(100vw-2rem)]">
        <DialogHeader>
          <DialogTitle className="text-lg flex items-center gap-2.5">
            <i className={`${selectedCap?.icon || "fa-solid fa-cube"} text-base text-text-muted`} />
            {title}
          </DialogTitle>
          <DialogDescription className="sr-only">Configure and submit a job</DialogDescription>
        </DialogHeader>

        <div className="space-y-5 mt-1">
          {Object.entries(params).map(([key, schema]) => (
            <div key={key}>
              <div className="mb-1.5">
                <span className="text-[13px] font-medium text-contrast">
                  {formatLabel(key)}
                  {schema.required && <span className="text-accent-red ml-0.5">*</span>}
                </span>
                {schema.description && (
                  <p className="text-[11px] text-text-muted mt-0.5 leading-relaxed">{schema.description}</p>
                )}
              </div>
              {schema.type === "file" ? (
                <div className="space-y-2">
                  <div className="flex items-center gap-2">
                    <button
                      type="button"
                      onClick={async () => {
                        if (recording) {
                          stopRecording()
                          return
                        }
                        try {
                          const stream = await navigator.mediaDevices.getUserMedia({ audio: true })
                          chunksRef.current = []
                          const mr = new MediaRecorder(stream, { mimeType: MediaRecorder.isTypeSupported("audio/webm;codecs=opus") ? "audio/webm;codecs=opus" : "audio/webm" })
                          mediaRecorderRef.current = mr
                          mr.ondataavailable = e => { if (e.data.size > 0) chunksRef.current.push(e.data) }
                          mr.onstop = () => {
                            stream.getTracks().forEach(t => t.stop())
                            const blob = new Blob(chunksRef.current, { type: mr.mimeType })
                            const file = new File([blob], "recording.webm", { type: mr.mimeType })
                            setValues(prev => ({ ...prev, [key]: file }))
                          }
                          mr.start(250)
                          setRecording(true)
                          setRecordingTime(0)
                          timerRef.current = setInterval(() => setRecordingTime(t => t + 1), 1000)
                        } catch {
                          setError("Microphone access denied")
                        }
                      }}
                      className={`flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-medium transition-colors ${
                        recording
                          ? "bg-accent-red/20 text-accent-red border border-accent-red/40"
                          : "bg-surface-base border border-border-subtle text-text-secondary hover:text-contrast"
                      }`}
                    >
                      <i className={`fa-solid ${recording ? "fa-stop" : "fa-microphone"} text-xs`} />
                      {recording
                        ? `Stop (${Math.floor(recordingTime / 60)}:${String(recordingTime % 60).padStart(2, "0")})`
                        : "Record"}
                    </button>
                    <span className="text-text-muted text-xs">or</span>
                    <label className="flex items-center gap-2 px-3 py-2 rounded-lg text-sm font-medium bg-surface-base border border-border-subtle text-text-secondary hover:text-contrast cursor-pointer transition-colors">
                      <i className="fa-solid fa-file-audio text-xs" />
                      Upload file
                      <input
                        type="file"
                        accept="audio/*"
                        className="hidden"
                        onChange={e => {
                          const file = e.target.files?.[0]
                          if (file) setValues(prev => ({ ...prev, [key]: file }))
                        }}
                      />
                    </label>
                  </div>
                  {values[key] instanceof File && !recording && (
                    <div className="flex items-center gap-2 text-xs text-accent-teal">
                      <i className="fa-solid fa-check" />
                      {(values[key] as File).name} ({Math.round((values[key] as File).size / 1024)}KB)
                    </div>
                  )}
                </div>
              ) : schema.type === "boolean" ? (
                <Switch
                  checked={!!values[key]}
                  onCheckedChange={checked => setValues(prev => ({ ...prev, [key]: checked }))}
                />
              ) : schema.enum ? (
                <select
                  value={String(values[key] ?? "")}
                  onChange={e => setValues(prev => ({ ...prev, [key]: e.target.value }))}
                  className="w-full bg-surface-base border border-border-subtle rounded-lg px-3 py-2.5 text-sm"
                >
                  {schema.enum.map(v => <option key={v} value={v}>{v}</option>)}
                </select>
              ) : (
                <Input
                  type={schema.type === "number" || schema.type === "integer" ? "number" : "text"}
                  value={String(values[key] ?? "")}
                  onChange={e => {
                    const val = schema.type === "number" ? parseFloat(e.target.value) :
                                schema.type === "integer" ? parseInt(e.target.value) : e.target.value
                    setValues(prev => ({ ...prev, [key]: val }))
                  }}
                  placeholder={schema.default !== undefined ? String(schema.default) : undefined}
                  className="bg-surface-base border-border-subtle py-2.5"
                />
              )}
            </div>
          ))}

          {error && (
            <div className="bg-accent-red/10 border border-accent-red/30 rounded-lg p-3">
              <p className="text-sm text-accent-red">{error}</p>
            </div>
          )}

          <div className="flex justify-end gap-2 pt-2">
            <DialogClose render={<Button variant="ghost">Cancel</Button>} />
            <Button onClick={submit} disabled={!selectedSlug || !endpointPath || submitting}>
              {submitting ? (
                <span className="flex items-center gap-2">
                  <i className="fa-solid fa-spinner fa-spin text-xs" />
                  Submitting...
                </span>
              ) : selectedSlug === "stt" ? "Transcribe" : "Generate"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
