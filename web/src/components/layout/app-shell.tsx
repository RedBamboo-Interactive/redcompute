import { useState } from "react"
import { NavLink, Outlet } from "react-router-dom"
import { AppShell as UtilityAppShell } from "@redbamboo/utility"
import { DropdownMenuItem } from "@redbamboo/ui"
import { SettingsModal } from "./settings-modal"
import { ConsolePanel } from "./console-panel"
import type { Settings, LogEntry, TagInfo } from "@/api/types"

interface Props {
  settings: Settings | null
  saving: boolean
  onUpdateGeneral: (updates: Record<string, unknown>) => void
  onUpdateCapability: (slug: string, updates: { activeProvider?: string }) => void
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

  const tunnel = settings?.tunnel
  const canShare = tunnel?.status === "Running" && !!tunnel.hostname && !!tunnel.accessToken

  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    `flex items-center gap-1.5 px-2.5 py-1 rounded text-xs transition-colors ${
      isActive
        ? "text-accent-teal bg-accent-teal-a15"
        : "text-text-muted hover:text-contrast hover:bg-overlay-10"
    }`

  return (
    <UtilityAppShell
      config={{
        name: "RedCompute",
        version: __APP_VERSION__,
        description: "AI compute service dashboard",
        icon: "fa-solid fa-microchip",
        brand: {
          icon: "fa-solid fa-microchip",
          nameParts: ["Red", "Compute"],
          accentClass: "text-accent-teal",
        },
        github: {
          app: "https://github.com/RedBamboo-Interactive/redcompute",
          company: "https://github.com/RedBamboo-Interactive",
        },
        share: canShare
          ? {
              url: () => `https://${tunnel!.hostname}/#/?token=${encodeURIComponent(tunnel!.accessToken!)}`,
              title: "Share Connection",
              description: "Scan this QR code to open RedCompute on another device.",
            }
          : undefined,
      }}
      headerContent={
        <>
          <NavLink to="/" end className={navLinkClass}>
            <i className="fa-solid fa-grid-2 text-xs" />
            <span>Capabilities</span>
          </NavLink>
          <NavLink to="/jobs" className={navLinkClass}>
            <i className="fa-solid fa-list text-xs" />
            <span>Jobs</span>
          </NavLink>
          <NavLink to="/stats" className={navLinkClass}>
            <i className="fa-solid fa-chart-simple text-xs" />
            <span>Stats</span>
          </NavLink>
        </>
      }
      menuItems={
        <>
          <DropdownMenuItem onClick={() => setConsoleOpen(true)}>
            <i className="fa-solid fa-terminal size-4 text-center" />
            Console
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => setSettingsOpen(true)}>
            <i className="fa-solid fa-gear size-4 text-center" />
            Settings
          </DropdownMenuItem>
        </>
      }
      className="flex flex-col h-dvh w-full"
    >
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
    </UtilityAppShell>
  )
}
