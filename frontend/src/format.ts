// Formatting and month helpers. Months are "yyyy-MM" strings, as the API expects.

export function money(amount: number, currency = 'PLN'): string {
  return new Intl.NumberFormat('pl-PL', { style: 'currency', currency }).format(amount)
}

export function date(iso: string): string {
  return new Date(iso + 'T00:00:00').toLocaleDateString('pl-PL', { day: '2-digit', month: '2-digit', year: 'numeric' })
}

const pad = (n: number) => String(n).padStart(2, '0')
const toIso = (d: Date) => `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`

export function today(): string {
  return toIso(new Date())
}

export function currentMonth(): string {
  return today().slice(0, 7)
}

export function isMonth(value: string | null): value is string {
  return value !== null && /^\d{4}-(0[1-9]|1[0-2])$/.test(value)
}

export function shiftMonth(month: string, delta: number): string {
  const [y, m] = month.split('-').map(Number)
  return toIso(new Date(y, m - 1 + delta, 1)).slice(0, 7)
}

/** First and last day of the month; the last day is capped at today (the bank has no future data). */
export function monthRange(month: string): { from: string; to: string } {
  const [y, m] = month.split('-').map(Number)
  const last = toIso(new Date(y, m, 0))
  const now = today()
  return { from: `${month}-01`, to: last > now ? now : last }
}

export function monthLabel(month: string): string {
  const [y, m] = month.split('-').map(Number)
  return new Date(y, m - 1, 1).toLocaleDateString('en-GB', { month: 'long', year: 'numeric' })
}
