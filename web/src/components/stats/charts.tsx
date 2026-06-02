import { ChartCard, RbBarChart, RbAreaChart, RbDonutChart, CHART_PALETTE } from "@redbamboo/ui"
import type { TimeBucket, DurationBucket, GroupCount, CostBucket, CostBySource } from "@/lib/stats-utils"
import { formatDurationCompact, formatCost } from "@/lib/stats-utils"

export function JobsOverTimeChart({ data }: { data: TimeBucket[] }) {
  return (
    <ChartCard title="Jobs over time">
      <RbBarChart
        data={data}
        xKey="label"
        series={[
          { dataKey: "completed", name: "Completed", color: "#26A69A" },
          { dataKey: "failed", name: "Failed", color: "#E55B5B" },
          { dataKey: "cancelled", name: "Cancelled", color: "#727C7D" },
        ]}
        stacked
        emptyMessage="No job data for this period"
      />
    </ChartCard>
  )
}

export function StatusDonut({ data, total }: { data: GroupCount[]; total: number }) {
  const STATUS_COLORS: Record<string, string> = {
    Completed: "#26A69A", Failed: "#E55B5B", Cancelled: "#727C7D",
    Queued: "#D4AA4F", Running: "#D4AA4F",
  }
  const segments = data.map(d => ({ name: d.name, value: d.count, color: STATUS_COLORS[d.name] || "#9B9CA2" }))
  return (
    <ChartCard title="Status breakdown">
      <RbDonutChart
        data={segments}
        centerLabel={total}
        showLegend
        emptyMessage="No jobs yet"
      />
    </ChartCard>
  )
}

export function DurationChart({ data }: { data: DurationBucket[] }) {
  return (
    <ChartCard title="Avg duration over time">
      <RbAreaChart
        data={data}
        xKey="label"
        series={[{ dataKey: "avgMs", color: "#26A69A" }]}
        formatY={formatDurationCompact}
        formatTooltip={(v: number) => formatDurationCompact(v)}
        emptyMessage="No completed jobs with duration data"
      />
    </ChartCard>
  )
}

export function SourceBarChart({ data, label }: { data: GroupCount[]; label: string }) {
  const segments = data.slice(0, 8).map((d, i) => ({ name: d.name, value: d.count, color: CHART_PALETTE[i % CHART_PALETTE.length] }))
  const total = segments.reduce((s, d) => s + d.value, 0)
  return (
    <ChartCard title={label}>
      <RbDonutChart data={segments} centerLabel={total} showLegend emptyMessage="No data" />
    </ChartCard>
  )
}

export function CostChart({ data }: { data: CostBucket[] }) {
  return (
    <ChartCard title="Cost over time">
      <RbAreaChart
        data={data}
        xKey="label"
        series={[{ dataKey: "cost", color: "#D4AA4F" }]}
        formatY={(v: number) => formatCost(v)}
        formatTooltip={(v: number) => formatCost(v)}
        emptyMessage="No cost data available"
      />
    </ChartCard>
  )
}

export function CostBySourcePie({ data, totalCost }: { data: CostBySource[]; totalCost: number }) {
  const segments = data.slice(0, 8).map((d, i) => ({ name: d.name, value: d.cost, color: CHART_PALETTE[i % CHART_PALETTE.length] }))
  return (
    <ChartCard title="Cost by source">
      <RbDonutChart
        data={segments}
        centerLabel={formatCost(totalCost)}
        showLegend
        formatTooltip={(v: number) => formatCost(v)}
        emptyMessage="No cost data"
      />
    </ChartCard>
  )
}
