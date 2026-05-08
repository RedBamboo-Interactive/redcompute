const C = {
  key: "#26A69A",
  string: "#CE9178",
  number: "#B5CEA8",
  bool: "#569CD6",
  null: "#569CD6",
  punct: "#6B6F77",
}

export function JsonHighlight({ json }: { json: string }) {
  let parsed: unknown
  try {
    parsed = JSON.parse(json)
  } catch {
    return <pre className="text-xs font-mono whitespace-pre-wrap break-all text-text-muted">{json}</pre>
  }

  return (
    <pre className="text-xs font-mono whitespace-pre-wrap break-all">
      {renderValue(parsed, 0)}
    </pre>
  )
}

function renderValue(val: unknown, depth: number): React.ReactNode {
  if (val === null) return <span style={{ color: C.null }}>null</span>
  if (typeof val === "boolean") return <span style={{ color: C.bool }}>{String(val)}</span>
  if (typeof val === "number") return <span style={{ color: C.number }}>{String(val)}</span>
  if (typeof val === "string") return <span style={{ color: C.string }}>"{escapeStr(val)}"</span>

  const indent = "  ".repeat(depth)
  const innerIndent = "  ".repeat(depth + 1)

  if (Array.isArray(val)) {
    if (val.length === 0) return <span style={{ color: C.punct }}>[]</span>
    return (
      <>
        <span style={{ color: C.punct }}>{"["}</span>
        {"\n"}
        {val.map((item, i) => (
          <span key={i}>
            {innerIndent}
            {renderValue(item, depth + 1)}
            {i < val.length - 1 && <span style={{ color: C.punct }}>,</span>}
            {"\n"}
          </span>
        ))}
        {indent}
        <span style={{ color: C.punct }}>{"]"}</span>
      </>
    )
  }

  if (typeof val === "object") {
    const entries = Object.entries(val as Record<string, unknown>)
    if (entries.length === 0) return <span style={{ color: C.punct }}>{"{}"}</span>
    return (
      <>
        <span style={{ color: C.punct }}>{"{"}</span>
        {"\n"}
        {entries.map(([k, v], i) => (
          <span key={k}>
            {innerIndent}
            <span style={{ color: C.key }}>"{k}"</span>
            <span style={{ color: C.punct }}>: </span>
            {renderValue(v, depth + 1)}
            {i < entries.length - 1 && <span style={{ color: C.punct }}>,</span>}
            {"\n"}
          </span>
        ))}
        {indent}
        <span style={{ color: C.punct }}>{"}"}</span>
      </>
    )
  }

  return String(val)
}

function escapeStr(s: string): string {
  return s.replace(/\\/g, "\\\\").replace(/"/g, '\\"').replace(/\n/g, "\\n").replace(/\t/g, "\\t")
}
