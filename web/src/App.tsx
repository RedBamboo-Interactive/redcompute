import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react"
import { createHashRouter, RouterProvider } from "react-router-dom"
import { WsEventProvider, useWsSubscribe } from "@redbamboo/utility"
import { AppLayout } from "@/components/layout/app-layout"
import { DashboardPage } from "@/pages/dashboard"
import { JobsPage } from "@/pages/jobs"
import { StatsPage } from "@/pages/stats"
import { TokenPrompt } from "@/components/auth/token-prompt"
import { AppStateProvider } from "@/contexts/app-state"
import { useCapabilities } from "@/hooks/use-capabilities"
import { useHardware } from "@/hooks/use-hardware"
import { useJobs, type JobFilters } from "@/hooks/use-jobs"
import { useSettings } from "@/hooks/use-settings"
import { isRemoteAccess, getToken, setToken } from "@/api/auth"
import type { WsEvent } from "@/api/types"
import { getTheme, subscribeTheme, getContrast, subscribeContrast } from "@/lib/theme-store"

function WsHookBridge({ capsRef, hardwareRef, jobsRef, settingsRef }: {
  capsRef: React.RefObject<ReturnType<typeof useCapabilities>>
  hardwareRef: React.RefObject<ReturnType<typeof useHardware>>
  jobsRef: React.RefObject<ReturnType<typeof useJobs>>
  settingsRef: React.RefObject<ReturnType<typeof useSettings>>
}) {
  useWsSubscribe((event) => {
    const e = event as WsEvent
    capsRef.current.handleWsEvent(e)
    hardwareRef.current.handleWsEvent(e)
    jobsRef.current.handleWsEvent(e)
    settingsRef.current.handleWsEvent(e)
  })
  return null
}

function extractTokenFromHash(): string | null {
  const hash = window.location.hash
  const match = hash.match(/[?&]token=([^&]+)/)
  if (!match) return null
  window.location.hash = hash.replace(/[?&]token=[^&]+/, "").replace(/#\/$/, "#/")
  return decodeURIComponent(match[1])
}

const router = createHashRouter([
  {
    element: <AppLayout />,
    children: [
      { index: true, element: <DashboardPage />, handle: { crumb: "Capabilities", icon: "fa-solid fa-microchip" } },
      { path: "jobs", element: <JobsPage />, handle: { crumb: "Jobs", icon: "fa-solid fa-list-check" } },
      { path: "stats", element: <StatsPage />, handle: { crumb: "Stats", icon: "fa-solid fa-chart-line" } },
    ],
  },
])

export default function App() {
  const [authed, setAuthed] = useState(() => {
    const hashToken = extractTokenFromHash()
    if (hashToken) { setToken(hashToken); return true }
    return !isRemoteAccess() || !!getToken()
  })
  const theme = useSyncExternalStore(subscribeTheme, getTheme)
  const contrast = useSyncExternalStore(subscribeContrast, getContrast)

  useEffect(() => {
    const root = document.documentElement
    root.classList.toggle("dark", theme === "dark")
    root.dataset.contrast = contrast
  }, [theme, contrast])

  const caps = useCapabilities()
  const hardware = useHardware()
  const [jobFilters, setJobFilters] = useState<JobFilters>({})
  const jobs = useJobs(jobFilters)
  const settings = useSettings()

  const capsRef = useRef(caps)
  const hardwareRef = useRef(hardware)
  const jobsRef = useRef(jobs)
  const settingsRef = useRef(settings)
  capsRef.current = caps
  hardwareRef.current = hardware
  jobsRef.current = jobs
  settingsRef.current = settings

  const wsUrl = useCallback(() => {
    const protocol = window.location.protocol === "https:" ? "wss:" : "ws:"
    const token = getToken()
    const tokenParam = token ? `?token=${encodeURIComponent(token)}` : ""
    return `${protocol}//${window.location.host}/ws${tokenParam}`
  }, [])

  const onReconnect = useCallback(() => {
    capsRef.current.refresh()
    jobsRef.current.refresh()
  }, [])

  useEffect(() => {
    const handler = () => setAuthed(false)
    window.addEventListener("redcompute:auth-required", handler)
    return () => window.removeEventListener("redcompute:auth-required", handler)
  }, [])

  if (!authed) {
    if (!isRemoteAccess()) {
      window.location.href = "/login"
      return null
    }
    return <TokenPrompt onAuthenticated={() => { setAuthed(true); location.reload() }} />
  }

  return (
    <AppStateProvider value={{ caps, hardware, jobs, jobFilters, setJobFilters, settings }}>
      <WsEventProvider url={wsUrl} onReconnect={onReconnect}>
        <WsHookBridge capsRef={capsRef} hardwareRef={hardwareRef} jobsRef={jobsRef} settingsRef={settingsRef} />
        <RouterProvider router={router} />
      </WsEventProvider>
    </AppStateProvider>
  )
}
