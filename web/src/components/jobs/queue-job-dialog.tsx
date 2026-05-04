import { useState, useEffect } from "react"
import { useNavigate } from "react-router-dom"
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogClose } from "@/components/ui/dialog"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
import { api } from "@/api/client"
import type { CapabilityStatus, ParameterSchema } from "@/api/types"

const capabilityIcons: Record<string, string> = {
  tts: "fa-solid fa-volume-high",
  stt: "fa-solid fa-microphone",
  "image-gen": "fa-solid fa-image",
  "music-gen": "fa-solid fa-music",
  llm: "fa-solid fa-brain",
  "video-gen": "fa-solid fa-video",
}

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
  const [params, setParams] = useState<Record<string, ParameterSchema>>({})
  const [values, setValues] = useState<Record<string, unknown>>({})
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    if (!open) return
    setError(null)
    setSubmitting(false)
    setParams({})
    setValues({})
    const initial = defaultSlug || runningCaps[0]?.slug || ""
    setSelectedSlug(initial)
  }, [open, defaultSlug, runningCaps.length])

  useEffect(() => {
    if (!open || !selectedSlug) return
    api.get<DiscoverResponse>("/discover").then(data => {
      const cap = data.capabilities.find(c => c.slug === selectedSlug)
      const generateEndpoint = cap?.endpoints.find(e => e.method === "POST" && e.path.endsWith("/generate"))
      if (generateEndpoint?.parameters) {
        setParams(generateEndpoint.parameters)
        const defaults: Record<string, unknown> = {}
        for (const [key, schema] of Object.entries(generateEndpoint.parameters)) {
          if (schema.default !== undefined) defaults[key] = schema.default
        }
        setValues(defaults)
      }
    }).catch(() => {})
  }, [open, selectedSlug])

  async function submit() {
    if (!selectedSlug) return
    setSubmitting(true)
    setError(null)
    try {
      const result = await api.post<{ jobId?: string }>(`/${selectedSlug}/generate?async=true`, values)
      onOpenChange(false)
      navigate("/jobs", result?.jobId ? { state: { focusJobId: result.jobId } } : undefined)
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
            <i className={`${capabilityIcons[selectedSlug] || "fa-solid fa-cube"} text-base text-text-muted`} />
            {title}
          </DialogTitle>
          <DialogDescription className="sr-only">Configure and submit a job</DialogDescription>
        </DialogHeader>

        <div className="space-y-5 mt-1">
          {Object.entries(params).map(([key, schema]) => (
            <div key={key}>
              <div className="mb-1.5">
                <span className="text-[13px] font-medium text-white">
                  {formatLabel(key)}
                  {schema.required && <span className="text-accent-red ml-0.5">*</span>}
                </span>
                {schema.description && (
                  <p className="text-[11px] text-text-muted mt-0.5 leading-relaxed">{schema.description}</p>
                )}
              </div>
              {schema.type === "boolean" ? (
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
            <Button onClick={submit} disabled={!selectedSlug || submitting}>
              {submitting ? (
                <span className="flex items-center gap-2">
                  <i className="fa-solid fa-spinner fa-spin text-xs" />
                  Submitting...
                </span>
              ) : "Generate"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
