import { Outlet } from "react-router-dom"
import { NavRail } from "./nav-rail"

export function AppShell() {
  return (
    <div className="flex w-full min-h-screen">
      <NavRail />
      <main className="flex-1 overflow-auto">
        <Outlet />
      </main>
    </div>
  )
}
