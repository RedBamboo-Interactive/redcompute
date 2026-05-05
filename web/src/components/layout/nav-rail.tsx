import { NavLink } from "react-router-dom"
import { useInstallPrompt } from "@/hooks/use-install-prompt"

const navItems = [
  { to: "/", icon: "fa-solid fa-grid-2", label: "Compute" },
  { to: "/claude", icon: "fa-regular fa-square-terminal", label: "Claude" },
  { to: "/jobs", icon: "fa-solid fa-list", label: "Jobs" },
  { to: "/settings", icon: "fa-solid fa-gear", label: "Settings" },
  { to: "/logs", icon: "fa-solid fa-terminal", label: "Logs" },
]

export function NavRail() {
  const { canInstall, install } = useInstallPrompt()

  return (
    <nav className="shrink-0 flex flex-row items-center justify-around w-full h-14 bg-surface-deep border-t border-border-subtle px-2 pb-[env(safe-area-inset-bottom)] gap-1 md:order-first md:flex-col md:justify-start md:w-14 md:h-auto md:border-t-0 md:border-r md:py-3 md:px-0 md:pb-3">
      {navItems.map(({ to, icon, label }) => (
        <NavLink
          key={to}
          to={to}
          end={to === "/"}
          title={label}
          className={({ isActive }) =>
            `flex items-center justify-center w-12 h-12 rounded-xl transition-colors ${
              isActive
                ? "bg-surface-elevated text-white"
                : "text-text-muted hover:bg-white/[0.15] hover:text-white"
            }`
          }
        >
          <i className={`${icon} text-base`} />
        </NavLink>
      ))}
      {canInstall && (
        <button
          onClick={install}
          title="Install app"
          className="flex items-center justify-center w-12 h-12 rounded-xl transition-colors text-[#aa3bff] hover:bg-white/[0.15] md:mt-auto"
        >
          <i className="fa-solid fa-download text-base" />
        </button>
      )}
    </nav>
  )
}
