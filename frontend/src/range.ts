import { date, toIso, today } from './format'

/** Inclusive date range, both ends yyyy-MM-dd. Kept in the URL hash as ?from=&to=. */
export interface DateRange {
  from: string
  to: string
}

export interface Preset {
  id: string
  label: string
  range: () => DateRange
}

const span = (from: Date, to: Date): DateRange => ({ from: toIso(from), to: toIso(to) })

export const PRESETS: Preset[] = [
  {
    id: 'this-month',
    label: 'This month',
    range: () => {
      const n = new Date()
      return span(new Date(n.getFullYear(), n.getMonth(), 1), new Date(n.getFullYear(), n.getMonth() + 1, 0))
    },
  },
  {
    id: 'last-month',
    label: 'Last month',
    range: () => {
      const n = new Date()
      return span(new Date(n.getFullYear(), n.getMonth() - 1, 1), new Date(n.getFullYear(), n.getMonth(), 0))
    },
  },
  {
    id: 'last-30-days',
    label: 'Last 30 days',
    range: () => {
      const n = new Date()
      return span(new Date(n.getFullYear(), n.getMonth(), n.getDate() - 29), n)
    },
  },
  {
    id: 'this-year',
    label: 'This year',
    range: () => {
      const y = new Date().getFullYear()
      return span(new Date(y, 0, 1), new Date(y, 11, 31))
    },
  },
]

export const CUSTOM = 'custom'

/** True for a real calendar date in yyyy-MM-dd form (rejects e.g. 2026-02-31). */
export function isDate(value: string | null): value is string {
  return value !== null && /^\d{4}-\d{2}-\d{2}$/.test(value) && toIso(new Date(value + 'T00:00:00')) === value
}

export function isValidRange(r: DateRange): boolean {
  return isDate(r.from) && isDate(r.to) && r.from <= r.to
}

/** The range from the URL; falls back to this month when it is missing or invalid. */
export function rangeFromParams(params: URLSearchParams): DateRange {
  const r = { from: params.get('from') ?? '', to: params.get('to') ?? '' }
  return isValidRange(r) ? r : PRESETS[0].range()
}

export function rangeParams(r: DateRange): Record<string, string> {
  return { from: r.from, to: r.to }
}

/** Id of the preset that produces exactly this range, or CUSTOM. */
export function presetOf(r: DateRange): string {
  return PRESETS.find((p) => {
    const pr = p.range()
    return pr.from === r.from && pr.to === r.to
  })?.id ?? CUSTOM
}

export function rangeLabel(r: DateRange): string {
  return r.from === r.to ? date(r.from) : `${date(r.from)} – ${date(r.to)}`
}

/** Same range with the end capped at today; the bank has no future transactions to sync. */
export function capToToday(r: DateRange): DateRange {
  const now = today()
  return { from: r.from, to: r.to > now ? now : r.to }
}
