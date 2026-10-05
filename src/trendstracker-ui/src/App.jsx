import { useEffect, useState } from 'react'

function CompanyCard({ c }) {
  return (
    <div className="card">
      <div className="card-top">
        <div>
          <span className="card-title">{c.name}</span>
          <span className="card-stock">{c.stock}</span>
        </div>
        <span className={`badge ${c.verdict ? 'yes' : 'no'}`}>
          {c.verdict ? 'CAN 2x+ in 3-5y' : 'not evident'}
        </span>
      </div>

      <div className="confidence">Confidence {c.confidence}/100 · signal: {c.signalType || 'n/a'}</div>
      <div className="conf-bar"><div className="conf-fill" style={{ width: `${c.confidence}%` }} /></div>

      {c.verdict && <div className="verdict">{c.growthComment || c.marginComment || c.expansionComment}</div>}

      {c.growthComment && c.growthComment !== 'none found' &&
        <div className="signal"><b>Growth:</b> {c.growthComment}</div>}
      {c.marginComment && c.marginComment !== 'none found' &&
        <div className="signal"><b>Margins:</b> {c.marginComment}</div>}
      {c.expansionComment && c.expansionComment !== 'none found' &&
        <div className="signal"><b>Expansion:</b> {c.expansionComment}</div>}

      {c.evidenceQuotes?.length > 0 && (
        <ul className="quotes">
          {c.evidenceQuotes.map((q, i) => <li key={i}>{q}</li>)}
        </ul>
      )}

      {c.sources?.length > 0 && (
        <div className="sources">
          {c.sources.map((s, i) => <a key={i} href={s} target="_blank" rel="noreferrer">{s}</a>)}
        </div>
      )}
      <div className="muted" style={{ fontSize: 11, marginTop: 8 }}>
        Analyzed {new Date(c.analyzedAt).toLocaleString()}
      </div>
    </div>
  )
}

export default function App() {
  const [tab, setTab] = useState('companies')
  const [companies, setCompanies] = useState([])
  const [news, setNews] = useState([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  async function loadCompanies() {
    setLoading(true); setError('')
    try {
      const r = await fetch('/api/companies')
      if (!r.ok) throw new Error(`API returned ${r.status}`)
      setCompanies(await r.json())
    } catch (e) {
      setError(`Could not load companies: ${e.message}. Is the API running on :5080?`)
    } finally { setLoading(false) }
  }

  async function loadNews() {
    setLoading(true); setError('')
    try {
      const r = await fetch('/api/news?max=60')
      if (!r.ok) throw new Error(`API returned ${r.status}`)
      setNews(await r.json())
    } catch (e) {
      setError(`Could not load news: ${e.message}`)
    } finally { setLoading(false) }
  }

  async function searchStock() {
    const q = query.trim()
    if (!q) { loadCompanies(); return }
    setLoading(true); setError('')
    try {
      const r = await fetch(`/api/companies/${encodeURIComponent(q)}`)
      if (r.status === 404) { setCompanies([]); setError(`No saved analysis for "${q}". Run the Company app on it first.`); return }
      if (!r.ok) throw new Error(`API returned ${r.status}`)
      setCompanies([await r.json()])
    } catch (e) {
      setError(`Search failed: ${e.message}`)
    } finally { setLoading(false) }
  }

  useEffect(() => { if (tab === 'companies') loadCompanies(); else loadNews() }, [tab])

  return (
    <div className="app">
      <header>
        <h1>TrendsTracker</h1>
        <p>Companies whose management guidance suggests they could roughly double in 3-5 years.</p>
      </header>

      <div className="tabs">
        <button className={`tab ${tab === 'companies' ? 'active' : ''}`} onClick={() => setTab('companies')}>Companies</button>
        <button className={`tab ${tab === 'news' ? 'active' : ''}`} onClick={() => setTab('news')}>News feed</button>
      </div>

      {tab === 'companies' && (
        <>
          <div className="searchbar">
            <input
              placeholder="Search a stock symbol (e.g. HSCL, AIMTRON)…"
              value={query}
              onChange={e => setQuery(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && searchStock()}
            />
            <button onClick={searchStock}>Search</button>
            <button onClick={() => { setQuery(''); loadCompanies() }} style={{ background: 'var(--panel-2)' }}>All</button>
          </div>

          {loading && <div className="center muted">Loading…</div>}
          {error && <div className="error">{error}</div>}
          {!loading && !error && companies.length === 0 && (
            <div className="center muted">No analyzed companies yet. Run the Company app on a stock to populate this.</div>
          )}
          {companies.map(c => <CompanyCard key={c.stock} c={c} />)}
        </>
      )}

      {tab === 'news' && (
        <>
          {loading && <div className="center muted">Loading…</div>}
          {error && <div className="error">{error}</div>}
          {!loading && news.length === 0 && <div className="center muted">No news stored yet.</div>}
          {news.map((n, i) => (
            <div className="news-item" key={i}>
              <a href={n.url} target="_blank" rel="noreferrer">{n.title}</a>
              <div className="news-meta">{n.source}{n.publishedAt ? ` · ${new Date(n.publishedAt).toLocaleDateString()}` : ''}</div>
            </div>
          ))}
        </>
      )}

      <div className="disclaimer">
        Research assistant output generated from public filings and news. NOT investment advice.
        Media attention and management guidance can be wrong. Do your own research.
      </div>
    </div>
  )
}
