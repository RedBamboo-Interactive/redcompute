import { useState, useEffect } from "react"
import { QRCodeSVG } from "qrcode.react"
import type { Settings } from "@/api/types"
import { api } from "@/api/client"

interface DiscoverEndpoint { method: string; path: string; description: string }
interface DiscoverResponse {
  capabilities: { slug: string; endpoints?: DiscoverEndpoint[] }[]
  management: { endpoints: DiscoverEndpoint[] }
}

const inputClass = "bg-surface-deep border border-white/10 rounded px-2 py-1 outline-none text-white text-[13px] font-mono focus:border-white/30"
const inputSmClass = "bg-surface-deep border border-white/10 rounded px-2 py-1 outline-none text-white text-[11px] font-mono min-w-0 focus:border-white/30"

export function SettingsPage({ settings, saving, onUpdateGeneral, onUpdateCapability, onUpdateProvider }: {
  settings: Settings | null
  saving: boolean
  onUpdateGeneral: (updates: Record<string, unknown>) => void
  onUpdateCapability: (slug: string, updates: { enabled?: boolean; activeProvider?: string }) => void
  onUpdateProvider: (slug: string, providerName: string, updates: Record<string, unknown>) => Promise<void>
}) {
  const [endpoints, setEndpoints] = useState<DiscoverEndpoint[]>([])
  const [port, setPort] = useState("")
  const [tunnelToken, setTunnelToken] = useState("")
  const [accessToken, setAccessToken] = useState("")
  const [hostname, setHostname] = useState("")
  const [cloudflaredPath, setCloudflaredPath] = useState("")
  const [showTunnelToken, setShowTunnelToken] = useState(false)
  const [showAccessToken, setShowAccessToken] = useState(false)

  useEffect(() => {
    api.get<DiscoverResponse>("/discover").then(data => {
      const all: DiscoverEndpoint[] = []
      for (const cap of data.capabilities)
        if (cap.endpoints) all.push(...cap.endpoints)
      all.push(...data.management.endpoints)
      setEndpoints(all)
    }).catch(() => {})
  }, [])

  if (!settings) return <p className="text-text-muted p-8">Loading settings...</p>

  const tunnel = settings.tunnel
  const statusColor = {
    Stopped: "#727680",
    Starting: "#FFB74D",
    Running: "#43A25A",
    Error: "#FF5252",
  }[tunnel.status] ?? "#727680"

  return (
    <div className="p-4 md:p-6 max-w-[600px]">
      <h1 className="text-[20px] font-semibold text-white opacity-95 mb-5">Settings</h1>

      {/* GENERAL */}
      <SectionLabel>GENERAL</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4">
        <FieldRow label="API Port">
          <input type="number" className={`${inputClass} w-24`}
            placeholder={String(settings.apiPort)} value={port}
            onChange={e => setPort(e.target.value)}
            onBlur={() => { if (port && port !== String(settings.apiPort)) { onUpdateGeneral({ apiPort: Number(port) }); setPort("") } }} />
        </FieldRow>
      </div>

      {/* REMOTE ACCESS */}
      <SectionLabel>REMOTE ACCESS</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4">
        <FieldRow label="Status">
          <div className="flex items-center gap-2">
            <span className="inline-block w-2 h-2 rounded-full" style={{ backgroundColor: statusColor }} />
            <span className="text-white text-[11px]">{tunnel.status}</span>
            {tunnel.status === "Running" && tunnel.hostname && (
              <button
                className="text-[11px] text-text-muted hover:text-white font-mono ml-1 transition-colors"
                onClick={() => navigator.clipboard.writeText(`https://${tunnel.hostname}`)}
                title="Copy URL"
              >
                https://{tunnel.hostname}
              </button>
            )}
          </div>
        </FieldRow>
        {tunnel.error && (
          <FieldRow label="">
            <span className="text-[11px] text-red-400">{tunnel.error}</span>
          </FieldRow>
        )}
        <FieldRow label="Enabled">
          <Toggle enabled={tunnel.enabled} disabled={saving}
            onToggle={() => onUpdateGeneral({ tunnelEnabled: !tunnel.enabled })} />
        </FieldRow>
        <FieldRow label="Tunnel Token">
          <div className="flex items-center gap-1">
            <input
              type={showTunnelToken ? "text" : "password"}
              className={`${inputSmClass} flex-1`}
              placeholder={tunnel.tunnelToken ? "***" : "Paste from Cloudflare dashboard"}
              value={tunnelToken}
              onChange={e => setTunnelToken(e.target.value)}
              onBlur={() => { if (tunnelToken) { onUpdateGeneral({ tunnelToken }); setTunnelToken("") } }}
            />
            <button className="text-[10px] text-text-muted hover:text-white shrink-0 transition-colors"
              onClick={() => setShowTunnelToken(!showTunnelToken)}>{showTunnelToken ? "hide" : "show"}</button>
          </div>
        </FieldRow>
        <FieldRow label="Access Token">
          <div className="flex items-center gap-1">
            <input
              type={showAccessToken ? "text" : "password"}
              className={`${inputSmClass} flex-1`}
              placeholder={tunnel.accessToken ? "***" : "Auto-generated on enable"}
              value={accessToken}
              onChange={e => setAccessToken(e.target.value)}
              onBlur={() => { if (accessToken) { onUpdateGeneral({ tunnelAccessToken: accessToken }); setAccessToken("") } }}
            />
            <button className="text-[10px] text-text-muted hover:text-white shrink-0 transition-colors"
              onClick={() => setShowAccessToken(!showAccessToken)}>{showAccessToken ? "hide" : "show"}</button>
          </div>
        </FieldRow>
        <FieldRow label="Hostname">
          <input
            type="text"
            className={`${inputSmClass} w-full`}
            placeholder={tunnel.hostname ?? "redcompute.example.com"}
            value={hostname}
            onChange={e => setHostname(e.target.value)}
            onBlur={() => { if (hostname && hostname !== (tunnel.hostname ?? "")) { onUpdateGeneral({ tunnelHostname: hostname }); setHostname("") } }}
          />
        </FieldRow>
        <FieldRow label="Cloudflared Path">
          <input
            type="text"
            className={`${inputSmClass} w-full`}
            placeholder={tunnel.cloudflaredPath ?? "auto-detect"}
            value={cloudflaredPath}
            onChange={e => setCloudflaredPath(e.target.value)}
            onBlur={() => { if (cloudflaredPath) { onUpdateGeneral({ tunnelCloudflaredPath: cloudflaredPath }); setCloudflaredPath("") } }}
          />
        </FieldRow>
        {tunnel.status === "Running" && tunnel.hostname && tunnel.accessToken && tunnel.accessToken !== "***" && (
          <div className="mt-3 pt-3 border-t border-white/5 flex flex-col items-center gap-2">
            <p className="text-[11px] text-text-muted">Scan to connect from your phone</p>
            <QRCodeSVG
              value={`https://${tunnel.hostname}/#/?token=${tunnel.accessToken}`}
              size={160}
              bgColor="transparent"
              fgColor="#ffffff"
              level="M"
            />
          </div>
        )}
      </div>

      {/* CAPABILITIES */}
      <SectionLabel>CAPABILITIES</SectionLabel>
      {Object.entries(settings.capabilities).map(([slug, cap]) => (
        <CapabilityCard key={slug} slug={slug} cap={cap} saving={saving}
          onUpdateCapability={onUpdateCapability}
          onUpdateProvider={onUpdateProvider} />
      ))}

      {/* API ENDPOINTS */}
      <SectionLabel>API ENDPOINTS</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4 space-y-1">
        {endpoints.map((ep, i) => (
          <EndpointLine key={i} method={ep.method} path={ep.path} description={ep.description} />
        ))}
        {endpoints.length === 0 && <p className="text-[11px] text-text-muted">Loading...</p>}
      </div>
    </div>
  )
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return <p className="text-[11px] font-semibold text-text-muted mb-2 tracking-wide">{children}</p>
}

function FieldRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex flex-col gap-0.5 mb-2 md:flex-row md:gap-0 md:mb-1">
      <span className="text-[11px] text-text-muted md:w-[140px] md:shrink-0">{label}</span>
      <div className="flex-1">{children}</div>
    </div>
  )
}

function Toggle({ enabled, disabled, onToggle }: { enabled: boolean; disabled?: boolean; onToggle: () => void }) {
  return (
    <button
      className={`w-8 h-[18px] rounded-full relative transition-colors ${enabled ? "bg-[#43A25A]" : "bg-white/15"}`}
      disabled={disabled}
      onClick={onToggle}
    >
      <span className={`absolute top-[2px] w-[14px] h-[14px] rounded-full bg-white transition-all ${enabled ? "left-[15px]" : "left-[2px]"}`} />
    </button>
  )
}

const PROVIDER_FIELDS: { key: string; label: string; type?: "number" | "password" }[] = [
  { key: "backendPort", label: "Backend Port", type: "number" },
  { key: "wslDistro", label: "WSL Distro" },
  { key: "serverPath", label: "Server Path" },
  { key: "model", label: "Model" },
  { key: "venvPath", label: "Venv Path" },
  { key: "voicesBasePath", label: "Voices Path" },
  { key: "healthEndpoint", label: "Health Endpoint" },
  { key: "startupTimeoutSeconds", label: "Startup Timeout (s)", type: "number" },
  { key: "apiKey", label: "API Key", type: "password" },
]

type CapabilityEntry = Settings["capabilities"][string]

function CapabilityCard({ slug, cap, saving, onUpdateCapability, onUpdateProvider }: {
  slug: string
  cap: CapabilityEntry
  saving: boolean
  onUpdateCapability: (slug: string, updates: { enabled?: boolean; activeProvider?: string }) => void
  onUpdateProvider: (slug: string, providerName: string, updates: Record<string, unknown>) => Promise<void>
}) {
  const [edits, setEdits] = useState<Record<string, string>>({})
  const [showApiKey, setShowApiKey] = useState(false)
  const [lastProvider, setLastProvider] = useState(cap.activeProvider)

  const providerNames = Object.keys(cap.providers)
  const activeProvider = cap.activeProvider ?? providerNames[0]
  const provider = cap.providers[activeProvider] ?? null

  if (cap.activeProvider !== lastProvider) {
    setLastProvider(cap.activeProvider)
    setEdits({})
  }

  const saveField = async (key: string, raw: string) => {
    if (!raw) { setEdits(prev => { const next = { ...prev }; delete next[key]; return next }); return }
    const value = key === "backendPort" || key === "startupTimeoutSeconds" ? Number(raw) : raw
    await onUpdateProvider(slug, activeProvider, { [key]: value })
    setEdits(prev => { const next = { ...prev }; delete next[key]; return next })
  }

  return (
    <div className={`bg-surface-elevated rounded-lg p-4 mb-3 ${!cap.enabled ? "opacity-50" : ""}`}>
      <div className="flex items-center justify-between mb-2">
        <span className="text-[13px] font-semibold text-white">{slug}</span>
        <Toggle enabled={cap.enabled} disabled={saving}
          onToggle={() => onUpdateCapability(slug, { enabled: !cap.enabled })} />
      </div>

      {providerNames.length > 1 ? (
        <FieldRow label="Default Provider">
          <select
            className={`${inputSmClass} w-full`}
            value={activeProvider}
            disabled={saving}
            onChange={e => onUpdateCapability(slug, { activeProvider: e.target.value })}
          >
            {providerNames.map(n => <option key={n} value={n}>{n}</option>)}
          </select>
        </FieldRow>
      ) : (
        <FieldRow label="Provider">
          <span className="text-white text-[11px]">{activeProvider}</span>
        </FieldRow>
      )}
      {provider?.type != null && (
        <FieldRow label="Type">
          <span className="text-white text-[11px] font-mono">{String(provider.type)}</span>
        </FieldRow>
      )}

      {provider && PROVIDER_FIELDS.map(({ key, label, type }) => {
        const current = provider[key]
        if (current == null && !(key in edits)) return null

        const isPassword = type === "password"
        const displayValue = isPassword && current === "***" ? "" : String(current ?? "")
        const editing = key in edits

        return (
          <FieldRow key={key} label={label}>
            <div className="flex items-center gap-1">
              <input
                type={isPassword && !showApiKey ? "password" : type === "number" ? "number" : "text"}
                className={`${inputSmClass} flex-1 ${key === "serverPath" || key === "venvPath" || key === "voicesBasePath" ? "break-all" : ""}`}
                placeholder={displayValue || label}
                value={editing ? edits[key] : ""}
                disabled={saving}
                onChange={e => setEdits(prev => ({ ...prev, [key]: e.target.value }))}
                onBlur={() => { if (editing) saveField(key, edits[key]) }}
                onKeyDown={e => { if (e.key === "Enter" && editing) { e.currentTarget.blur() } }}
              />
              {isPassword && (
                <button className="text-[10px] text-text-muted hover:text-white shrink-0 transition-colors"
                  onClick={() => setShowApiKey(!showApiKey)}>{showApiKey ? "hide" : "show"}</button>
              )}
            </div>
          </FieldRow>
        )
      })}

      {provider?.extra != null && Object.keys(provider.extra as Record<string, unknown>).length > 0 && (
        <>
          {Object.entries(provider.extra as Record<string, unknown>).map(([key, value]) => {
            const editing = `extra.${key}` in edits
            return (
              <FieldRow key={key} label={key}>
                <input
                  type="text"
                  className={`${inputSmClass} w-full`}
                  placeholder={String(value ?? "")}
                  value={editing ? edits[`extra.${key}`] : ""}
                  disabled={saving}
                  onChange={e => setEdits(prev => ({ ...prev, [`extra.${key}`]: e.target.value }))}
                  onBlur={async () => {
                    const k = `extra.${key}`
                    if (k in edits) {
                      const raw = edits[k]
                      if (raw) {
                        await onUpdateProvider(slug, activeProvider, { extra: { [key]: raw } })
                      }
                      setEdits(prev => { const next = { ...prev }; delete next[k]; return next })
                    }
                  }}
                  onKeyDown={e => { if (e.key === "Enter") e.currentTarget.blur() }}
                />
              </FieldRow>
            )
          })}
        </>
      )}
    </div>
  )
}

function EndpointLine({ method, path, description }: { method: string; path: string; description?: string }) {
  const color = { GET: "#26A69A", POST: "#FFB74D", PUT: "#42A5F5", DELETE: "#FF5252" }[method] ?? "#727680"
  return (
    <p className="font-mono text-[11px] text-text-muted break-all" title={description}>
      <span style={{ color }}>{method.padEnd(6)}</span>
      <span>{path}</span>
    </p>
  )
}
