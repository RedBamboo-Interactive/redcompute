import { useEffect, useRef } from "react"
import { HashRouter, Routes, Route } from "react-router-dom"
import { TooltipProvider } from "@/components/ui/tooltip"
import { AppShell } from "@/components/layout/app-shell"
import { DashboardPage } from "@/pages/dashboard"
import { JobsPage } from "@/pages/jobs"
import { LogsPage } from "@/pages/logs"
import { SettingsPage } from "@/pages/settings"
import { useCapabilities } from "@/hooks/use-capabilities"
import { useJobs } from "@/hooks/use-jobs"
import { useLogs } from "@/hooks/use-logs"
import { useSettings } from "@/hooks/use-settings"
import { createWebSocket } from "@/api/websocket"
import type { WsEvent } from "@/api/types"

export default function App() {
  const caps = useCapabilities()
  const jobs = useJobs()
  const logs = useLogs()
  const settings = useSettings()

  const capsRef = useRef(caps)
  const jobsRef = useRef(jobs)
  const logsRef = useRef(logs)
  capsRef.current = caps
  jobsRef.current = jobs
  logsRef.current = logs

  useEffect(() => {
    const ws = createWebSocket((event: WsEvent) => {
      capsRef.current.handleWsEvent(event)
      jobsRef.current.handleWsEvent(event)
      logsRef.current.handleWsEvent(event)
    })
    return () => ws.close()
  }, [])

  return (
    <HashRouter>
      <TooltipProvider>
        <Routes>
          <Route element={<AppShell />}>
            <Route index element={<DashboardPage capabilities={caps.capabilities} jobs={jobs.jobs} onRefresh={caps.refresh} />} />
            <Route path="jobs" element={<JobsPage jobs={jobs.jobs} selectedJob={jobs.selectedJob} onSelectJob={jobs.setSelectedJob} />} />
            <Route path="logs" element={
              <LogsPage
                entries={logs.entries}
                tags={logs.tags}
                search={logs.search}
                setSearch={logs.setSearch}
                tagFilter={logs.tagFilter}
                setTagFilter={logs.setTagFilter}
                selectedEntry={logs.selectedEntry}
                setSelectedEntry={logs.setSelectedEntry}
                autoScrollRef={logs.autoScrollRef}
              />
            } />
            <Route path="settings" element={
              <SettingsPage
                settings={settings.settings}
                saving={settings.saving}
                onUpdateGeneral={settings.updateGeneral}
              />
            } />
          </Route>
        </Routes>
      </TooltipProvider>
    </HashRouter>
  )
}
