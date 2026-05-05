import { useState } from "react"
import type { MessageBlock as MessageBlockType, MessagePart } from "@/hooks/use-claude"
import { ToolCallCard } from "./tool-call-card"

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

  return (
    <div className="mb-4">
      <div className="max-w-full">
        {block.parts.map((part, i) => (
          <PartRenderer key={i} part={part} />
        ))}
      </div>
    </div>
  )
}

function PartRenderer({ part }: { part: MessagePart }) {
  switch (part.type) {
    case "text":
      return (
        <div className="text-sm whitespace-pre-wrap leading-relaxed">
          {part.content}
        </div>
      )

    case "thinking":
      return <ThinkingBlock content={part.content} />

    case "tool_use":
      return (
        <ToolCallCard
          toolName={part.toolName || "unknown"}
          toolInput={part.toolInput}
        />
      )

    case "tool_result":
      return (
        <ToolCallCard
          toolName="result"
          toolResult={part.content}
        />
      )

    case "error":
      return (
        <div className="my-1.5 px-3 py-2 rounded-lg bg-red-500/10 border border-red-500/20 text-sm text-red-300">
          {part.content}
        </div>
      )

    default:
      return null
  }
}

function ThinkingBlock({ content }: { content: string }) {
  const [expanded, setExpanded] = useState(false)

  if (!content) return null

  return (
    <div className="my-1.5">
      <button
        onClick={() => setExpanded(!expanded)}
        className="flex items-center gap-1.5 text-xs text-text-muted hover:text-white/70 transition-colors"
      >
        <i className={`fa-solid fa-chevron-right transition-transform text-[10px] ${expanded ? "rotate-90" : ""}`} />
        <i className="fa-solid fa-brain text-purple-400/60" />
        <span>Thinking...</span>
      </button>
      {expanded && (
        <div className="mt-1 ml-5 pl-3 border-l border-purple-400/20 text-sm text-text-muted italic whitespace-pre-wrap max-h-60 overflow-y-auto">
          {content}
        </div>
      )}
    </div>
  )
}
