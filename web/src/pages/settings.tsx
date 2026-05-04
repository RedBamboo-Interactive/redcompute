import { useState } from "react"
import { QRCodeSVG } from "qrcode.react"
import type { Settings } from "@/api/types"

export function SettingsPage({ settings, saving, onUpdateGeneral }: {
  settings: Settings | null
  saving: boolean
  onUpdateGeneral: (updates: Record<string, unknown>) => void
}) {
  const [port, setPort] = useState("")
  const [retention, setRetention] = useState("")
  const [tunnelToken, setTunnelToken] = useState("")
  const [accessToken, setAccessToken] = useState("")
  const [hostname, setHostname] = useState("")
  const [cloudflaredPath, setCloudflaredPath] = useState("")
  const [showTunnelToken, setShowTunnelToken] = useState(false)
  const [showAccessToken, setShowAccessToken] = useState(false)

  if (!settings) return <p className="text-text-muted p-8">Loading settings...</p>

  const tunnel = settings.tunnel
  const statusColor = {
    Stopped: "#727680",
    Starting: "#FFB74D",
    Running: "#43A25A",
    Error: "#FF5252",
  }[tunnel.status] ?? "#727680"

  return (
    <div className="p-6 max-w-[600px]">
      <h1 className="text-[20px] font-semibold text-white opacity-95 mb-5">Settings</h1>

      {/* GENERAL */}
      <SectionLabel>GENERAL</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4">
        <FieldRow label="API Port">
          <input type="number" className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[13px] w-24 font-mono focus:border-white/30"
            placeholder={String(settings.apiPort)} value={port}
            onChange={e => setPort(e.target.value)}
            onBlur={() => { if (port && port !== String(settings.apiPort)) { onUpdateGeneral({ apiPort: Number(port) }); setPort("") } }} />
        </FieldRow>
        <FieldRow label="Job Retention (days)">
          <input type="number" className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[13px] w-24 font-mono focus:border-white/30"
            placeholder={String(settings.jobRetentionDays)} value={retention}
            onChange={e => setRetention(e.target.value)}
            onBlur={() => { if (retention && retention !== String(settings.jobRetentionDays)) { onUpdateGeneral({ jobRetentionDays: Number(retention) }); setRetention("") } }} />
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
          <button
            className={`w-8 h-[18px] rounded-full relative transition-colors ${tunnel.enabled ? "bg-[#43A25A]" : "bg-white/15"}`}
            disabled={saving}
            onClick={() => onUpdateGeneral({ tunnelEnabled: !tunnel.enabled })}
          >
            <span className={`absolute top-[2px] w-[14px] h-[14px] rounded-full bg-white transition-all ${tunnel.enabled ? "left-[15px]" : "left-[2px]"}`} />
          </button>
        </FieldRow>
        <FieldRow label="Tunnel Token">
          <div className="flex items-center gap-1">
            <input
              type={showTunnelToken ? "text" : "password"}
              className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[11px] flex-1 font-mono min-w-0 focus:border-white/30"
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
              className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[11px] flex-1 font-mono min-w-0 focus:border-white/30"
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
            className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[11px] w-full font-mono focus:border-white/30"
            placeholder={tunnel.hostname ?? "redcompute.example.com"}
            value={hostname}
            onChange={e => setHostname(e.target.value)}
            onBlur={() => { if (hostname && hostname !== (tunnel.hostname ?? "")) { onUpdateGeneral({ tunnelHostname: hostname }); setHostname("") } }}
          />
        </FieldRow>
        <FieldRow label="Cloudflared Path">
          <input
            type="text"
            className="bg-[#1a1a1f] border border-white/10 rounded px-2 py-1 outline-none text-white text-[11px] w-full font-mono focus:border-white/30"
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

      {/* CONFIGURATION FILE */}
      <SectionLabel>CONFIGURATION FILE</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4">
        <p className="font-mono text-[11px] text-text-muted mb-2">{settings.configPath}</p>
        <div className="flex gap-2">
          <button className="text-[13px] text-text-muted hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10">
            Open Config File
          </button>
          <button className="text-[13px] text-text-muted hover:text-white transition-colors px-2 py-1 rounded hover:bg-white/10"
            disabled={saving}>
            Save Changes
          </button>
        </div>
      </div>

      {/* CAPABILITIES */}
      <SectionLabel>CAPABILITIES</SectionLabel>
      {Object.entries(settings.capabilities).map(([slug, cap]) => {
        const provider = cap.activeProvider && cap.providers[cap.activeProvider]
          ? cap.providers[cap.activeProvider]
          : null

        return (
          <div key={slug} className="bg-surface-elevated rounded-lg p-4 mb-3">
            <div className="flex items-center gap-2 mb-2">
              <span className="text-[13px] font-semibold text-white">{slug}</span>
              <span className="text-[11px] text-text-muted">({slug})</span>
            </div>
            {provider && (
              <div className="space-y-1">
                <FieldRow label="Provider"><span className="text-white text-[11px]">{cap.activeProvider}</span></FieldRow>
                {provider.backendPort != null && <FieldRow label="Backend Port"><Val mono>{provider.backendPort}</Val></FieldRow>}
                {provider.wslDistro != null && <FieldRow label="WSL Distro"><Val>{provider.wslDistro}</Val></FieldRow>}
                {provider.serverPath != null && <FieldRow label="Server Path"><Val mono wrap>{provider.serverPath}</Val></FieldRow>}
                {provider.model != null && <FieldRow label="Model"><Val mono wrap>{provider.model}</Val></FieldRow>}
                {provider.healthEndpoint != null && <FieldRow label="Health Endpoint"><Val mono>{provider.healthEndpoint}</Val></FieldRow>}
              </div>
            )}
          </div>
        )
      })}

      {/* API ENDPOINTS */}
      <SectionLabel>API ENDPOINTS</SectionLabel>
      <div className="bg-surface-elevated rounded-lg p-4 mb-4 space-y-1">
        <EndpointLine method="GET" path={`http://localhost:${settings.apiPort}/discover`} />
        <EndpointLine method="GET" path={`http://localhost:${settings.apiPort}/openapi.json`} />
        <EndpointLine method="GET" path={`http://localhost:${settings.apiPort}/status`} />
        <EndpointLine method="GET" path={`http://localhost:${settings.apiPort}/ws/schema`} />
        <EndpointLine method="POST" path={`http://localhost:${settings.apiPort}/tts/generate`} />
      </div>
    </div>
  )
}

function SectionLabel({ children }: { children: React.ReactNode }) {
  return <p className="text-[11px] font-semibold text-text-muted mb-2 tracking-wide">{children}</p>
}

function FieldRow({ label, children }: { label: string; children: React.ReactNode }) {
  return (
    <div className="flex mb-1">
      <span className="text-[11px] text-text-muted w-[120px] shrink-0">{label}</span>
      <div className="flex-1">{children}</div>
    </div>
  )
}

function Val({ children, mono, wrap }: { children: unknown; mono?: boolean; wrap?: boolean }) {
  return (
    <span className={`text-white text-[11px] ${mono ? "font-mono" : ""} ${wrap ? "break-all" : ""}`}>
      {String(children ?? "")}
    </span>
  )
}

function EndpointLine({ method, path }: { method: string; path: string }) {
  const color = method === "GET" ? "#26A69A" : "#FFB74D"
  return (
    <p className="font-mono text-[11px] text-text-muted">
      <span style={{ color }}>{method.padEnd(5)}</span>
      <span>{path}</span>
    </p>
  )
}
