import { Outlet } from "react-router-dom"
import { NavRail } from "./nav-rail"

export function AppShell() {
  return (
    <div className="flex flex-col w-full min-h-screen md:flex-row">
      <NavRail />
      <main className="flex-1 overflow-auto pb-16 md:pb-0">
        <Outlet />
      </main>
    </div>
  )
}
