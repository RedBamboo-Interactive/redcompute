import { useState } from "react"
import { setToken } from "@/api/auth"

export function TokenPrompt({ onAuthenticated }: { onAuthenticated: () => void }) {
  const [value, setValue] = useState("")
  const [error, setError] = useState<string | null>(null)
  const [checking, setChecking] = useState(false)

  async function handleSubmit(e: React.FormEvent) {
    e.preventDefault()
    if (!value.trim()) return

    setChecking(true)
    setError(null)

    try {
      const res = await fetch("/status", {
        headers: { Authorization: `Bearer ${value.trim()}` },
      })
      if (res.ok) {
        setToken(value.trim())
        onAuthenticated()
      } else {
        setError("Invalid access token")
      }
    } catch {
      setError("Cannot reach server")
    } finally {
      setChecking(false)
    }
  }

  return (
    <div className="fixed inset-0 bg-[#0a0a0b] flex items-center justify-center z-50">
      <form onSubmit={handleSubmit} className="bg-surface-elevated rounded-lg p-6 w-[360px]">
        <h2 className="text-[16px] font-semibold text-white mb-1">RedCompute</h2>
        <p className="text-[12px] text-text-muted mb-4">Enter your access token to continue</p>

        <input
          type="password"
          className="w-full bg-[#1a1a1f] border border-white/10 rounded px-3 py-2 text-[13px] text-white font-mono outline-none focus:border-white/30 mb-2"
          placeholder="Access token"
          value={value}
          onChange={(e) => setValue(e.target.value)}
          autoFocus
        />

        {error && <p className="text-[12px] text-red-400 mb-2">{error}</p>}

        <button
          type="submit"
          disabled={checking || !value.trim()}
          className="w-full bg-white/10 hover:bg-white/15 disabled:opacity-40 text-white text-[13px] rounded px-3 py-2 transition-colors"
        >
          {checking ? "Checking..." : "Connect"}
        </button>
      </form>
    </div>
  )
}
