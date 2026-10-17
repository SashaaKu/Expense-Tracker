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
  // Expense: its category. Refund (CRDT, isRefund): the purchase's category, null if unknown. Income: null.
  category: string | null
  categoryConfidence: 'high' | 'low' | null
  // null = not classified yet; rule = copied from your correction of the same merchant or a matched bank refund
  categorySource: 'ai' | 'manual' | 'rule' | null
  isRefund: boolean
}

/** PATCH body: expenses send a category; incoming money sends refund (true/false) and, for refunds, a category. */
export interface Classification {
  category: string | null
  refund?: boolean
}

export interface CategorySummary {
  category: string
  total: number // expenses − refunds; can be negative
  expenses: number
  refunds: number
  percent: number
  count: number
  refundCount: number
}

export interface Summary {
  month: string | null // set only when requested with ?month=
  from: string
  to: string
  currency: string
  total: number // expenses − refunds
  expenses: number
  refunds: number
  count: number
  refundCount: number
  skippedNonPln: number
  unclassifiedCredits: number // incoming transactions not yet checked for refunds
  categories: CategorySummary[]
}
