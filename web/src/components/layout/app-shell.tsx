import { useState } from "react"
import { Outlet } from "react-router-dom"
import { AppHeader } from "./app-header"
import { SettingsModal } from "./settings-modal"
import { ConsolePanel } from "./console-panel"
import { ShareModal } from "./share-modal"
import type { Settings, LogEntry, TagInfo } from "@/api/types"

interface Props {
  settings: Settings | null
  saving: boolean
  onUpdateGeneral: (updates: Record<string, unknown>) => void
  onUpdateCapability: (slug: string, updates: { enabled?: boolean; activeProvider?: string }) => void
  onUpdateProvider: (slug: string, providerName: string, updates: Record<string, unknown>) => Promise<void>
  logEntries: LogEntry[]
  logTags: TagInfo[]
  logSearch: string
  setLogSearch: (s: string) => void
  logTagFilter: string | null
  setLogTagFilter: (t: string | null) => void
  logSelectedEntry: LogEntry | null
  setLogSelectedEntry: (e: LogEntry | null) => void
  logAutoScrollRef: React.MutableRefObject<boolean>
}

export function AppShell({
  settings, saving, onUpdateGeneral, onUpdateCapability, onUpdateProvider,
  logEntries, logTags, logSearch, setLogSearch, logTagFilter, setLogTagFilter,
  logSelectedEntry, setLogSelectedEntry, logAutoScrollRef,
}: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
  const [shareOpen, setShareOpen] = useState(false)

  const tunnel = settings?.tunnel
  const canShare = tunnel?.status === "Running" && !!tunnel.hostname && !!tunnel.accessToken

  return (
    <div className="flex flex-col h-dvh w-full">
      <AppHeader
        onOpenConsole={() => setConsoleOpen(true)}
        onOpenSettings={() => setSettingsOpen(true)}
        onOpenShare={() => setShareOpen(true)}
        canShare={canShare}
      />

      <main className="flex-1 min-h-0 overflow-auto">
        <Outlet />
      </main>

      <SettingsModal
        open={settingsOpen}
        onOpenChange={setSettingsOpen}
        settings={settings}
        saving={saving}
        onUpdateGeneral={onUpdateGeneral}
        onUpdateCapability={onUpdateCapability}
        onUpdateProvider={onUpdateProvider}
      />

      <ConsolePanel
        open={consoleOpen}
        onOpenChange={setConsoleOpen}
        entries={logEntries}
        tags={logTags}
        search={logSearch}
        setSearch={setLogSearch}
        tagFilter={logTagFilter}
        setTagFilter={setLogTagFilter}
        selectedEntry={logSelectedEntry}
        setSelectedEntry={setLogSelectedEntry}
        autoScrollRef={logAutoScrollRef}
      />

      {tunnel && (
        <ShareModal
          open={shareOpen}
          onClose={() => setShareOpen(false)}
          tunnel={tunnel}
        />
      )}
    </div>
  )
}
