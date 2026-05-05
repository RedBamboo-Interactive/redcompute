import { useState } from "react"
import type { MessageBlock as MessageBlockType, MessagePart } from "@/hooks/use-claude"

const partColor: Record<string, string> = {
  thinking: "#7C4DFF",
  tool_use: "#D4AA4F",
  tool_result: "#26A69A",
  error: "#E55B5B",
}

interface Props {
  block: MessageBlockType
}

export function MessageBlock({ block }: Props) {
  if (block.role === "user") {
    return (
      <div className="flex justify-end mb-3">
        <div className="max-w-[80%] bg-white/10 rounded-xl rounded-br-sm px-4 py-2.5">
          <p className="text-sm whitespace-pre-wrap">{block.parts[0]?.content}</p>
        </div>
      </div>
    )
  }

  const groups = groupParts(block.parts)

  return (
    <div className="mb-4">
      <div className="max-w-full">
        {groups.map((group, i) =>
          group.kind === "text" ? (
            <div key={i} className="text-sm whitespace-pre-wrap leading-relaxed">
              {group.parts[0].content}
            </div>
          ) : (
            <PartFrieze key={i} parts={group.parts} />
          )
        )}
      </div>
    </div>
  )
}

type PartGroup =
  | { kind: "text"; parts: [MessagePart] }
  | { kind: "frieze"; parts: MessagePart[] }

function groupParts(parts: MessagePart[]): PartGroup[] {
  const groups: PartGroup[] = []
  let frieze: MessagePart[] = []

  const flushFrieze = () => {
    if (frieze.length > 0) {
      groups.push({ kind: "frieze", parts: frieze })
      frieze = []
    }
  }

  for (const part of parts) {
    if (part.type === "text") {
      flushFrieze()
      groups.push({ kind: "text", parts: [part] })
    } else {
      frieze.push(part)
    }
  }
  flushFrieze()
  return groups
}

function partLabel(part: MessagePart): string {
  switch (part.type) {
    case "thinking": return "Thinking"
    case "tool_use": return part.toolName || "Tool"
    case "tool_result": return "Result"
    case "error": return "Error"
    default: return part.type
  }
}

function PartFrieze({ parts }: { parts: MessagePart[] }) {
  const [selected, setSelected] = useState<MessagePart | null>(null)

  return (
    <>
      <div className="flex flex-wrap gap-[3px] py-1.5">
        {parts.map((part, i) => (
          <button
            key={i}
            onClick={() => setSelected(part)}
            className="w-2.5 h-2.5 rounded-[2px] transition-all duration-100 hover:brightness-125 hover:scale-[1.5] cursor-pointer"
            style={{ backgroundColor: partColor[part.type] || "#555" }}
            title={partLabel(part)}
          />
        ))}
      </div>

      {selected && (
        <PartModal part={selected} onClose={() => setSelected(null)} />
      )}
    </>
  )
}

function PartModal({ part, onClose }: { part: MessagePart; onClose: () => void }) {
  return (
    <div className="fixed inset-0 z-50 flex items-center justify-center" onClick={onClose}>
      <div className="absolute inset-0 bg-black/60" />
      <div
        className="relative bg-surface-elevated border border-border-subtle rounded-xl shadow-2xl w-full max-w-2xl max-h-[70vh] flex flex-col mx-4"
        onClick={e => e.stopPropagation()}
      >
        <div className="flex items-center gap-2.5 px-4 py-3 border-b border-border-subtle shrink-0">
          <div
            className="w-3 h-3 rounded-[2px]"
            style={{ backgroundColor: partColor[part.type] || "#555" }}
          />
          <span className="font-medium text-sm">{partLabel(part)}</span>
          <span className="text-xs text-text-muted">{part.type}</span>
          <button
            onClick={onClose}
            className="ml-auto text-text-muted hover:text-white transition-colors"
          >
            <i className="fa-solid fa-xmark" />
          </button>
        </div>

        <div className="overflow-y-auto p-4 flex-1 min-h-0">
          {part.type === "tool_use" && part.toolInput && (
            <div className="mb-3">
              <div className="text-[10px] uppercase text-text-muted mb-1 font-semibold">Input</div>
              <pre className="text-xs font-mono whitespace-pre-wrap break-all text-text-muted">
                {formatJson(part.toolInput)}
              </pre>
            </div>
          )}
          {part.content && (
            <div>
              {part.type === "tool_use" && part.toolInput && (
                <div className="text-[10px] uppercase text-text-muted mb-1 font-semibold">Content</div>
              )}
              <pre className="text-xs font-mono whitespace-pre-wrap break-all">
                {part.content.slice(0, 5000)}{part.content.length > 5000 ? "\n..." : ""}
              </pre>
            </div>
          )}
          {!part.content && !part.toolInput && (
            <p className="text-sm text-text-muted italic">No content</p>
          )}
        </div>
      </div>
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
