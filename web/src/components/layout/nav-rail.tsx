import { NavLink } from "react-router-dom"

const navItems = [
  { to: "/", icon: "fa-solid fa-table-cells-large", label: "Dashboard" },
  { to: "/jobs", icon: "fa-solid fa-list", label: "Jobs" },
  { to: "/settings", icon: "fa-solid fa-gear", label: "Settings" },
  { to: "/logs", icon: "fa-solid fa-terminal", label: "Logs" },
]

export function NavRail() {
  return (
    <nav className="flex flex-col items-center w-14 bg-surface-deep border-r border-border-subtle py-3 gap-1 shrink-0">
      {navItems.map(({ to, icon, label }) => (
        <NavLink
          key={to}
          to={to}
          end={to === "/"}
          title={label}
          className={({ isActive }) =>
            `flex items-center justify-center w-12 h-12 rounded-xl transition-colors ${
              isActive
                ? "bg-[#36373E] text-white"
                : "text-text-muted hover:bg-white/[0.15] hover:text-white"
            }`
          }
        >
          <i className={`${icon} text-base`} />
        </NavLink>
      ))}
    </nav>
  )
}
