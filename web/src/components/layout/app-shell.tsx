import { useState, type ReactNode } from "react"
import { NavLink } from "react-router-dom"
import { AppShell as UtilityAppShell, useLogStream, LogPanel } from "@redbamboo/utility"
import { DropdownMenuItem, NavTabs, navTabClass, ResizablePanelGroup, ResizablePanel, ResizableHandle } from "@redbamboo/ui"
import { SettingsModal } from "./settings-modal"
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

export function AppShell({
  settings, saving, onUpdateGeneral, onUpdateCapability, onUpdateProvider, breadcrumb, children,
}: Props) {
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [consoleOpen, setConsoleOpen] = useState(false)
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
          <DropdownMenuItem onClick={() => setSettingsOpen(true)}>
            <i className="fa-solid fa-gear size-4 text-center" />
            Settings
          </DropdownMenuItem>
        </>
      }
      className="flex flex-col h-dvh w-full"
    >
      {consoleOpen ? (
        <ResizablePanelGroup orientation="horizontal" className="flex-1 min-h-0">
          <ResizablePanel defaultSize={75} minSize={30}>
            <main className="h-full overflow-auto">
              {children}
            </main>
          </ResizablePanel>
          <ResizableHandle withHandle />
          <ResizablePanel defaultSize={25} minSize={15}>
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
        </ResizablePanelGroup>
      ) : (
        <main className="flex-1 min-h-0 overflow-auto">
          {children}
        </main>
      )}

      <SettingsModal
        open={settingsOpen}
        onOpenChange={setSettingsOpen}
        settings={settings}
        saving={saving}
        onUpdateGeneral={onUpdateGeneral}
        onUpdateCapability={onUpdateCapability}
        onUpdateProvider={onUpdateProvider}
      />
    </UtilityAppShell>
  )
}
