import { useState } from "react"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@redbamboo/ui"
import type { HardwareSnapshot, GpuInfo } from "@/api/types"

const fallbackCapNames: Record<string, string> = {
  tts: "Text to Speech",
  stt: "Speech to Text",
  "image-gen": "Image Generation",
  "music-gen": "Music Generation",
  "ai-session": "AI Session",
  llm: "LLM",
}

function formatBytes(bytes: number): string {
  if (bytes >= 1073741824) return (bytes / 1073741824).toFixed(1) + " GB"
  if (bytes >= 1048576) return (bytes / 1048576).toFixed(0) + " MB"
  return (bytes / 1024).toFixed(0) + " KB"
}

function tempColor(c: number): string {
  if (c >= 85) return "#FF5252"
  if (c >= 70) return "#FFB74D"
  return "#26A69A"
}

function Bar({ value, max, color = "#26A69A" }: { value: number; max: number; color?: string }) {
  const pct = max > 0 ? Math.min(100, (value / max) * 100) : 0
  return (
    <div className="h-[3px] rounded-full bg-contrast/10 overflow-hidden">
      <div className="h-full rounded-full transition-all duration-500" style={{ width: `${pct}%`, backgroundColor: color }} />
    </div>
  )
}

function MetricRow({ label, value, sub, bar }: { label: string; value: string; sub?: string; bar?: { value: number; max: number; color?: string } }) {
  return (
    <div className="flex flex-col gap-1">
      <div className="flex items-baseline justify-between gap-2">
        <span className="text-contrast/40 text-[11px]">{label}</span>
        <span className="text-contrast text-[13px] font-mono">{value}</span>
      </div>
      {bar && <Bar {...bar} />}
      {sub && <span className="text-contrast/30 text-[10px]">{sub}</span>}
    </div>
  )
}

function CapabilityVramSection({ gpu }: { gpu: GpuInfo }) {
  const entries = Object.entries(gpu.capabilityVram || {}).sort((a, b) => b[1] - a[1])
  if (entries.length === 0) return null

  const attributed = entries.reduce((sum, [, v]) => sum + v, 0)
  const other = gpu.memory.usedBytes - attributed

  return (
    <div className="pt-2 border-t border-contrast/[0.06]">
      <span className="text-contrast/40 text-[11px]">VRAM by Capability</span>
      <div className="mt-1.5 space-y-1.5">
        {entries.map(([slug, bytes]) => (
          <div key={slug}>
            <div className="flex items-baseline justify-between text-[11px] font-mono mb-0.5">
              <span className="text-contrast/70">{fallbackCapNames[slug] || slug}</span>
              <span className="text-contrast">{formatBytes(bytes)}</span>
            </div>
            <Bar value={bytes} max={gpu.memory.totalBytes} color="#7C4DFF" />
          </div>
        ))}
        {other > 0 && (
          <div>
            <div className="flex items-baseline justify-between text-[11px] font-mono mb-0.5">
              <span className="text-contrast/40">Other</span>
              <span className="text-contrast/50">{formatBytes(other)}</span>
            </div>
            <Bar value={other} max={gpu.memory.totalBytes} color="#9098A0" />
          </div>
        )}
      </div>
    </div>
  )
}

function GpuDetail({ gpu }: { gpu: GpuInfo }) {
  const shortName = gpu.name.replace(/^NVIDIA\s+/, "").replace(/GeForce\s+/, "")
  return (
    <div className="space-y-3">
      <h3 className="text-contrast/60 text-[12px] font-medium">{shortName}</h3>
      <div className="grid grid-cols-2 gap-x-6 gap-y-3">
        <MetricRow label="GPU" value={`${gpu.utilizationPercent}%`} bar={{ value: gpu.utilizationPercent, max: 100 }} />
        <MetricRow label="VRAM" value={`${formatBytes(gpu.memory.usedBytes)} / ${formatBytes(gpu.memory.totalBytes)}`} bar={{ value: gpu.memory.usedBytes, max: gpu.memory.totalBytes }} />
        <MetricRow label="Power" value={`${gpu.powerWatts} / ${gpu.powerLimitWatts} W`} bar={{ value: gpu.powerWatts, max: gpu.powerLimitWatts, color: "#FFB74D" }} />
        <MetricRow label="Temp" value={`${gpu.temperatureCelsius}°C`} bar={{ value: gpu.temperatureCelsius, max: 100, color: tempColor(gpu.temperatureCelsius) }} />
        <MetricRow label="Graphics Clock" value={`${gpu.graphicsClockMHz} MHz`} />
        <MetricRow label="Memory Clock" value={`${gpu.memoryClockMHz} MHz`} />
      </div>
      <CapabilityVramSection gpu={gpu} />
      {gpu.processes.length > 0 && (
        <div className="pt-2 border-t border-contrast/[0.06]">
          <span className="text-contrast/40 text-[11px]">GPU Processes</span>
          <div className="mt-1.5 space-y-0.5">
            {gpu.processes.slice(0, 15).map(p => (
              <div key={p.pid} className="flex items-center gap-2 text-[11px] font-mono">
                <span className="text-contrast/60 truncate flex-1">
                  {p.processName}
                  {p.capabilitySlug && <span className="text-[#7C4DFF] ml-1.5">{fallbackCapNames[p.capabilitySlug] || p.capabilitySlug}</span>}
                </span>
                <span className="text-contrast/25">{p.pid}</span>
                <span className="text-contrast/50 w-16 text-right">{p.usedMemoryBytes > 0 ? formatBytes(p.usedMemoryBytes) : "—"}</span>
              </div>
            ))}
          </div>
        </div>
      )}
    </div>
  )
}

export function HardwareFooter({ hardware }: { hardware: HardwareSnapshot }) {
  const [open, setOpen] = useState(false)
  const gpu = hardware.gpus[0]

  return (
    <>
      <div
        className="border-t border-contrast/[0.06] px-4 py-1.5 flex items-center gap-3 text-[11px] text-text-muted font-mono cursor-pointer select-none hover:bg-contrast/[0.02] transition-colors"
        onClick={() => setOpen(true)}
      >
        {gpu && (
          <>
            <span className="text-contrast/30 hidden sm:inline">{gpu.name.replace(/^NVIDIA\s+/, "").replace(/GeForce\s+/, "")}</span>
            <span>GPU {gpu.utilizationPercent}%</span>
            <span className="hidden sm:inline">VRAM {formatBytes(gpu.memory.usedBytes)}/{formatBytes(gpu.memory.totalBytes)}</span>
            <span style={{ color: tempColor(gpu.temperatureCelsius) }}>{gpu.temperatureCelsius}°C</span>
            <span className="text-contrast/10 hidden sm:inline">|</span>
          </>
        )}
        <span>CPU {hardware.cpu.usagePercent}%</span>
        <span className="hidden sm:inline">RAM {formatBytes(hardware.ram.usedBytes)}/{formatBytes(hardware.ram.totalBytes)}</span>
      </div>

      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent className="max-w-lg">
          <DialogHeader>
            <DialogTitle>Hardware Monitor</DialogTitle>
          </DialogHeader>
          <div className="space-y-6 py-2">
            {hardware.gpus.map(g => (
              <GpuDetail key={g.index} gpu={g} />
            ))}

            <div className="space-y-3">
              <h3 className="text-contrast/60 text-[12px] font-medium">System</h3>
              <div className="grid grid-cols-2 gap-x-6 gap-y-3">
                <MetricRow
                  label="CPU"
                  value={`${hardware.cpu.usagePercent}%`}
                  sub={`${hardware.cpu.coreCount} cores`}
                  bar={{ value: hardware.cpu.usagePercent, max: 100 }}
                />
                <MetricRow
                  label="RAM"
                  value={`${formatBytes(hardware.ram.usedBytes)} / ${formatBytes(hardware.ram.totalBytes)}`}
                  sub={`${formatBytes(hardware.ram.availableBytes)} available`}
                  bar={{ value: hardware.ram.usedBytes, max: hardware.ram.totalBytes }}
                />
              </div>
            </div>
          </div>
        </DialogContent>
      </Dialog>
    </>
  )
}
