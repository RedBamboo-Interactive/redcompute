import { useState, type ReactNode } from "react"
import { NavLink } from "react-router-dom"
import { AppShell as UtilityAppShell, useLogStream, LogPanel, useCommand } from "@redbamboo/utility"
import { DropdownMenuItem, NavTabs, navTabClass, ResizablePanelGroup, ResizablePanel, ResizableHandle } from "@redbamboo/ui"
import { SettingsPanel } from "./settings-panel"
import { QueueJobDialog } from "@/components/jobs/queue-job-dialog"
import { useAppState } from "@/contexts/app-state"
import { connectionStore } from "@/api/auth"
import type { Settings } from "@/api/types"

interface Props {
  settings: Settings | null
  saving: boolean
  onUpdateGeneral: (updates: Record<string, unknown>) => void
  onUpdateCapability: (slug: string, updates: { activeProvider?: string }) => void
  onUpdateProvider: (slug: string, providerName: string, updates: Record<string, unknown>) => Promise<void>
  breadcrumb?: ReactNode
  children?: ReactNode
}

// Registered as a child of UtilityAppShell so the CommandProvider context exists
function ShellCommands({ onToggleConsole, onToggleSettings, onQueueJob }: {
  onToggleConsole: () => void
  onToggleSettings: () => void
  onQueueJob: () => void
}) {
  useCommand("view:toggle-console", {
    label: "Toggle Console",
    description: "Show or hide the live log console panel",
    group: "View",
    action: onToggleConsole,
  })
  useCommand("view:toggle-settings", {
    label: "Toggle Settings",
    description: "Show or hide the settings panel",
    group: "View",
    action: onToggleSettings,
  })
  useCommand("jobs:queue", {
    label: "Queue Job…",
    description: "Submit a new job to a running capability",
    group: "Jobs",
    action: onQueueJob,
  })
  return null
}

export function AppShell({
  settings, saving, onUpdateGeneral, onUpdateCapability, onUpdateProvider, breadcrumb, children,
}: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
  const [queueOpen, setQueueOpen] = useState(false)
  const { caps } = useAppState()
  const logStream = useLogStream({ store: connectionStore })

  const tunnel = settings?.tunnel
  const canShare = tunnel?.status === "Running" && !!tunnel.hostname && !!tunnel.accessToken

  const navLinkClass = ({ isActive }: { isActive: boolean }) =>
    navTabClass(isActive)

  return (
    <UtilityAppShell
      breadcrumb={breadcrumb}
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
        <NavTabs>
          <NavLink to="/" end className={navLinkClass}
            data-command="Capabilities" data-command-shortcut="F1" data-command-group="Navigate">
            <i className="fa-solid fa-grid-2 text-xs" />
            <span>Capabilities</span>
          </NavLink>
          <NavLink to="/jobs" className={navLinkClass}
            data-command="Jobs" data-command-shortcut="F2" data-command-group="Navigate">
            <i className="fa-solid fa-list text-xs" />
            <span>Jobs</span>
          </NavLink>
          <NavLink to="/stats" className={navLinkClass}
            data-command="Stats" data-command-shortcut="F3" data-command-group="Navigate">
            <i className="fa-solid fa-chart-simple text-xs" />
            <span>Stats</span>
          </NavLink>
        </NavTabs>
      }
      menuItems={
        <>
          <DropdownMenuItem onClick={() => setConsoleOpen(prev => !prev)}>
            <i className="fa-solid fa-terminal size-4 text-center" />
            Console
            {logStream.errorCount > 0 && (
              <span className="ml-auto text-[10px] text-destructive font-medium">
                {logStream.errorCount}
              </span>
            )}
          </DropdownMenuItem>
          <DropdownMenuItem onClick={() => setSettingsOpen(prev => !prev)}>
            <i className="fa-solid fa-gear size-4 text-center" />
            Settings
          </DropdownMenuItem>
        </>
      }
      className="flex flex-col h-dvh w-full"
    >
      <ShellCommands
        onToggleConsole={() => setConsoleOpen(prev => !prev)}
        onToggleSettings={() => setSettingsOpen(prev => !prev)}
        onQueueJob={() => setQueueOpen(true)}
      />
      <QueueJobDialog
        open={queueOpen}
        onOpenChange={setQueueOpen}
        capabilities={caps.capabilities}
      />
      {(consoleOpen || settingsOpen) ? (
        <ResizablePanelGroup orientation="horizontal" className="flex-1 min-h-0">
          <ResizablePanel defaultSize={consoleOpen && settingsOpen ? 55 : 75} minSize={30}>
            <main className="h-full overflow-auto">{children}</main>
          </ResizablePanel>
          {consoleOpen && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel defaultSize={settingsOpen ? 20 : 25} minSize={15}>
                <LogPanel
                  entries={logStream.entries}
                  connected={logStream.connected}
                  paused={logStream.paused}
                  onPauseChange={logStream.setPaused}
                  onClear={logStream.clear}
                  onRefresh={() => logStream.refresh()}
                  errorCount={logStream.errorCount}
                  warnCount={logStream.warnCount}
                />
              </ResizablePanel>
            </>
          )}
          {settingsOpen && (
            <>
              <ResizableHandle withHandle />
              <ResizablePanel defaultSize={consoleOpen ? 25 : 25} minSize={15}>
                <SettingsPanel
                  onClose={() => setSettingsOpen(false)}
                  settings={settings}
                  saving={saving}
                  onUpdateGeneral={onUpdateGeneral}
                  onUpdateCapability={onUpdateCapability}
                  onUpdateProvider={onUpdateProvider}
                />
              </ResizablePanel>
            </>
          )}
        </ResizablePanelGroup>
      ) : (
        <main className="flex-1 min-h-0 overflow-auto">
          {children}
        </main>
      )}
    </UtilityAppShell>
  )
}
