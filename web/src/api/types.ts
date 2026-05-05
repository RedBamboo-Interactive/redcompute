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

export interface CapabilityStatus {
  slug: string
  displayName: string
  type: string
  status: BackendStatus
  provider?: string
  sleeping: boolean
  endpoints?: EndpointManifest[]
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
    enabled: boolean
    activeProvider?: string
    providers: Record<string, Record<string, unknown>>
  }>
}

export interface TagInfo {
  tag: string
  category: string
  color: string
  recentCount: number
}

export interface TagCount {
  [tag: string]: number
}

export interface WsEvent {
  type: "job.created" | "job.updated" | "log.entry" | "capability.status" | "tunnel.status"
    | "claude.session.created" | "claude.session.updated" | "claude.session.ended" | "claude.stream"
  data: unknown
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
  permissionMode?: PermissionMode
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

export interface ProjectInfo {
  name: string
  path: string
  hasClaudeMd: boolean
  hasIcon: boolean
}
