import { useEffect, useState } from 'react'
import { api, ApiError } from '../api'
import { date, money } from '../format'
import { navigate } from '../nav'
import { capToToday, rangeFromParams, rangeParams, type DateRange } from '../range'
import type { Classification, Transaction } from '../types'
import { useAsync } from '../useAsync'
import { DateRangePicker } from './DateRangePicker'

// Same label the summary uses for expenses (and refunds) without a category.
const UNCATEGORIZED = 'Uncategorized'
// AI categorization runs in the background after a sync; refresh once it has had time to finish.
const REFRESH_AFTER_SYNC_MS = 30_000

// <select> values for incoming money: "income", or "refund:<category>" ("refund:" = category unknown).
const INCOME = 'income'
const REFUND = 'refund:'

interface Notice {
  kind: 'ok' | 'error'
  text: string
  needsConnect?: boolean
}

/** Expenses and refunds count towards category totals; income doesn't. */
const counts = (t: Transaction) => t.creditDebit === 'DBIT' || t.isRefund

function creditValue(t: Transaction): string {
  if (t.categorySource === null) return ''
  return t.isRefund ? REFUND + (t.category ?? '') : INCOME
}

function parseCreditValue(value: string): Classification {
  return value === INCOME ? { category: null, refund: false } : { category: value.slice(REFUND.length) || null, refund: true }
}

export function TransactionsPage({ params }: { params: URLSearchParams }) {
  const range = rangeFromParams(params)
  const category = params.get('category') ?? ''

  const categories = useAsync('categories', api.categories)
  const list = useAsync(`${range.from}|${range.to}`, () => api.transactions(range, false))
  const [busy, setBusy] = useState(false)
  const [refreshCount, setRefreshCount] = useState(0)
  const [notice, setNotice] = useState<Notice | null>(null)

  const { reload } = list
  useEffect(() => {
    if (refreshCount === 0) return
    const timer = setTimeout(reload, REFRESH_AFTER_SYNC_MS)
    return () => clearTimeout(timer)
  }, [refreshCount, reload])

  const go = (p: { range?: DateRange; category?: string }) => {
    const nextRange = rangeParams(p.range ?? range)
    const nextCategory = p.category ?? category
    navigate('transactions', nextCategory ? { ...nextRange, category: nextCategory } : nextRange)
  }

  async function sync() {
    setBusy(true)
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
      if (added > 0) setRefreshCount((n) => n + 1)
    } catch (e) {
      const needsConnect = e instanceof ApiError && e.status === 409
      setNotice({ kind: 'error', text: (e as Error).message, needsConnect })
    } finally {
      setBusy(false)
    }
  }

  async function recategorize() {
    const ok = window.confirm(
      'Send expenses that the AI put in Shopping or Other back to Claude, so they can move to the new categories ' +
        '(Sport, Education, Travel, Clothes)? Categories you set yourself are kept.',
    )
    if (!ok) return
    setBusy(true)
    setNotice(null)
    try {
      const { reset } = await api.recategorize()
      setNotice({
        kind: 'ok',
        text: reset > 0 ? `${reset} transaction(s) sent for re-categorization…` : 'Nothing to re-check.',
      })
      if (reset > 0) {
        list.reload()
        setRefreshCount((n) => n + 1)
      }
    } catch (e) {
      setNotice({ kind: 'error', text: (e as Error).message })
    } finally {
      setBusy(false)
    }
  }

  async function classify(tx: Transaction, c: Classification) {
    const replace = (row: Transaction) => list.setData((rows) => rows.map((t) => (t.id === row.id ? row : t)))
    replace({
      ...tx,
      category: c.category,
      isRefund: c.refund === true,
      categoryConfidence: 'high',
      categorySource: 'manual',
    })
    try {
      replace(await api.setCategory(tx.id, c))
    } catch (e) {
      replace(tx)
      setNotice({ kind: 'error', text: `Could not change the category: ${(e as Error).message}` })
    }
  }

  const rows = (list.data ?? []).filter((t) => !category || (counts(t) && (t.category ?? UNCATEGORIZED) === category))
  const categoryOptions = categories.data ?? []

  return (
    <section>
      <div className="toolbar">
        <DateRangePicker range={range} onChange={(r) => go({ range: r })} />
        <select value={category} onChange={(e) => go({ category: e.target.value })} aria-label="Category filter">
          <option value="">All transactions</option>
          {categoryOptions.map((c) => (
            <option key={c} value={c}>
              {c}
            </option>
          ))}
          <option value={UNCATEGORIZED}>{UNCATEGORIZED}</option>
        </select>
        <button type="button" onClick={list.reload} disabled={list.loading}>
          Refresh
        </button>
        <button type="button" onClick={recategorize} disabled={busy} title="Only AI-set categories are re-checked">
          Re-check Shopping &amp; Other
        </button>
        <button type="button" className="primary" onClick={sync} disabled={busy}>
          {busy ? 'Working…' : 'Sync with bank'}
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
              {rows.map((t) => {
                const credit = t.creditDebit === 'CRDT'
                const unsure =
                  t.categorySource !== 'manual' &&
                  t.categorySource !== null &&
                  (t.categoryConfidence === 'low' || (t.isRefund && t.category === null))
                return (
                  <tr key={t.id}>
                    <td className="nowrap">{date(t.bookingDate)}</td>
                    <td>
                      <div>
                        {t.counterparty ?? '—'}
                        {t.isRefund && <span className="tag">refund</span>}
                      </div>
                      {t.description && <div className="muted small">{t.description}</div>}
                    </td>
                    <td className={`num nowrap ${credit ? 'income' : ''}`}>
                      {money(credit ? t.amount : -t.amount, t.currency)}
                    </td>
                    <td className="nowrap">
                      {credit ? (
                        <select
                          value={creditValue(t)}
                          onChange={(e) => classify(t, parseCreditValue(e.target.value))}
                          aria-label="Income or refund"
                        >
                          {t.categorySource === null && <option value="">Not checked yet</option>}
                          <option value={INCOME}>Income (not a refund)</option>
                          {t.isRefund && t.category === null && <option value={REFUND}>Refund · choose category</option>}
                          {categoryOptions.map((c) => (
                            <option key={c} value={REFUND + c}>
                              Refund · {c}
                            </option>
                          ))}
                        </select>
                      ) : (
                        <select
                          value={t.category ?? ''}
                          onChange={(e) => classify(t, { category: e.target.value })}
                          aria-label="Category"
                        >
                          {t.category === null && <option value="">{UNCATEGORIZED}</option>}
                          {categoryOptions.map((c) => (
                            <option key={c} value={c}>
                              {c}
                            </option>
                          ))}
                        </select>
                      )}
                      {t.categorySource === 'manual' && (
                        <span className="badge" title="Set by you">
                          ✎
                        </span>
                      )}
                      {t.categorySource === 'rule' && !unsure && (
                        <span className="badge" title="Same merchant as one you corrected earlier">
                          ↺
                        </span>
                      )}
                      {unsure && (
                        <span
                          className="badge warn"
                          title={
                            t.isRefund
                              ? 'Refund category guessed from a purchase with the same amount, or unknown. Please check.'
                              : 'AI was unsure, please check'
                          }
                        >
                          ?
                        </span>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}
    </section>
  )
}
