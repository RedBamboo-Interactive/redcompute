import { useCallback, useEffect, useRef, useState, useSyncExternalStore } from "react"
import { HashRouter, Routes, Route } from "react-router-dom"
import { TooltipProvider } from "@redbamboo/ui"
import { WsEventProvider, useWsSubscribe } from "@redbamboo/utility"
import { AppShell } from "@/components/layout/app-shell"
import { DashboardPage } from "@/pages/dashboard"
import { JobsPage } from "@/pages/jobs"
import { StatsPage } from "@/pages/stats"
import { TokenPrompt } from "@/components/auth/token-prompt"
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
    return <TokenPrompt onAuthenticated={() => { setAuthed(true); location.reload() }} />
  }

  return (
    <WsEventProvider url={wsUrl} onReconnect={onReconnect}>
    <WsHookBridge capsRef={capsRef} hardwareRef={hardwareRef} jobsRef={jobsRef} settingsRef={settingsRef} />
    <HashRouter>
      <TooltipProvider>
        <Routes>
          <Route element={
            <AppShell
              settings={settings.settings}
              saving={settings.saving}
              onUpdateGeneral={settings.updateGeneral}
              onUpdateCapability={settings.updateCapability}
              onUpdateProvider={settings.updateProvider}
            />
          }>
            <Route index element={<DashboardPage capabilities={caps.capabilities} onRefresh={caps.refresh} hardware={hardware.hardware} />} />
            <Route path="jobs" element={
              <JobsPage
                jobs={jobs.jobs} total={jobs.total} hasMore={jobs.hasMore} loading={jobs.loading}
                selectedJob={jobs.selectedJob} onSelectJob={jobs.setSelectedJob} onLoadMore={jobs.loadMore}
                filters={jobFilters} onFiltersChange={setJobFilters}
                capabilities={caps.capabilities}
              />
            } />
            <Route path="stats" element={<StatsPage capabilities={caps.capabilities} />} />
          </Route>
        </Routes>
      </TooltipProvider>
    </HashRouter>
    </WsEventProvider>
  )
}
