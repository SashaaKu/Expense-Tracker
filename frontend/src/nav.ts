import { useEffect, useState } from 'react'

// Navigation lives in the URL hash ("#transactions?from=2026-10-01&to=2026-10-31&category=Groceries"),
// so a refresh keeps the page and the backend callback can redirect to "#connect?connected=1".

export const TABS = ['summary', 'transactions', 'connect'] as const
export type Tab = (typeof TABS)[number]

export interface Route {
  tab: Tab
  params: URLSearchParams
}

function parse(): Route {
  const [name, query = ''] = window.location.hash.slice(1).split('?')
  const tab = (TABS as readonly string[]).includes(name) ? (name as Tab) : 'summary'
  return { tab, params: new URLSearchParams(query) }
}

export function navigate(tab: Tab, params: Record<string, string> = {}) {
  const query = new URLSearchParams(params).toString()
  window.location.hash = query ? `${tab}?${query}` : tab
}

export function useRoute(): Route {
  const [route, setRoute] = useState(parse)
  useEffect(() => {
    const onChange = () => setRoute(parse())
    window.addEventListener('hashchange', onChange)
    return () => window.removeEventListener('hashchange', onChange)
  }, [])
  return route
}
