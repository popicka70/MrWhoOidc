import { useEffect, useState } from 'react'
import { handleRedirectCallback } from '../oidc/client'
import { useNavigate, useSearchParams } from 'react-router-dom'

export function Callback() {
  const [error, setError] = useState<string | null>(null)
  const [searchParams] = useSearchParams()
  const navigate = useNavigate()

  useEffect(() => {
    const run = async () => {
      try {
        await handleRedirectCallback(searchParams.toString())
        navigate('/')
      } catch (e: any) {
        setError(e?.message ?? 'Login failed')
      }
    }
    void run()
  }, [searchParams, navigate])

  return (
    <div className="space-y-4">
      <h1 className="text-2xl font-bold">Signing you in…</h1>
      {error && <div className="text-red-400">{error}</div>}
    </div>
  )
}
