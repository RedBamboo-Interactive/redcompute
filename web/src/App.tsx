import { useEffect, useMemo, useRef, useState } from "react"
import { HashRouter, Routes, Route } from "react-router-dom"
import { TooltipProvider } from "@redbamboo/ui"
import { AppShell } from "@/components/layout/app-shell"
import { DashboardPage } from "@/pages/dashboard"
import { JobsPage } from "@/pages/jobs"
import { TokenPrompt } from "@/components/auth/token-prompt"
import { useCapabilities } from "@/hooks/use-capabilities"
import { useHardware } from "@/hooks/use-hardware"
import { useJobs, type JobFilters } from "@/hooks/use-jobs"
import { useLogs } from "@/hooks/use-logs"
import { useSettings } from "@/hooks/use-settings"
import { WsEventContext, type WsEventContextValue } from "@/contexts/ws-events"
import { createWebSocket } from "@/api/websocket"
import { isRemoteAccess, getToken, setToken } from "@/api/auth"
import type { WsEvent } from "@/api/types"

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
  const caps = useCapabilities()
  const hardware = useHardware()
  const [jobFilters, setJobFilters] = useState<JobFilters>({})
  const jobs = useJobs(jobFilters)
  const logs = useLogs()
  const settings = useSettings()

  const wsHandlers = useRef(new Set<(e: WsEvent) => void>())
  const wsContext = useMemo<WsEventContextValue>(() => ({
    subscribe: (handler) => {
      wsHandlers.current.add(handler)
      return () => { wsHandlers.current.delete(handler) }
    },
    dispatch: (event) => {
      wsHandlers.current.forEach(h => h(event))
    },
  }), [])

  const capsRef = useRef(caps)
  const hardwareRef = useRef(hardware)
  const jobsRef = useRef(jobs)
  const logsRef = useRef(logs)
  const settingsRef = useRef(settings)
  capsRef.current = caps
  hardwareRef.current = hardware
  jobsRef.current = jobs
  logsRef.current = logs
  settingsRef.current = settings

  useEffect(() => {
    if (!authed) return
    const ws = createWebSocket((event: WsEvent) => {
      wsContext.dispatch(event)
      capsRef.current.handleWsEvent(event)
      hardwareRef.current.handleWsEvent(event)
      jobsRef.current.handleWsEvent(event)
      logsRef.current.handleWsEvent(event)
      settingsRef.current.handleWsEvent(event)
    }, () => {
      capsRef.current.refresh()
      jobsRef.current.refresh()
    })
    return () => ws.close()
  }, [authed])

  useEffect(() => {
    const handler = () => setAuthed(false)
    window.addEventListener("redcompute:auth-required", handler)
    return () => window.removeEventListener("redcompute:auth-required", handler)
  }, [])

  if (!authed) {
    return <TokenPrompt onAuthenticated={() => { setAuthed(true); location.reload() }} />
  }

  return (
    <WsEventContext.Provider value={wsContext}>
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
              logEntries={logs.entries}
              logTags={logs.tags}
              logSearch={logs.search}
              setLogSearch={logs.setSearch}
              logTagFilter={logs.tagFilter}
              setLogTagFilter={logs.setTagFilter}
              logSelectedEntry={logs.selectedEntry}
              setLogSelectedEntry={logs.setSelectedEntry}
              logAutoScrollRef={logs.autoScrollRef}
            />
          }>
            <Route index element={<DashboardPage capabilities={caps.capabilities} onRefresh={caps.refresh} hardware={hardware.hardware} />} />
            <Route path="jobs" element={
              <JobsPage
                jobs={jobs.jobs} total={jobs.total} hasMore={jobs.hasMore} loading={jobs.loading}
                selectedJob={jobs.selectedJob} onSelectJob={jobs.setSelectedJob} onLoadMore={jobs.loadMore}
                filters={jobFilters} onFiltersChange={setJobFilters}
              />
            } />
          </Route>
        </Routes>
      </TooltipProvider>
    </HashRouter>
    </WsEventContext.Provider>
  )
}
