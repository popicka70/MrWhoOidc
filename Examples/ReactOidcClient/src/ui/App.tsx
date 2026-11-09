import { Link, NavLink, Outlet, useLocation } from 'react-router-dom'
import { useMemo } from 'react'
import { getUser, startFrontChannelLogout, startLogin } from '../oidc/client'

export function App() {
  // The auth state lives in sessionStorage and every login/logout coincides with a route
  // change (callback -> '/', logout -> full page navigation). Re-reading the user on each
  // navigation keeps the header button in sync; without this the App captured a stale
  // snapshot when mounted on /callback and kept showing "Login" after sign-in.
  const location = useLocation()
  const user = useMemo(() => getUser(), [location.pathname])

  return (
    <div className="min-h-screen flex flex-col">
      <header className="backdrop-blur bg-slate-900/70 border-b border-slate-800 sticky top-0">
        <div className="max-w-5xl mx-auto px-4 py-3 flex items-center justify-between">
          <Link to="/" className="text-xl font-semibold tracking-tight">MrWho OIDC React Demo</Link>
          <nav className="flex items-center gap-6">
            <NavLink to="/" className={({isActive}) => isActive ? 'text-primary' : 'text-slate-300 hover:text-white'}>Home</NavLink>
            {user ? (
              <button onClick={startFrontChannelLogout} className="px-3 py-1.5 rounded-md bg-primary hover:brightness-110 text-slate-900 font-medium">Logout</button>
            ) : (
              <button onClick={startLogin} className="px-3 py-1.5 rounded-md bg-primary hover:brightness-110 text-slate-900 font-medium">Login</button>
            )}
          </nav>
        </div>
      </header>
      <main className="flex-1">
        <div className="max-w-5xl mx-auto px-4 py-8">
          <Outlet />
        </div>
      </main>
      <footer className="border-t border-slate-800">
        <div className="max-w-5xl mx-auto px-4 py-6 text-sm text-slate-400">Powered by MrWhoOidc</div>
      </footer>
    </div>
  )
}
