import { useState } from 'react'
import { api } from '../api'

export function ConnectPage({ params }: { params: URLSearchParams }) {
  const connected = params.get('connected')
  const callbackError = params.get('error')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function connect() {
    setBusy(true)
    setError(null)
    try {
      const { url } = await api.connect()
      window.location.href = url // bank login; the backend callback redirects back here
    } catch (e) {
      setError((e as Error).message)
      setBusy(false)
    }
  }

  return (
    <section>
      {connected !== null && (
        <p className="ok">PKO Bank Polski connected ({connected} account(s)). Open Transactions and press Sync.</p>
      )}
      {callbackError && <p className="error">Connection failed: {callbackError}</p>}
      {error && <p className="error">{error}</p>}

      <p>
        Bank access is granted for a limited time (usually 90–180 days). Connect again when syncing reports that
        there is no active bank session.
      </p>
      <button type="button" className="primary" onClick={connect} disabled={busy}>
        {busy ? 'Redirecting…' : 'Connect PKO Bank Polski'}
      </button>
    </section>
  )
}
