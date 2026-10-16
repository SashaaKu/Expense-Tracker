import { currentMonth, monthLabel, shiftMonth } from '../format'

export function MonthPicker({ month, onChange }: { month: string; onChange: (month: string) => void }) {
  return (
    <div className="month-picker">
      <button type="button" onClick={() => onChange(shiftMonth(month, -1))} aria-label="Previous month">
        ‹
      </button>
      <strong>{monthLabel(month)}</strong>
      <button
        type="button"
        onClick={() => onChange(shiftMonth(month, 1))}
        disabled={month >= currentMonth()}
        aria-label="Next month"
      >
        ›
      </button>
    </div>
  )
}
