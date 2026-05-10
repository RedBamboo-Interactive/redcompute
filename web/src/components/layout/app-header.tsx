import { useState, useRef, useEffect } from "react"
import { NavLink } from "react-router-dom"
import { useInstallPrompt } from "@/hooks/use-install-prompt"
import { AboutDialog, AppHeader as AppHeaderBase } from "@redbamboo/ui"

interface Props {
  onOpenConsole: () => void
  onOpenSettings: () => void
  onOpenShare: () => void
  canShare: boolean
}

export function AppHeader({ onOpenConsole, onOpenSettings, onOpenShare, canShare }: Props) {
  const { canInstall, install } = useInstallPrompt()
  const [aboutOpen, setAboutOpen] = useState(false)
  const [menuOpen, setMenuOpen] = useState(false)
  const menuRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!menuOpen) return
    function handleClick(e: MouseEvent) {
      if (menuRef.current && !menuRef.current.contains(e.target as Node)) {
        setMenuOpen(false)
      }
    }
    document.addEventListener("mousedown", handleClick)
    return () => document.removeEventListener("mousedown", handleClick)
  }, [menuOpen])

  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    `flex items-center gap-1.5 px-2.5 py-1 rounded text-xs transition-colors ${
      isActive
        ? "text-accent-teal bg-accent-teal/15"
        : "text-text-muted hover:text-contrast hover:bg-contrast/10"
    }`

  return (
    <AppHeaderBase brand={{ icon: "fa-solid fa-microchip", nameParts: ["Red", "Compute"], accentClass: "text-accent-teal" }}>
      <NavLink to="/" end className={navLinkClass}>
        <i className="fa-solid fa-grid-2 text-xs" />
        <span>Capabilities</span>
      </NavLink>
      <NavLink to="/jobs" className={navLinkClass}>
        <i className="fa-solid fa-list text-xs" />
        <span>Jobs</span>
      </NavLink>

      <div className="relative" ref={menuRef}>
        <button
          onClick={() => setMenuOpen(v => !v)}
          className="text-text-muted hover:text-contrast text-xs transition-colors p-2 rounded hover:bg-contrast/10"
          title="Menu"
        >
          <i className="fa-solid fa-bars text-sm" />
        </button>

        {menuOpen && (
          <div className="absolute right-0 top-full mt-1 w-44 bg-surface-elevated border border-contrast/[0.08] rounded-lg shadow-lg shadow-black/40 py-1 z-50">
            <button
              onClick={() => { onOpenConsole(); setMenuOpen(false) }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-contrast/[0.07] transition-colors"
            >
              <i className="fa-solid fa-terminal w-4 text-center text-text-muted" />
              Console
            </button>
            <button
              onClick={() => { onOpenSettings(); setMenuOpen(false) }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-contrast/[0.07] transition-colors"
            >
              <i className="fa-solid fa-gear w-4 text-center text-text-muted" />
              Settings
            </button>
            {canShare && (
              <button
                onClick={() => { onOpenShare(); setMenuOpen(false) }}
                className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-contrast/[0.07] transition-colors"
              >
                <i className="fa-solid fa-qrcode w-4 text-center text-text-muted" />
                Share
              </button>
            )}
            {canInstall && (
              <button
                onClick={() => { install(); setMenuOpen(false) }}
                className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-contrast/[0.07] transition-colors"
              >
                <i className="fa-solid fa-download w-4 text-center text-text-muted" />
                Install
              </button>
            )}
            <div className="my-1 h-px bg-contrast/[0.06]" />
            <button
              onClick={() => { setAboutOpen(true); setMenuOpen(false) }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-contrast/[0.07] transition-colors"
            >
              <i className="fa-solid fa-circle-info w-4 text-center text-text-muted" />
              About
            </button>
          </div>
        )}
      </div>

      <AboutDialog
        open={aboutOpen}
        onOpenChange={setAboutOpen}
        app={{
          name: "RedCompute",
          version: __APP_VERSION__,
          description: "AI compute service dashboard",
          icon: "fa-solid fa-microchip",
        }}
        appGitHub="https://github.com/RedBamboo-Interactive/redcompute"
        companyGitHub="https://github.com/RedBamboo-Interactive"
      />
    </AppHeaderBase>
  )
}
