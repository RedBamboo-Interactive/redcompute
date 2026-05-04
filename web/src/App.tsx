import { useEffect, useRef, useState } from "react"
import { HashRouter, Routes, Route } from "react-router-dom"
import { TooltipProvider } from "@/components/ui/tooltip"
import { AppShell } from "@/components/layout/app-shell"
import { DashboardPage } from "@/pages/dashboard"
import { JobsPage } from "@/pages/jobs"
import { LogsPage } from "@/pages/logs"
import { SettingsPage } from "@/pages/settings"
import { TokenPrompt } from "@/components/auth/token-prompt"
import { useCapabilities } from "@/hooks/use-capabilities"
import { useJobs } from "@/hooks/use-jobs"
import { useLogs } from "@/hooks/use-logs"
import { useSettings } from "@/hooks/use-settings"
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
  const jobs = useJobs()
  const logs = useLogs()
  const settings = useSettings()

  const capsRef = useRef(caps)
  const jobsRef = useRef(jobs)
  const logsRef = useRef(logs)
  const settingsRef = useRef(settings)
  capsRef.current = caps
  jobsRef.current = jobs
  logsRef.current = logs
  settingsRef.current = settings

  useEffect(() => {
    if (!authed) return
    const ws = createWebSocket((event: WsEvent) => {
      capsRef.current.handleWsEvent(event)
      jobsRef.current.handleWsEvent(event)
      logsRef.current.handleWsEvent(event)
      settingsRef.current.handleWsEvent(event)
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
                onUpdateCapability={settings.updateCapability}
                onUpdateProvider={settings.updateProvider}
              />
            } />
          </Route>
        </Routes>
      </TooltipProvider>
    </HashRouter>
  )
}
