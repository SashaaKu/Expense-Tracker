import { api } from '../api'
import { money } from '../format'
import { navigate } from '../nav'
import { rangeFromParams, rangeLabel, rangeParams } from '../range'
import { useAsync } from '../useAsync'
import { DateRangePicker } from './DateRangePicker'

export function SummaryPage({ params }: { params: URLSearchParams }) {
  const range = rangeFromParams(params)
  const { data, error, loading } = useAsync(`${range.from}|${range.to}`, () => api.summary(range))

  return (
    <section>
      <div className="toolbar">
        <DateRangePicker range={range} onChange={(r) => navigate('summary', rangeParams(r))} />
      </div>

      {error && <p className="error">{error}</p>}
      {loading && !data && <p className="muted">Loading…</p>}

      {data && (
        <>
          <p className="total">
            {money(data.total, data.currency)}
            <span className="muted">
              {' '}
              spent {rangeLabel(range)} · {data.count} expenses
            </span>
          </p>
          {data.refunds > 0 && (
            <p className="muted">
              {money(data.expenses, data.currency)} expenses − {money(data.refunds, data.currency)} refunds (
              {data.refundCount})
            </p>
          )}
          {data.unclassifiedCredits > 0 && (
            <p className="muted">
              {data.unclassifiedCredits} incoming transaction(s) are still being checked for refunds; totals may drop.
            </p>
          )}
          {data.skippedNonPln > 0 && (
            <p className="muted">{data.skippedNonPln} non-PLN transactions are not included in the totals.</p>
          )}
          {data.categories.length === 0 && <p className="muted">No expenses in this period.</p>}

          <ul className="bars">
            {data.categories.map((c) => (
              <li key={c.category}>
                <button
                  type="button"
                  className="bar-row"
                  title="Show transactions"
                  onClick={() => navigate('transactions', { ...rangeParams(range), category: c.category })}
                >
                  <span className="bar-label">
                    {c.category}
                    {c.refunds > 0 && (
                      <span className="bar-detail muted">
                        {money(c.expenses, data.currency)} − {money(c.refunds, data.currency)} refunds ({c.refundCount})
                      </span>
                    )}
                  </span>
                  <span className="bar-track">
                    <span className="bar-fill" style={{ width: `${Math.max(0, c.percent)}%` }} />
                  </span>
                  <span className={`bar-value ${c.total < 0 ? 'income' : ''}`}>{money(c.total, data.currency)}</span>
                  <span className="bar-meta muted">
                    {c.percent}% · {c.count}
                  </span>
                </button>
              </li>
            ))}
          </ul>
        </>
      )}
    </section>
  )
}
