import { Outlet } from "react-router-dom"
import { NavRail } from "./nav-rail"

export function AppShell() {
  return (
    <div className="flex flex-col w-full h-dvh md:flex-row">
      <main className="flex-1 min-h-0 overflow-auto">
        <Outlet />
      </main>
      <NavRail />
    </div>
  )
}
