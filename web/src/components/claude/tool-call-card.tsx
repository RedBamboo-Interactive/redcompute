import { useState } from "react"

interface Props {
  toolName: string
  toolInput?: string
  toolResult?: string
}

export function ToolCallCard({ toolName, toolInput, toolResult }: Props) {
  const [expanded, setExpanded] = useState(false)

  return (
    <div className="border border-border-subtle rounded-lg overflow-hidden my-1.5">
      <button
        onClick={() => setExpanded(!expanded)}
        className="w-full flex items-center gap-2 px-3 py-1.5 text-xs bg-white/[0.03] hover:bg-white/[0.06] transition-colors"
      >
        <i className={`fa-solid fa-chevron-right transition-transform text-[10px] ${expanded ? "rotate-90" : ""}`} />
        <i className="fa-solid fa-wrench text-text-muted" />
        <span className="font-mono font-medium text-amber-300">{toolName}</span>
        {toolResult && !expanded && (
          <span className="ml-auto text-text-muted truncate max-w-[200px]">
            {toolResult.slice(0, 60)}{toolResult.length > 60 ? "..." : ""}
          </span>
        )}
      </button>
      {expanded && (
        <div className="border-t border-border-subtle">
          {toolInput && (
            <div className="p-2 border-b border-border-subtle">
              <div className="text-[10px] uppercase text-text-muted mb-1 font-semibold">Input</div>
              <pre className="text-xs font-mono whitespace-pre-wrap break-all text-text-muted max-h-40 overflow-y-auto">
                {formatJson(toolInput)}
              </pre>
            </div>
          )}
          {toolResult && (
            <div className="p-2">
              <div className="text-[10px] uppercase text-text-muted mb-1 font-semibold">Result</div>
              <pre className="text-xs font-mono whitespace-pre-wrap break-all max-h-60 overflow-y-auto">
                {toolResult.slice(0, 2000)}{toolResult.length > 2000 ? "\n..." : ""}
              </pre>
            </div>
          )}
        </div>
      )}
    </div>
  )
}

function formatJson(s: string): string {
  try {
    return JSON.stringify(JSON.parse(s), null, 2)
  } catch {
    return s
  }
}
