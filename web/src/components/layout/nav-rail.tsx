import { NavLink } from "react-router-dom"

const navItems = [
  { to: "/", icon: "fa-solid fa-table-cells-large", label: "Dashboard" },
  { to: "/jobs", icon: "fa-solid fa-list", label: "Jobs" },
  { to: "/settings", icon: "fa-solid fa-gear", label: "Settings" },
  { to: "/logs", icon: "fa-solid fa-terminal", label: "Logs" },
]

export function NavRail() {
  return (
    <nav className="fixed bottom-0 left-0 right-0 z-50 flex flex-row items-center justify-around w-full h-14 bg-surface-deep border-t border-border-subtle px-2 gap-1 md:static md:flex-col md:justify-start md:w-14 md:h-auto md:border-t-0 md:border-r md:py-3 md:px-0 md:shrink-0">
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
