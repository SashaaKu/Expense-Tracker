import { useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { date, money } from '../format'
import { navigate } from '../nav'
import { capToToday, rangeFromParams, rangeParams, type DateRange } from '../range'
import type { Transaction } from '../types'
import { useAsync } from '../useAsync'
import { DateRangePicker } from './DateRangePicker'

// Same label the summary uses for expenses without a category.
const UNCATEGORIZED = 'Uncategorized'
// AI categorization runs in the background after a sync; refresh once it has had time to finish.
const REFRESH_AFTER_SYNC_MS = 30_000

interface Notice {
  kind: 'ok' | 'error'
  text: string
  needsConnect?: boolean
}

export function TransactionsPage({ params }: { params: URLSearchParams }) {
  const range = rangeFromParams(params)
  const category = params.get('category') ?? ''

  const categories = useAsync('categories', api.categories)
  const list = useAsync(`${range.from}|${range.to}`, () => api.transactions(range, false))
  const [syncing, setSyncing] = useState(false)
  const [syncCount, setSyncCount] = useState(0)
  const [notice, setNotice] = useState<Notice | null>(null)

  const { reload } = list
  useEffect(() => {
    if (syncCount === 0) return
    const timer = setTimeout(reload, REFRESH_AFTER_SYNC_MS)
    return () => clearTimeout(timer)
  }, [syncCount, reload])

  const go = (p: { range?: DateRange; category?: string }) => {
    const nextRange = rangeParams(p.range ?? range)
    const nextCategory = p.category ?? category
    navigate('transactions', nextCategory ? { ...nextRange, category: nextCategory } : nextRange)
  }

  async function sync() {
    setSyncing(true)
    setNotice(null)
    try {
      const before = new Set(list.data?.map((t) => t.id))
      // The sync call returns only its own (capped) range, so re-read the full range from storage afterwards.
      const syncRange = capToToday(range)
      if (syncRange.from <= syncRange.to) await api.transactions(syncRange, true)
      const rows = await api.transactions(range, false)
      list.setData(() => rows)
      const added = rows.filter((t) => !before.has(t.id)).length
      setNotice({
        kind: 'ok',
        text: added > 0 ? `${added} new transaction(s). Categorizing in the background…` : 'Up to date.',
      })
      if (added > 0) setSyncCount((n) => n + 1)
    } catch (e) {
      const needsConnect = e instanceof ApiError && e.status === 409
      setNotice({ kind: 'error', text: (e as Error).message, needsConnect })
    } finally {
      setSyncing(false)
    }
  }

  async function changeCategory(tx: Transaction, value: string) {
    const replace = (row: Transaction) => list.setData((rows) => rows.map((t) => (t.id === row.id ? row : t)))
    replace({ ...tx, category: value, categoryConfidence: 'high', categorySource: 'manual' })
    try {
      replace(await api.setCategory(tx.id, value))
    } catch (e) {
      replace(tx)
      setNotice({ kind: 'error', text: `Could not change the category: ${(e as Error).message}` })
    }
  }

  const rows = (list.data ?? []).filter(
    (t) => !category || (t.creditDebit === 'DBIT' && (t.category ?? UNCATEGORIZED) === category),
  )

  return (
    <section>
      <div className="toolbar">
        <DateRangePicker range={range} onChange={(r) => go({ range: r })} />
        <select value={category} onChange={(e) => go({ category: e.target.value })} aria-label="Category filter">
          <option value="">All transactions</option>
          {categories.data?.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
          <option value={UNCATEGORIZED}>{UNCATEGORIZED}</option>
        </select>
        <button type="button" onClick={list.reload} disabled={list.loading}>
          Refresh
        </button>
        <button type="button" className="primary" onClick={sync} disabled={syncing}>
          {syncing ? 'Syncing…' : 'Sync with bank'}
        </button>
      </div>

      {notice && (
        <p className={notice.kind}>
          {notice.text}
          {notice.needsConnect && (
            <>
              {' '}
              <button type="button" className="link" onClick={() => navigate('connect')}>
                Connect the bank
              </button>
            </>
          )}
        </p>
      )}
      {list.error && <p className="error">{list.error}</p>}
      {list.loading && !list.data && <p className="muted">Loading…</p>}
      {list.data && rows.length === 0 && <p className="muted">No transactions.</p>}

      {rows.length > 0 && (
        <div className="table-wrap">
          <table>
            <thead>
              <tr>
                <th>Date</th>
                <th>Description</th>
                <th className="num">Amount</th>
                <th>Category</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((t) => (
                <tr key={t.id}>
                  <td className="nowrap">{date(t.bookingDate)}</td>
                  <td>
                    <div>{t.counterparty ?? '—'}</div>
                    {t.description && <div className="muted small">{t.description}</div>}
                  </td>
                  <td className={`num nowrap ${t.creditDebit === 'CRDT' ? 'income' : ''}`}>
                    {money(t.creditDebit === 'DBIT' ? -t.amount : t.amount, t.currency)}
                  </td>
                  <td className="nowrap">
                    {t.creditDebit === 'DBIT' ? (
                      <>
                        <select
                          value={t.category ?? ''}
                          onChange={(e) => changeCategory(t, e.target.value)}
                          aria-label="Category"
                        >
                          {t.category === null && <option value="">{UNCATEGORIZED}</option>}
                          {categories.data?.map((c) => (
                            <option key={c} value={c}>
                              {c}
                            </option>
                          ))}
                        </select>
                        {t.categorySource === 'manual' && (
                          <span className="badge" title="Set by you">
                            ✎
                          </span>
                        )}
                        {t.categorySource === 'ai' && t.categoryConfidence === 'low' && (
                          <span className="badge warn" title="AI was unsure, please check">
                            ?
                          </span>
                        )}
                      </>
                    ) : (
                      <span className="muted">Income</span>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
