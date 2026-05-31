import { createContext, useContext } from "react"
import type { useCapabilities } from "@/hooks/use-capabilities"
import type { useHardware } from "@/hooks/use-hardware"
import type { useJobs, JobFilters } from "@/hooks/use-jobs"
import type { useSettings } from "@/hooks/use-settings"

interface AppState {
  caps: ReturnType<typeof useCapabilities>
  hardware: ReturnType<typeof useHardware>
  jobs: ReturnType<typeof useJobs>
  jobFilters: JobFilters
  setJobFilters: (f: JobFilters) => void
  settings: ReturnType<typeof useSettings>
}

const AppStateContext = createContext<AppState>(null!)

export function AppStateProvider({ value, children }: { value: AppState; children: React.ReactNode }) {
  return <AppStateContext.Provider value={value}>{children}</AppStateContext.Provider>
}

export function useAppState() {
  return useContext(AppStateContext)
}
