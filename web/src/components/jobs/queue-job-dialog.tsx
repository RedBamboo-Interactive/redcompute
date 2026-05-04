import { useState, useEffect } from "react"
import { useNavigate } from "react-router-dom"
import { Dialog, DialogContent, DialogHeader, DialogTitle, DialogDescription, DialogClose } from "@/components/ui/dialog"
import { Button } from "@/components/ui/button"
import { Input } from "@/components/ui/input"
import { Switch } from "@/components/ui/switch"
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
  const [selectedSlug, setSelectedSlug] = useState(defaultSlug || "")
  const [params, setParams] = useState<Record<string, ParameterSchema>>({})
  const [values, setValues] = useState<Record<string, unknown>>({})
  const [submitting, setSubmitting] = useState(false)
  const [error, setError] = useState<string | null>(null)

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

  useEffect(() => {
    if (open) {
      setError(null)
      setSubmitting(false)
      if (defaultSlug) {
        setSelectedSlug(defaultSlug)
      } else if (!selectedSlug && capabilities.length > 0) {
        const running = capabilities.find(c => c.status === "Running")
        if (running) setSelectedSlug(running.slug)
      }
    }
  }, [open, capabilities, selectedSlug, defaultSlug])

  async function submit() {
    if (!selectedSlug) return
    setSubmitting(true)
    setError(null)
    try {
      await api.post(`/${selectedSlug}/generate?async=true`, values)
      onOpenChange(false)
      navigate("/jobs")
    } catch (err) {
      setError(err instanceof Error ? err.message : "Unknown error")
    } finally {
      setSubmitting(false)
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="bg-surface-elevated border-border-subtle max-w-md">
        <DialogHeader>
          <DialogTitle>Queue Job</DialogTitle>
          <DialogDescription>Submit a new job to a capability</DialogDescription>
        </DialogHeader>

        <div className="space-y-4 mt-2">
          <div>
            <label className="text-xs text-text-muted block mb-1">Capability</label>
            <select
              value={selectedSlug}
              onChange={e => { setSelectedSlug(e.target.value); setParams({}); setValues({}); setError(null) }}
              className="w-full bg-surface-base border border-border-subtle rounded-lg px-3 py-2 text-sm"
            >
              <option value="">Select...</option>
              {capabilities.filter(c => c.status === "Running").map(c => (
                <option key={c.slug} value={c.slug}>{c.displayName}</option>
              ))}
            </select>
          </div>

          {Object.entries(params).map(([key, schema]) => (
            <div key={key}>
              <label className="text-xs text-text-muted block mb-1">
                {key}{schema.required && <span className="text-accent-red"> *</span>}
                {schema.description && <span className="opacity-60 ml-1">— {schema.description}</span>}
              </label>
              {schema.type === "boolean" ? (
                <Switch
                  checked={!!values[key]}
                  onCheckedChange={checked => setValues(prev => ({ ...prev, [key]: checked }))}
                />
              ) : schema.enum ? (
                <select
                  value={String(values[key] ?? "")}
                  onChange={e => setValues(prev => ({ ...prev, [key]: e.target.value }))}
                  className="w-full bg-surface-base border border-border-subtle rounded-lg px-3 py-2 text-sm"
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
                  className="bg-surface-base border-border-subtle"
                />
              )}
            </div>
          ))}

          {error && (
            <div className="bg-accent-red/10 border border-accent-red/30 rounded-lg p-3">
              <p className="text-sm text-accent-red">{error}</p>
            </div>
          )}

          <div className="flex justify-end gap-2">
            <DialogClose render={<Button variant="ghost">Cancel</Button>} />
            <Button onClick={submit} disabled={!selectedSlug || submitting}>
              {submitting ? (
                <span className="flex items-center gap-2">
                  <i className="fa-solid fa-spinner fa-spin text-xs" />
                  Submitting...
                </span>
              ) : "Queue"}
            </Button>
          </div>
        </div>
      </DialogContent>
    </Dialog>
  )
}
