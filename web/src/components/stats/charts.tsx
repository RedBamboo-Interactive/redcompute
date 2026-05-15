import type { ReactNode } from "react"
import {
  ResponsiveContainer, BarChart, Bar, AreaChart, Area, PieChart, Pie, Cell,
  XAxis, YAxis, CartesianGrid, Tooltip as RechartsTooltip,
} from "recharts"
import type { TimeBucket, DurationBucket, GroupCount, CostBucket } from "@/lib/stats-utils"
import { formatDurationCompact, formatCost } from "@/lib/stats-utils"

const STATUS_COLORS = {
  completed: "#26A69A",
  failed: "#E55B5B",
  cancelled: "#727C7D",
} as const

const GRID_COLOR = "rgba(255,255,255,0.06)"
const AXIS_COLOR = "#9B9CA2"
const PURPLE = "#7C4DFF"

function ChartCard({ title, children, className }: { title: string; children: ReactNode; className?: string }) {
  return (
    <div className={`bg-surface-elevated rounded-xl shadow-[0_1px_6px_rgba(0,0,0,0.15)] p-5 ${className || ""}`}>
      <h3 className="text-[13px] font-medium text-contrast mb-4">{title}</h3>
      {children}
    </div>
  )
}

function ChartTooltipContent({ payload, label }: { payload?: Array<{ name: string; value: number; color: string }>; label?: string }) {
  if (!payload?.length) return null
  return (
    <div className="bg-[#2E3038] border border-[#43454F] rounded-lg px-3 py-2 text-xs shadow-lg">
      {label && <div className="text-text-muted mb-1">{label}</div>}
      {payload.map((entry, i) => (
        <div key={i} className="flex items-center gap-2">
          <div className="w-2 h-2 rounded-full shrink-0" style={{ backgroundColor: entry.color }} />
          <span className="text-contrast">{entry.name}: <span className="font-mono">{entry.value}</span></span>
        </div>
      ))}
    </div>
  )
}

export function JobsOverTimeChart({ data }: { data: TimeBucket[] }) {
  const hasData = data.some(d => d.completed + d.failed + d.cancelled > 0)

  return (
    <ChartCard title="Jobs over time">
      {hasData ? (
        <ResponsiveContainer width="100%" height={220}>
          <BarChart data={data} barCategoryGap="20%">
            <CartesianGrid stroke={GRID_COLOR} strokeDasharray="3 3" vertical={false} />
            <XAxis dataKey="label" tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} interval="preserveStartEnd" />
            <YAxis tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} allowDecimals={false} width={30} />
            <RechartsTooltip content={<ChartTooltipContent />} cursor={{ fill: "rgba(255,255,255,0.03)" }} />
            <Bar dataKey="completed" name="Completed" fill={STATUS_COLORS.completed} stackId="a" radius={[0, 0, 0, 0]} />
            <Bar dataKey="failed" name="Failed" fill={STATUS_COLORS.failed} stackId="a" radius={[0, 0, 0, 0]} />
            <Bar dataKey="cancelled" name="Cancelled" fill={STATUS_COLORS.cancelled} stackId="a" radius={[2, 2, 0, 0]} />
          </BarChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[220px] text-text-disabled text-xs">No job data for this period</div>
      )}
    </ChartCard>
  )
}

const STATUS_DONUT_COLORS: Record<string, string> = {
  Completed: STATUS_COLORS.completed,
  Failed: STATUS_COLORS.failed,
  Cancelled: STATUS_COLORS.cancelled,
  Queued: "#D4AA4F",
  Running: "#D4AA4F",
}

export function StatusDonut({ data, total }: { data: GroupCount[]; total: number }) {
  const hasData = data.some(d => d.count > 0)

  return (
    <ChartCard title="Status breakdown">
      {hasData ? (
        <ResponsiveContainer width="100%" height={220}>
          <PieChart>
            <Pie
              data={data}
              dataKey="count"
              nameKey="name"
              cx="50%"
              cy="50%"
              innerRadius={55}
              outerRadius={85}
              paddingAngle={2}
              strokeWidth={0}
            >
              {data.map((entry, i) => (
                <Cell key={i} fill={STATUS_DONUT_COLORS[entry.name] || "#9B9CA2"} />
              ))}
            </Pie>
            <RechartsTooltip content={<ChartTooltipContent />} />
            <text x="50%" y="50%" textAnchor="middle" dominantBaseline="central"
              style={{ fill: "#D0D1D6", fontSize: 20, fontWeight: 600, fontFamily: "var(--font-mono)" }}>
              {total}
            </text>
          </PieChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[220px] text-text-disabled text-xs">No jobs yet</div>
      )}
      {hasData && (
        <div className="flex flex-wrap gap-3 mt-1">
          {data.map(d => (
            <div key={d.name} className="flex items-center gap-1.5 text-[10px]">
              <div className="w-2 h-2 rounded-full" style={{ backgroundColor: STATUS_DONUT_COLORS[d.name] || "#9B9CA2" }} />
              <span className="text-text-muted">{d.name}</span>
              <span className="font-mono text-text-disabled">{d.count}</span>
            </div>
          ))}
        </div>
      )}
    </ChartCard>
  )
}

function DurationTooltipContent({ payload, label }: { payload?: Array<{ value: number }>; label?: string }) {
  if (!payload?.length || !payload[0].value) return null
  return (
    <div className="bg-[#2E3038] border border-[#43454F] rounded-lg px-3 py-2 text-xs shadow-lg">
      {label && <div className="text-text-muted mb-1">{label}</div>}
      <div className="text-contrast font-mono">{formatDurationCompact(payload[0].value)}</div>
    </div>
  )
}

export function DurationChart({ data }: { data: DurationBucket[] }) {
  const hasData = data.some(d => d.avgMs > 0)

  return (
    <ChartCard title="Avg duration over time">
      {hasData ? (
        <ResponsiveContainer width="100%" height={220}>
          <AreaChart data={data}>
            <CartesianGrid stroke={GRID_COLOR} strokeDasharray="3 3" vertical={false} />
            <XAxis dataKey="label" tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} interval="preserveStartEnd" />
            <YAxis tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} width={45}
              tickFormatter={v => formatDurationCompact(v)} />
            <RechartsTooltip content={<DurationTooltipContent />} cursor={{ stroke: "rgba(255,255,255,0.1)" }} />
            <Area type="monotone" dataKey="avgMs" stroke="#26A69A" fill="#26A69A" fillOpacity={0.15} strokeWidth={2} dot={false} />
          </AreaChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[220px] text-text-disabled text-xs">No completed jobs with duration data</div>
      )}
    </ChartCard>
  )
}

export function SourceBarChart({ data, label }: { data: GroupCount[]; label: string }) {
  const items = data.slice(0, 8)
  const hasData = items.length > 0
  const height = Math.max(160, items.length * 32)

  return (
    <ChartCard title={label}>
      {hasData ? (
        <ResponsiveContainer width="100%" height={height}>
          <BarChart data={items} layout="vertical" margin={{ left: 0, right: 12 }}>
            <CartesianGrid stroke={GRID_COLOR} strokeDasharray="3 3" horizontal={false} />
            <XAxis type="number" tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} allowDecimals={false} />
            <YAxis type="category" dataKey="name" tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} width={80} />
            <RechartsTooltip content={<ChartTooltipContent />} cursor={{ fill: "rgba(255,255,255,0.03)" }} />
            <Bar dataKey="count" name="Jobs" fill={PURPLE} radius={[0, 3, 3, 0]} barSize={16} />
          </BarChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[160px] text-text-disabled text-xs">No data</div>
      )}
    </ChartCard>
  )
}

function CostTooltipContent({ payload, label }: { payload?: Array<{ value: number }>; label?: string }) {
  if (!payload?.length || !payload[0].value) return null
  return (
    <div className="bg-[#2E3038] border border-[#43454F] rounded-lg px-3 py-2 text-xs shadow-lg">
      {label && <div className="text-text-muted mb-1">{label}</div>}
      <div className="text-contrast font-mono">{formatCost(payload[0].value)}</div>
    </div>
  )
}

export function CostChart({ data }: { data: CostBucket[] }) {
  const hasData = data.some(d => d.cost > 0)

  return (
    <ChartCard title="Cost over time">
      {hasData ? (
        <ResponsiveContainer width="100%" height={220}>
          <AreaChart data={data}>
            <CartesianGrid stroke={GRID_COLOR} strokeDasharray="3 3" vertical={false} />
            <XAxis dataKey="label" tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} interval="preserveStartEnd" />
            <YAxis tick={{ fill: AXIS_COLOR, fontSize: 10 }} tickLine={false} axisLine={false} width={45}
              tickFormatter={v => formatCost(v)} />
            <RechartsTooltip content={<CostTooltipContent />} cursor={{ stroke: "rgba(255,255,255,0.1)" }} />
            <Area type="monotone" dataKey="cost" stroke="#D4AA4F" fill="#D4AA4F" fillOpacity={0.15} strokeWidth={2} dot={false} />
          </AreaChart>
        </ResponsiveContainer>
      ) : (
        <div className="flex items-center justify-center h-[220px] text-text-disabled text-xs">No cost data available</div>
      )}
    </ChartCard>
  )
}
