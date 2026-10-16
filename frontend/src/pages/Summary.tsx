import { api } from '../api'
import { currentMonth, isMonth, money, monthLabel } from '../format'
import { navigate } from '../nav'
import { useAsync } from '../useAsync'
import { MonthPicker } from './MonthPicker'

export function SummaryPage({ params }: { params: URLSearchParams }) {
  const requested = params.get('month')
  const month = isMonth(requested) ? requested : currentMonth()
  const { data, error, loading } = useAsync(month, () => api.summary(month))

  return (
    <section>
      <MonthPicker month={month} onChange={(m) => navigate('summary', { month: m })} />

      {error && <p className="error">{error}</p>}
      {loading && !data && <p className="muted">Loading…</p>}

      {data && (
        <>
          <p className="total">
            {money(data.total, data.currency)}
            <span className="muted"> spent in {monthLabel(month)} · {data.count} expenses</span>
          </p>
          {data.skippedNonPln > 0 && (
            <p className="muted">{data.skippedNonPln} non-PLN expenses are not included in the totals.</p>
          )}
          {data.categories.length === 0 && <p className="muted">No expenses this month.</p>}

          <ul className="bars">
            {data.categories.map((c) => (
              <li key={c.category}>
                <button
                  type="button"
                  className="bar-row"
                  title="Show transactions"
                  onClick={() => navigate('transactions', { month, category: c.category })}
                >
                  <span className="bar-label">{c.category}</span>
                  <span className="bar-track">
                    <span className="bar-fill" style={{ width: `${c.percent}%` }} />
                  </span>
                  <span className="bar-value">{money(c.total, data.currency)}</span>
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
