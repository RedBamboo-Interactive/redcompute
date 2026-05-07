import { QRCodeSVG } from "qrcode.react"
import type { TunnelSettings } from "@/api/types"

interface Props {
  open: boolean
  onClose: () => void
  tunnel: TunnelSettings
}

export function ShareModal({ open, onClose, tunnel }: Props) {
  if (!open) return null
  if (!tunnel.hostname || !tunnel.accessToken) return null

  const shareUrl = `https://${tunnel.hostname}/#/?token=${encodeURIComponent(tunnel.accessToken)}`

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-surface-elevated border border-border-subtle rounded-xl w-full max-w-sm mx-4 overflow-hidden shadow-xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="p-5 flex flex-col items-center gap-4">
          <div className="flex items-center gap-2">
            <i className="fa-solid fa-qrcode text-accent-teal" />
            <h2 className="text-base font-semibold">Share Connection</h2>
          </div>
          <p className="text-[11px] text-text-muted text-center">
            Scan this QR code to open RedCompute on another device.
          </p>
          <div className="bg-white rounded-xl p-4">
            <QRCodeSVG
              value={shareUrl}
              size={180}
              bgColor="#ffffff"
              fgColor="#262830"
              level="M"
            />
          </div>
          <div className="w-full">
            <div className="flex items-center gap-2">
              <input
                readOnly
                value={shareUrl}
                className="flex-1 bg-surface-deep border border-white/10 rounded px-2 py-1.5 text-[10px] text-text-muted font-mono truncate outline-none"
              />
              <button
                onClick={() => navigator.clipboard.writeText(shareUrl)}
                className="shrink-0 px-3 py-1.5 rounded bg-white/10 hover:bg-white/15 text-xs transition-colors"
              >
                <i className="fa-solid fa-copy mr-1" />
                Copy
              </button>
            </div>
          </div>
        </div>
        <div className="px-5 py-3 border-t border-border-subtle flex justify-end">
          <button
            onClick={onClose}
            className="px-3 py-1.5 text-sm text-text-muted hover:text-white transition-colors"
          >
            Close
          </button>
        </div>
      </div>
    </div>
  )
}
