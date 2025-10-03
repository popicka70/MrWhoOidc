import { getTokens, getUser, startLogin } from '../oidc/client'

export function Home() {
  const user = getUser()
  const tokens = getTokens()

  if (!user) {
    return (
      <div className="grid md:grid-cols-2 gap-8 items-center">
        <div className="space-y-4">
          <h1 className="text-3xl font-bold">React + OIDC Demo</h1>
          <p className="text-slate-300">This demo uses Pushed Authorization Requests (PAR) and front-channel logout with the MrWho OIDC server.</p>
          <button onClick={startLogin} className="px-4 py-2 rounded-md bg-primary text-slate-900 font-medium hover:brightness-110">Login</button>
        </div>
        <div className="rounded-xl border border-slate-800 p-6 bg-gradient-to-br from-slate-800/40 to-slate-800/10">
          <div className="text-slate-400 text-sm">Configure via .env:</div>
          <ul className="text-slate-200 text-sm mt-2 list-disc pl-6 space-y-1">
            <li>VITE_OIDC_AUTHORITY (default https://mrwho.onrender.com)</li>
            <li>VITE_OIDC_CLIENT_ID (default react-demo)</li>
            <li>VITE_REDIRECT_URI (default {`${location.origin}/callback`})</li>
            <li>VITE_POST_LOGOUT_REDIRECT_URI (default {`${location.origin}/`})</li>
          </ul>
        </div>
      </div>
    )
  }

  return (
    <div className="grid md:grid-cols-2 gap-8 items-start">
      <div className="space-y-4">
        <h1 className="text-3xl font-bold">Welcome, {user.name ?? user.preferred_username ?? user.sub}</h1>
        <div className="rounded-xl border border-slate-800 p-4 bg-slate-800/30">
          <h2 className="font-semibold mb-2">User Claims</h2>
          <pre className="text-xs whitespace-pre-wrap break-all overflow-auto">{JSON.stringify(user, null, 2)}</pre>
        </div>
      </div>
      <div className="space-y-4">
        <h2 className="font-semibold">Tokens</h2>
        <div className="rounded-xl border border-slate-800 p-4 bg-slate-800/30">
          <pre className="text-xs whitespace-pre-wrap break-all overflow-auto">{JSON.stringify(tokens, null, 2)}</pre>
        </div>
      </div>
    </div>
  )
}
