export function MetricCard({ label, value, icon, color, subtitle }: {
  label: string
  value: string
  icon?: string
  color?: string
  subtitle?: string
}) {
  return (
    <div className="bg-surface-elevated rounded-xl shadow-[0_1px_6px_rgba(0,0,0,0.15)] p-5">
      <div className="flex items-center gap-2.5">
        {icon && <i className={`${icon} text-sm text-text-muted opacity-50`} />}
        <span className="text-[28px] font-semibold font-mono leading-none" style={color ? { color } : undefined}>
          {value}
        </span>
      </div>
      <div className="text-[12px] text-text-muted mt-1.5">{label}</div>
      {subtitle && <div className="text-[10px] text-text-disabled mt-0.5">{subtitle}</div>}
    </div>
  )
}
