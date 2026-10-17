import { useState } from 'react'
import { CUSTOM, isDate, isValidRange, PRESETS, presetOf, rangeLabel, type DateRange } from '../range'

/** Preset selector plus two date inputs for a custom range. Shared by Summary and Transactions. */
export function DateRangePicker({ range, onChange }: { range: DateRange; onChange: (range: DateRange) => void }) {
  // "Custom" stays selected after picking dates, even if they happen to match a preset.
  const [customChosen, setCustomChosen] = useState(false)
  // Half-edited or invalid input; the valid range itself always lives in the URL (the `range` prop).
  const [draft, setDraft] = useState<DateRange | null>(null)

  const selected = customChosen ? CUSTOM : presetOf(range)
  const shown = draft ?? range

  function selectPreset(id: string) {
    setDraft(null)
    if (id === CUSTOM) {
      setCustomChosen(true)
      return
    }
    setCustomChosen(false)
    const preset = PRESETS.find((p) => p.id === id)
    if (preset) onChange(preset.range())
  }

  function edit(next: DateRange) {
    if (isValidRange(next)) {
      setDraft(null)
      onChange(next)
    } else {
      setDraft(next)
    }
  }

  const problem = !draft
    ? null
    : !isDate(draft.from) || !isDate(draft.to)
      ? 'Enter both dates.'
      : 'The start date must be on or before the end date.'

  return (
    <div className="range-picker">
      <select value={selected} onChange={(e) => selectPreset(e.target.value)} aria-label="Date range">
        {PRESETS.map((p) => (
          <option key={p.id} value={p.id}>
            {p.label}
          </option>
        ))}
        <option value={CUSTOM}>Custom</option>
      </select>

      {selected === CUSTOM ? (
        <span className="range-inputs">
          <input
            type="date"
            value={shown.from}
            onChange={(e) => edit({ ...shown, from: e.target.value })}
            aria-label="From"
          />
          –
          <input
            type="date"
            value={shown.to}
            onChange={(e) => edit({ ...shown, to: e.target.value })}
            aria-label="To"
          />
          {problem && <span className="error small">{problem}</span>}
        </span>
      ) : (
        <span className="muted">{rangeLabel(range)}</span>
      )}
    </div>
  )
}
