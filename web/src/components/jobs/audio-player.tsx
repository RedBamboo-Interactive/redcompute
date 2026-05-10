import { useRef, useState, useEffect, useCallback } from "react"

function formatTime(sec: number): string {
  if (!isFinite(sec) || sec < 0) return "0:00"
  const m = Math.floor(sec / 60)
  const s = Math.floor(sec % 60)
  return `${m}:${s.toString().padStart(2, "0")}`
}

interface Props {
  src: string
  label?: string | null
}

export function AudioPlayer({ src, label }: Props) {
  const audioRef = useRef<HTMLAudioElement>(null)
  const barRef = useRef<HTMLDivElement>(null)
  const [playing, setPlaying] = useState(false)
  const [duration, setDuration] = useState(0)
  const [currentTime, setCurrentTime] = useState(0)
  const [dragging, setDragging] = useState(false)

  useEffect(() => {
    const el = audioRef.current
    if (!el) return
    const onMeta = () => setDuration(el.duration)
    const onTime = () => { if (!dragging) setCurrentTime(el.currentTime) }
    const onEnd = () => { setPlaying(false); setCurrentTime(0) }
    el.addEventListener("loadedmetadata", onMeta)
    el.addEventListener("timeupdate", onTime)
    el.addEventListener("ended", onEnd)
    return () => {
      el.removeEventListener("loadedmetadata", onMeta)
      el.removeEventListener("timeupdate", onTime)
      el.removeEventListener("ended", onEnd)
    }
  }, [dragging])

  const toggle = useCallback(() => {
    const el = audioRef.current
    if (!el) return
    if (playing) { el.pause() } else { el.play() }
    setPlaying(!playing)
  }, [playing])

  const seek = useCallback((clientX: number) => {
    const bar = barRef.current
    const el = audioRef.current
    if (!bar || !el || !duration) return
    const rect = bar.getBoundingClientRect()
    const ratio = Math.max(0, Math.min(1, (clientX - rect.left) / rect.width))
    const t = ratio * duration
    el.currentTime = t
    setCurrentTime(t)
  }, [duration])

  const onPointerDown = useCallback((e: React.PointerEvent) => {
    setDragging(true)
    seek(e.clientX)
    ;(e.target as HTMLElement).setPointerCapture(e.pointerId)
  }, [seek])

  const onPointerMove = useCallback((e: React.PointerEvent) => {
    if (dragging) seek(e.clientX)
  }, [dragging, seek])

  const onPointerUp = useCallback(() => setDragging(false), [])

  const progress = duration > 0 ? (currentTime / duration) * 100 : 0

  return (
    <div className="rounded-lg bg-contrast/[0.06] px-3.5 py-3">
      {label && <p className="text-[11px] text-text-muted opacity-70 mb-2">{label}</p>}
      <audio ref={audioRef} src={src} preload="metadata" />

      <div className="flex items-center gap-3">
        {/* Play / Pause */}
        <button
          onClick={toggle}
          className="w-8 h-8 rounded-full bg-accent-teal/20 hover:bg-accent-teal/30 flex items-center justify-center transition-colors shrink-0"
        >
          <i className={`fa-solid ${playing ? "fa-pause" : "fa-play"} text-accent-teal text-[11px] ${playing ? "" : "ml-0.5"}`} />
        </button>

        {/* Time current */}
        <span className="text-[11px] font-mono text-text-muted w-9 text-right shrink-0">
          {formatTime(currentTime)}
        </span>

        {/* Progress bar */}
        <div
          ref={barRef}
          className="flex-1 h-5 flex items-center cursor-pointer group"
          onPointerDown={onPointerDown}
          onPointerMove={onPointerMove}
          onPointerUp={onPointerUp}
        >
          <div className="w-full h-1 rounded-full bg-contrast/10 relative">
            <div
              className="absolute inset-y-0 left-0 rounded-full bg-accent-teal/60 group-hover:bg-accent-teal/80 transition-colors"
              style={{ width: `${progress}%` }}
            />
            <div
              className="absolute top-1/2 -translate-y-1/2 w-2.5 h-2.5 rounded-full bg-accent-teal opacity-0 group-hover:opacity-100 transition-opacity shadow"
              style={{ left: `calc(${progress}% - 5px)` }}
            />
          </div>
        </div>

        {/* Duration */}
        <span className="text-[11px] font-mono text-text-muted w-9 shrink-0">
          {formatTime(duration)}
        </span>
      </div>
    </div>
  )
}
