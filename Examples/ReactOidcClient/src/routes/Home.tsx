import type { ReactNode } from 'react'
import { getTokens, getUser, startLogin } from '../oidc/client'

type UserClaims = Record<string, unknown>

const TIMESTAMP_CLAIMS = new Set(['exp', 'iat', 'nbf', 'auth_time', 'updated_at'])

function formatTimestampClaim(value: unknown): { text: string; raw?: string } {
  const num = typeof value === 'string' ? Number(value) : value
  if (typeof num === 'number' && Number.isFinite(num) && num > 0) {
    const date = new Date(num * 1000)
    if (!Number.isNaN(date.getTime())) {
      return { text: date.toLocaleString() }
    }
  }
  return { text: String(value) }
}

function humanizeDuration(seconds: number): string {
  if (seconds <= 0) return `${seconds}s`
  const hours = Math.floor(seconds / 3600)
  const minutes = Math.round((seconds % 3600) / 60)
  if (hours > 0) {
    return minutes > 0 ? `${hours} hour${hours > 1 ? 's' : ''} ${minutes} min` : `${hours} hour${hours > 1 ? 's' : ''}`
  }
  if (seconds >= 60) return `${minutes} minute${minutes > 1 ? 's' : ''}`
  return `${seconds}s`
}

function formatClaimValue(key: string, value: unknown): ReactNode {
  if (value === null || value === undefined || value === '') {
    return <span className="text-slate-500">—</span>
  }
  if (TIMESTAMP_CLAIMS.has(key)) {
    const { text } = formatTimestampClaim(value)
    return <span>{text}</span>
  }
  if (typeof value === 'object') {
    return <span className="break-all">{JSON.stringify(value)}</span>
  }
  return <span className="break-all">{String(value)}</span>
}

function TokenValue({ label, value }: { label: string; value: string }) {
  if (!value) {
    return (
      <div className="flex justify-between items-center py-1.5">
        <span className="text-slate-400 text-sm">{label}</span>
        <span className="text-slate-500">—</span>
      </div>
    )
  }
  const masked = value.length > 12 ? `${value.slice(0, 12)}…${value.slice(-10)}` : value
  return (
    <div className="flex justify-between items-center gap-3 py-1.5">
      <span className="text-slate-400 text-sm shrink-0">{label}</span>
      <code className="text-xs text-slate-200 break-all min-w-0">{masked}</code>
      <CopyButton value={value} />
    </div>
  )
}

function CopyButton({ value }: { value: string }) {
  const onCopy = async () => {
    try {
      await navigator.clipboard.writeText(value)
    } catch {
      // Clipboard unavailable — the label just reports failure below.
    }
  }
  return (
    <button
      className="shrink-0 px-2 py-1 text-xs rounded border border-slate-700 text-slate-300 hover:bg-slate-800"
      onClick={onCopy}
    >
      Copy
    </button>
  )
}

export function Home() {
  const user = getUser() as UserClaims | null
  const tokens = getTokens()

  if (!user) {
    return (
      <div className="grid md:grid-cols-2 gap-8 items-center">
        <div className="space-y-4">
          <h1 className="text-3xl font-bold">React + OIDC Demo</h1>
          <p className="text-slate-300">This demo uses PAR when the server advertises it, plus front-channel logout with the MrWho OIDC server.</p>
          <button onClick={startLogin} className="px-4 py-2 rounded-md bg-primary text-slate-900 font-medium hover:brightness-110">Login</button>
        </div>
        <div className="rounded-xl border border-slate-800 p-6 bg-gradient-to-br from-slate-800/40 to-slate-800/10">
          <div className="text-slate-400 text-sm">Configure via .env:</div>
          <ul className="text-slate-200 text-sm mt-2 list-disc pl-6 space-y-1">
            <li>VITE_OIDC_AUTHORITY (default https://mrwho.onrender.com)</li>
            <li>VITE_OIDC_CLIENT_ID (default react-demo)</li>
            <li>VITE_OIDC_REDIRECT_URI (default {`${location.origin}/callback`})</li>
            <li>VITE_POST_LOGOUT_REDIRECT_URI (default {`${location.origin}/`})</li>
          </ul>
        </div>
      </div>
    )
  }

  const displayName = String(
    user.name ?? user.preferred_username ?? user.sub ?? 'authenticated user',
  )
  const claims = Object.entries(user)
  const tokenEntries = tokens
    ? ([
        ['ID token', tokens.id_token],
        ['Access token', tokens.access_token],
        ['Token type', tokens.token_type],
        ['Expires in', tokens.expires_in != null ? humanizeDuration(tokens.expires_in) : undefined],
      ] as Array<[string, string | undefined]>)
    : []

  return (
    <div className="grid md:grid-cols-2 gap-8 items-start">
      <div className="space-y-4">
        <h1 className="text-3xl font-bold">Welcome, {displayName}</h1>
        <div className="rounded-xl border border-slate-800 p-4 bg-slate-800/30">
          <h2 className="font-semibold mb-2">User Claims</h2>
          <div className="text-sm">
            {claims.map(([key, value]) => (
              <div key={key} className="flex justify-between gap-4 py-1.5 border-b border-slate-800 last:border-0">
                <span className="text-slate-400 shrink-0"><code>{key}</code></span>
                <span className="text-slate-200 text-right min-w-0">{formatClaimValue(key, value)}</span>
              </div>
            ))}
          </div>
        </div>
      </div>
      <div className="space-y-4">
        <h2 className="font-semibold">Tokens</h2>
        <div className="rounded-xl border border-slate-800 p-4 bg-slate-800/30">
          <p className="text-slate-400 text-xs mb-2">Masked by default — copy the full value with the Copy button.</p>
          {tokenEntries.length === 0 ? (
            <p className="text-slate-500 text-sm">No tokens stored in this session.</p>
          ) : (
            tokenEntries.map(([label, value]) => <TokenValue key={label} label={label} value={value ?? ''} />)
          )}
        </div>
      </div>
    </div>
  )
}
