import { QRCodeSVG } from "qrcode.react"
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@redbamboo/ui"
import type { TunnelSettings } from "@/api/types"

interface Props {
  open: boolean
  onClose: () => void
  tunnel: TunnelSettings
}

export function ShareModal({ open, onClose, tunnel }: Props) {
  if (!tunnel.hostname || !tunnel.accessToken) return null

  const shareUrl = `https://${tunnel.hostname}/#/?token=${encodeURIComponent(tunnel.accessToken)}`

  return (
    <Dialog open={open} onOpenChange={v => { if (!v) onClose() }}>
      <DialogContent className="max-w-sm">
        <DialogHeader>
          <DialogTitle className="flex items-center gap-2 text-base">
            <i className="fa-solid fa-qrcode text-accent-teal" />
            Share Connection
          </DialogTitle>
        </DialogHeader>

        <p className="text-[11px] text-text-muted text-center">
          Scan this QR code to open RedCompute on another device.
        </p>

        <div className="flex justify-center">
          <div className="bg-white rounded-xl p-4">
            <QRCodeSVG
              value={shareUrl}
              size={180}
              bgColor="#ffffff"
              fgColor="#262830"
              level="M"
            />
          </div>
        </div>

        <div className="flex items-center gap-2">
          <input
            readOnly
            value={shareUrl}
            className="flex-1 bg-surface-deep border border-contrast/10 rounded px-2 py-1.5 text-[10px] text-text-muted font-mono truncate outline-none"
          />
          <button
            onClick={() => navigator.clipboard.writeText(shareUrl)}
            className="shrink-0 px-3 py-1.5 rounded bg-contrast/10 hover:bg-contrast/15 text-xs transition-colors"
          >
            <i className="fa-solid fa-copy mr-1" />
            Copy
          </button>
        </div>
      </DialogContent>
    </Dialog>
  )
}
