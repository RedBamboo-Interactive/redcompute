export type JobStatus = "Queued" | "Running" | "Completed" | "Failed" | "Cancelled"
export type BackendStatus = "Stopped" | "Starting" | "Running" | "Error" | "Draining"

export interface JobRecord {
  id: string
  capabilitySlug: string
  providerName: string
  status: JobStatus
  queuedAt: string
  startedAt?: string
  completedAt?: string
  inputJson: string
  outputLocation?: string
  outputSizeBytes?: number
  outputContentType?: string
  progress?: number
  resultJson?: string
  errorMessage?: string
  errorDetails?: string
  callerInfo?: string
  idempotencyKey?: string
  name?: string
  rationale?: string
  durationMs?: number
  costUsd?: number
  sessionStatus?: SessionStatus
}

export interface LogEntry {
  id: number
  timestamp: string
  tag: string
  tagCategory: string
  message: string
  fullMessage: string
  tagColor: string
  isMultiline: boolean
  isError: boolean
  jobId?: string
}

export interface ProviderStatus {
  name: string
  type: string
  status: BackendStatus
}

export interface CapabilityStatus {
  slug: string
  displayName: string
  type: string
  status: BackendStatus
  provider?: string
  defaultProvider?: string
  providers?: ProviderStatus[]
  sleeping: boolean
  disabled: boolean
  endpoints?: EndpointManifest[]
  icon?: string
  color?: string
  description?: string
  category?: string
  rerunnable?: boolean
}

export interface EndpointManifest {
  method: string
  path: string
  description: string
  parameters?: Record<string, ParameterSchema>
  returns?: { contentType: string; streaming: boolean; mediaCategory?: string; outputEndpoint?: string }
}

export interface ParameterSchema {
  type: string
  required: boolean
  description?: string
  default?: unknown
  enum?: string[]
  min?: number
  max?: number
}

export interface StatusResponse {
  service: string
  uptime: string
  capabilities: CapabilityStatus[]
}

export interface TunnelSettings {
  enabled: boolean
  accessToken: string | null
  tunnelToken: string | null
  hostname: string | null
  cloudflaredPath: string | null
  status: "Stopped" | "Starting" | "Running" | "Error"
  error: string | null
}

export interface Settings {
  apiPort: number
  logLevel: string
  autoStartWithWindows: boolean
  configPath: string
  tunnel: TunnelSettings
  capabilities: Record<string, {
    activeProvider?: string
    providers: Record<string, Record<string, unknown>>
  }>
}

export interface WsEvent {
  type: "job.created" | "job.updated" | "capability.status" | "tunnel.status"
    | "hardware.snapshot"
    | "claude.session.created" | "claude.session.updated" | "claude.session.ended" | "claude.stream"
  data: unknown
}

// Hardware monitoring

export interface HardwareSnapshot {
  available: boolean
  timestamp: string
  cpu: { usagePercent: number; coreCount: number }
  ram: { totalBytes: number; usedBytes: number; availableBytes: number; usagePercent: number }
  gpus: GpuInfo[]
}

export interface GpuInfo {
  index: number
  name: string
  memory: { totalBytes: number; usedBytes: number; freeBytes: number }
  utilizationPercent: number
  memoryUtilizationPercent: number
  powerWatts: number
  powerLimitWatts: number
  temperatureCelsius: number
  graphicsClockMHz: number
  memoryClockMHz: number
  processes: GpuProcessInfo[]
  capabilityVram: Record<string, number>
}

export interface GpuProcessInfo {
  pid: number
  processName: string
  usedMemoryBytes: number
  capabilitySlug?: string
}

// Claude Code Sessions

export type SessionStatus = "Starting" | "Active" | "Idle" | "Stopped" | "Error"

export type PermissionMode = "plan" | "bypassPermissions" | "default" | "acceptEdits" | "dontAsk" | "auto"

export interface ClaudeSessionInfo {
  id: string
  projectName: string
  projectPath: string
  status: SessionStatus
  startedAt: string
  model?: string
  claudeSessionId?: string
  title?: string
  messageCount: number
  costUsd?: number
  inputTokens?: number
  outputTokens?: number
  cacheReadInputTokens?: number
  cacheCreationInputTokens?: number
  contextWindow?: number
  effort?: string
  permissionMode?: PermissionMode
  jobId?: string
}

export interface ClaudeMessageRecord {
  id: number
  sessionId: string
  role: string
  eventType: string
  content?: string
  toolName?: string
  toolInput?: string
  toolResult?: string
  messageId?: string
  timestamp: string
}

export interface ClaudeStreamEvent {
  type: "text" | "thinking" | "tool_use" | "tool_result" | "error" | "status"
  content?: string
  toolName?: string
  toolInput?: unknown
  toolResult?: string
  isPartial?: boolean
  messageId?: string
}

export interface ImageAttachment {
  mediaType: "image/png" | "image/jpeg" | "image/gif" | "image/webp"
  base64: string
}

export interface ProjectInfo {
  name: string
  path: string
  hasClaudeMd: boolean
  hasIcon: boolean
}
