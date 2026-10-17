// Formatting helpers. Dates are "yyyy-MM-dd" strings, as the API expects.

export function money(amount: number, currency = 'PLN'): string {
  return new Intl.NumberFormat('pl-PL', { style: 'currency', currency }).format(amount)
}

export function date(iso: string): string {
  return new Date(iso + 'T00:00:00').toLocaleDateString('pl-PL', { day: '2-digit', month: '2-digit', year: 'numeric' })
}

const pad = (n: number) => String(n).padStart(2, '0')

/** Local calendar date as yyyy-MM-dd (not toISOString, which would shift to UTC). */
export function toIso(d: Date): string {
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`
}

export function today(): string {
  return toIso(new Date())
}
