import { Outlet, useMatches, useNavigate } from "react-router-dom"
import { TooltipProvider, Breadcrumb } from "@redbamboo/ui"
import { BreadcrumbLabelProvider, buildBreadcrumbs, useBreadcrumbLabelsContext, useNavigateUp } from "@redbamboo/utility"
import type { RouteMatch } from "@redbamboo/utility"
import { AppShell } from "./app-shell"
import { useAppState } from "@/contexts/app-state"

function BreadcrumbNav() {
  const matches = useMatches()
  const labelCtx = useBreadcrumbLabelsContext()
  const navigate = useNavigate()
  const items = buildBreadcrumbs(matches as RouteMatch[], labelCtx?.labels)

  useNavigateUp({
    getParentPath: () => {
      if (items.length <= 1) return null
      return items[items.length - 2]?.href ?? null
    },
    navigate,
  })

  if (items.length <= 1) return null
  return <Breadcrumb items={items} onNavigate={navigate} />
}

export function AppLayout() {
  const { settings } = useAppState()

  return (
    <TooltipProvider>
      <BreadcrumbLabelProvider>
        <AppShell
          settings={settings.settings}
          saving={settings.saving}
          onUpdateGeneral={settings.updateGeneral}
          onUpdateCapability={settings.updateCapability}
          onUpdateProvider={settings.updateProvider}
          breadcrumb={<BreadcrumbNav />}
        >
          <Outlet />
        </AppShell>
      </BreadcrumbLabelProvider>
    </TooltipProvider>
  )
}
