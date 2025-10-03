import { useEffect } from 'react'
import { startFrontChannelLogout } from '../oidc/client'

export function Logout() {
  useEffect(() => {
    startFrontChannelLogout()
  }, [])

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Signing you out…</h1>
    </div>
  )
}
