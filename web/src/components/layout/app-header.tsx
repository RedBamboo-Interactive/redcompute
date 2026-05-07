import { useState, useRef, useEffect } from "react"
import { NavLink } from "react-router-dom"
import { useInstallPrompt } from "@/hooks/use-install-prompt"

interface Props {
  onOpenConsole: () => void
  onOpenSettings: () => void
  onOpenShare: () => void
  canShare: boolean
}

export function AppHeader({ onOpenConsole, onOpenSettings, onOpenShare, canShare }: Props) {
  const { canInstall, install } = useInstallPrompt()
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
        : "text-text-muted hover:text-white hover:bg-white/10"
    }`

  return (
    <header className="shrink-0 flex items-center gap-3 px-4 py-2 border-b border-white/[0.06]">
      {/* Logo */}
      <div className="flex items-center gap-2">
        <div className="w-6 h-6 rounded bg-accent-teal/20 flex items-center justify-center">
          <i className="fa-solid fa-microchip text-accent-teal text-xs" />
        </div>
        <span className="text-sm font-semibold">
          <span className="text-accent-teal">Red</span>
          <span className="text-text-muted">Compute</span>
        </span>
      </div>

      <span className="flex-1" />

      {/* Nav buttons */}
      <NavLink to="/" end className={navLinkClass}>
        <i className="fa-solid fa-grid-2 text-xs" />
        <span>Capabilities</span>
      </NavLink>
      <NavLink to="/jobs" className={navLinkClass}>
        <i className="fa-solid fa-list text-xs" />
        <span>Jobs</span>
      </NavLink>

      {/* Hamburger menu */}
      <div className="relative" ref={menuRef}>
        <button
          onClick={() => setMenuOpen(v => !v)}
          className="text-text-muted hover:text-white text-xs transition-colors p-2 rounded hover:bg-white/10"
          title="Menu"
        >
          <i className="fa-solid fa-bars text-sm" />
        </button>

        {menuOpen && (
          <div className="absolute right-0 top-full mt-1 w-44 bg-surface-elevated border border-white/[0.08] rounded-lg shadow-lg shadow-black/40 py-1 z-50">
            <button
              onClick={() => { onOpenConsole(); setMenuOpen(false) }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-white/[0.07] transition-colors"
            >
              <i className="fa-solid fa-terminal w-4 text-center text-text-muted" />
              Console
            </button>
            <button
              onClick={() => { onOpenSettings(); setMenuOpen(false) }}
              className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-white/[0.07] transition-colors"
            >
              <i className="fa-solid fa-gear w-4 text-center text-text-muted" />
              Settings
            </button>
            {canShare && (
              <button
                onClick={() => { onOpenShare(); setMenuOpen(false) }}
                className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-white/[0.07] transition-colors"
              >
                <i className="fa-solid fa-qrcode w-4 text-center text-text-muted" />
                Share
              </button>
            )}
            {canInstall && (
              <button
                onClick={() => { install(); setMenuOpen(false) }}
                className="w-full flex items-center gap-2.5 px-3 py-2 text-xs text-text-primary hover:bg-white/[0.07] transition-colors"
              >
                <i className="fa-solid fa-download w-4 text-center text-text-muted" />
                Install
              </button>
            )}
          </div>
        )}
      </div>
    </header>
  )
}
