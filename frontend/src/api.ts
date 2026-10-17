import { rangeParams, type DateRange } from './range'
import type { Summary, Transaction } from './types'

export class ApiError extends Error {
  status: number

  constructor(message: string, status: number) {
    super(message)
    this.status = status
  }
}

// Backend errors come as { error } or ProblemDetails { title, detail }.
interface ErrorBody {
  error?: string
  title?: string
  detail?: string
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const res = await fetch(path, init)
  const body: unknown = await res.json().catch(() => null)
  if (!res.ok) {
    const err = (body ?? {}) as ErrorBody
    const message = err.error ?? [err.title, err.detail].filter(Boolean).join(': ')
    throw new ApiError(message || `${res.status} ${res.statusText}`, res.status)
  }
  return body as T
}

export const api = {
  categories: () => request<string[]>('/api/categories'),

  summary: (range: DateRange) => request<Summary>(`/api/summary?${new URLSearchParams(rangeParams(range))}`),

  /** sync=false reads stored rows only; sync=true fetches new ones from the bank first. */
  transactions: (range: DateRange, sync: boolean) =>
    request<Transaction[]>(`/api/transactions?${new URLSearchParams({ ...rangeParams(range), sync: String(sync) })}`),

  setCategory: (id: number, category: string) =>
    request<Transaction>(`/api/transactions/${id}/category`, {
      method: 'PATCH',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ category }),
    }),

  connect: () =>
    request<{ url: string }>(`/api/bank/connect?${new URLSearchParams({ name: 'PKO Bank Polski', country: 'PL' })}`),
}
