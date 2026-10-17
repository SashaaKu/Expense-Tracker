// Shapes returned by the backend (ASP.NET Core serializes camelCase).

export type CreditDebit = 'DBIT' | 'CRDT'

export interface Transaction {
  id: number
  accountUid: string
  bookingDate: string // yyyy-MM-dd
  valueDate: string | null
  amount: number // always positive; direction is in creditDebit
  currency: string
  creditDebit: CreditDebit
  counterparty: string | null
  description: string | null
  status: string | null
  category: string | null
  categoryConfidence: 'high' | 'low' | null
  categorySource: 'ai' | 'manual' | null
}

export interface CategorySummary {
  category: string
  total: number
  percent: number
  count: number
}

export interface Summary {
  month: string | null // set only when requested with ?month=
  from: string
  to: string
  currency: string
  total: number
  count: number
  skippedNonPln: number
  categories: CategorySummary[]
}
