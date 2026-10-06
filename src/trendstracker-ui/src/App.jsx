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

      <div className="confidence">
        Confidence {c.confidence}/100 · signal: {c.signalType || 'n/a'}
        {c.marketCapCr != null && <> · mcap ₹{Number(c.marketCapCr).toLocaleString('en-IN')} cr</>}
      </div>
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

// Sort by confidence, highest first.
const byConfidence = (a, b) => b.confidence - a.confidence

// Group search hits: if they're all one stock, group by concall date; otherwise
// group by stock (so you see which companies discuss the topic).
function groupHits(hits) {
  if (!hits || hits.length === 0) return {}
  const distinctStocks = new Set(hits.map(h => h.stock))
  const singleStock = distinctStocks.size === 1
  const groups = {}
  for (const h of hits) {
    const key = singleStock ? `${h.stock} — ${h.transcriptDate || 'undated'}` : h.stock
    ;(groups[key] ||= []).push(h)
  }
  return groups
}

export default function App() {
  const [tab, setTab] = useState('strong')
  const [companies, setCompanies] = useState([])
  const [news, setNews] = useState([])
  const [query, setQuery] = useState('')
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState('')

  // Semantic search / ask state (separate from the company list filter).
  const [searchQuery, setSearchQuery] = useState('')
  const [searchStock, setSearchStock] = useState('')
  const [searchHits, setSearchHits] = useState([])
  const [answer, setAnswer] = useState('')
  const [searched, setSearched] = useState(false)

  async function runSearch() {
    const sq = searchQuery.trim()
    if (!sq) return
    setLoading(true); setError(''); setSearched(true); setAnswer('')
    try {
      const params = new URLSearchParams({ q: sq, k: '10' })
      if (searchStock.trim()) params.set('stock', searchStock.trim())
      // /api/ask gives a concise LLM answer + the supporting passages.
      const r = await fetch(`/api/ask?${params}`)
      if (!r.ok) throw new Error(`API returned ${r.status}`)
      const data = await r.json()
      setAnswer(data.answer || '')
      setSearchHits(data.passages || [])
    } catch (e) {
      setError(`Search failed: ${e.message}. Is the API running on :5080?`)
    } finally { setLoading(false) }
  }

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

  useEffect(() => {
    if (tab === 'news') loadNews()
    else loadCompanies()
  }, [tab])

  // Split + sort. Search filters by stock or name within the active list.
  const q = query.trim().toLowerCase()
  const matches = c => !q || c.stock?.toLowerCase().includes(q) || c.name?.toLowerCase().includes(q)
  const strong = companies.filter(c => c.verdict && matches(c)).sort(byConfidence)
  const notEvident = companies.filter(c => !c.verdict && matches(c)).sort(byConfidence)

  const activeList = tab === 'strong' ? strong : tab === 'notEvident' ? notEvident : []

  return (
    <div className="app">
      <header>
        <h1>StockFinder</h1>
        <p>Companies whose management guidance suggests they could roughly double in 3-5 years.</p>
      </header>

      <div className="tabs">
        <button className={`tab ${tab === 'strong' ? 'active' : ''}`} onClick={() => setTab('strong')}>
          Strong growth ({strong.length})
        </button>
        <button className={`tab ${tab === 'notEvident' ? 'active' : ''}`} onClick={() => setTab('notEvident')}>
          Not evident ({notEvident.length})
        </button>
        <button className={`tab ${tab === 'search' ? 'active' : ''}`} onClick={() => setTab('search')}>Ask transcripts</button>
        <button className={`tab ${tab === 'news' ? 'active' : ''}`} onClick={() => setTab('news')}>News feed</button>
      </div>

      {(tab === 'strong' || tab === 'notEvident') && (
        <>
          <div className="searchbar">
            <input
              placeholder="Filter by stock symbol or name…"
              value={query}
              onChange={e => setQuery(e.target.value)}
            />
            {query && <button onClick={() => setQuery('')} style={{ background: 'var(--panel-2)' }}>Clear</button>}
          </div>

          {loading && <div className="center muted">Loading…</div>}
          {error && <div className="error">{error}</div>}
          {!loading && !error && activeList.length === 0 && (
            <div className="center muted">
              {tab === 'strong'
                ? 'No companies passed the growth check yet.'
                : 'Nothing here. Analyze some stocks to populate this.'}
            </div>
          )}
          {activeList.map(c => <CompanyCard key={c.stock} c={c} />)}
        </>
      )}

      {tab === 'search' && (
        <>
          <p className="muted" style={{ marginTop: 0, fontSize: 13 }}>
            Ask a question across stored concall transcripts — e.g. "what is the order book?",
            "margin outlook?", "any debt reduction plan?". Leave the stock box empty to ask across all companies.
          </p>
          <div className="searchbar">
            <input
              placeholder="Ask a question (e.g. what is the order book?)"
              value={searchQuery}
              onChange={e => setSearchQuery(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && runSearch()}
              style={{ flex: 2 }}
            />
            <input
              placeholder="Stock (optional)"
              value={searchStock}
              onChange={e => setSearchStock(e.target.value)}
              onKeyDown={e => e.key === 'Enter' && runSearch()}
              style={{ flex: 1 }}
            />
            <button onClick={runSearch}>Ask</button>
          </div>

          {loading && <div className="center muted">Thinking…</div>}
          {error && <div className="error">{error}</div>}

          {answer && (
            <div className="card" style={{ borderColor: 'var(--accent)' }}>
              <div className="card-title" style={{ marginBottom: 6 }}>Answer</div>
              <div style={{ fontSize: 15, lineHeight: 1.5, whiteSpace: 'pre-wrap' }}>{answer}</div>
            </div>
          )}

          {!loading && searched && searchHits.length === 0 && !error && (
            <div className="center muted">No matching passages. Analyze some stocks first to store their transcripts.</div>
          )}
          {searchHits.length > 0 && (
            <div className="muted" style={{ fontSize: 12, margin: '12px 0 6px' }}>Supporting passages:</div>
          )}
          {Object.entries(groupHits(searchHits)).map(([key, hits]) => (
            <div className="card" key={key}>
              <div className="card-title" style={{ marginBottom: 8 }}>{key}</div>
              {hits.map((h, i) => (
                <div key={i} style={{ marginBottom: 10 }}>
                  <div className="muted" style={{ fontSize: 12 }}>
                    {h.stock} · {h.transcriptDate || 'undated'} · similarity {h.similarity}
                  </div>
                  <div style={{ fontSize: 14, margin: '2px 0' }}>{h.text}</div>
                  {h.source && <a className="sources" href={h.source} target="_blank" rel="noreferrer" style={{ fontSize: 11 }}>source</a>}
                </div>
              ))}
            </div>
          ))}
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
