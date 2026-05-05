import { useState, useEffect, useCallback } from "react"
import type { ProjectInfo } from "@/api/types"

interface Props {
  open: boolean
  onClose: () => void
  onSelect: (projectPath: string) => void
  loadProjects: () => Promise<ProjectInfo[]>
}

export function ProjectPicker({ open, onClose, onSelect, loadProjects }: Props) {
  const [projects, setProjects] = useState<ProjectInfo[]>([])
  const [loading, setLoading] = useState(false)
  const [filter, setFilter] = useState("")

  useEffect(() => {
    if (!open) return
    setLoading(true)
    loadProjects()
      .then(setProjects)
      .catch(() => setProjects([]))
      .finally(() => setLoading(false))
  }, [open, loadProjects])

  if (!open) return null

  const filtered = projects.filter(p =>
    p.name.toLowerCase().includes(filter.toLowerCase())
  )

  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/60" onClick={onClose}>
      <div
        className="bg-surface-deep border border-border-subtle rounded-xl w-full max-w-md mx-4 overflow-hidden shadow-xl"
        onClick={e => e.stopPropagation()}
      >
        <div className="p-4 border-b border-border-subtle">
          <h2 className="text-base font-semibold mb-2">Start Claude Session</h2>
          <input
            type="text"
            value={filter}
            onChange={e => setFilter(e.target.value)}
            placeholder="Filter projects..."
            autoFocus
            className="w-full bg-white/5 border border-border-subtle rounded-lg px-3 py-2 text-sm placeholder:text-text-muted focus:outline-none focus:border-white/30"
          />
        </div>
        <div className="max-h-80 overflow-y-auto">
          {loading && <p className="p-4 text-sm text-text-muted">Loading...</p>}
          {!loading && filtered.length === 0 && (
            <p className="p-4 text-sm text-text-muted">No projects found</p>
          )}
          {filtered.map(project => (
            <button
              key={project.path}
              onClick={() => { onSelect(project.path); onClose() }}
              className="w-full text-left px-4 py-3 hover:bg-white/5 transition-colors border-b border-border-subtle last:border-b-0"
            >
              <div className="flex items-center gap-2">
                <ProjectPickerIcon project={project} />
                <span className="text-sm font-medium">{project.name}</span>
                {project.hasClaudeMd && (
                  <span className="ml-auto text-xs text-green-400 bg-green-400/10 px-1.5 py-0.5 rounded">
                    CLAUDE.md
                  </span>
                )}
              </div>
            </button>
          ))}
        </div>
        <div className="p-3 border-t border-border-subtle flex justify-end">
          <button
            onClick={onClose}
            className="px-3 py-1.5 text-sm text-text-muted hover:text-white transition-colors"
          >
            Cancel
          </button>
        </div>
      </div>
    </div>
  )
}

function ProjectPickerIcon({ project }: { project: ProjectInfo }) {
  const [imgFailed, setImgFailed] = useState(false)
  const onError = useCallback(() => setImgFailed(true), [])

  if (project.hasIcon && !imgFailed) {
    return (
      <img
        src={`/claude/projects/${encodeURIComponent(project.name)}/icon`}
        alt=""
        className="w-4 h-4 object-contain"
        onError={onError}
      />
    )
  }

  return <i className="fa-solid fa-folder text-text-muted text-sm" />
}
