import { navigate, TABS, useRoute, type Tab } from './nav'
import { ConnectPage } from './pages/Connect'
import { SummaryPage } from './pages/Summary'
import { TransactionsPage } from './pages/Transactions'
import { rangeFromParams, rangeParams } from './range'

const TITLES: Record<Tab, string> = {
  summary: 'Summary',
  transactions: 'Transactions',
  connect: 'Bank',
}

export default function App() {
  const { tab, params } = useRoute()

  return (
    <div className="app">
      <header>
        <h1>Expenses</h1>
        <nav>
          {TABS.map((t) => (
            <button
              key={t}
              type="button"
              className={t === tab ? 'tab active' : 'tab'}
              aria-current={t === tab ? 'page' : undefined}
              onClick={() => navigate(t, rangeParams(rangeFromParams(params)))}
            >
              {TITLES[t]}
            </button>
          ))}
        </nav>
      </header>
      <main>
        {tab === 'summary' && <SummaryPage params={params} />}
        {tab === 'transactions' && <TransactionsPage params={params} />}
        {tab === 'connect' && <ConnectPage params={params} />}
      </main>
    </div>
  )
}
