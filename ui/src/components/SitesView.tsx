import { useCallback, useEffect, useState } from "react";
import type { SiteSummary } from "../types";
import { checkSitesNow, fetchSites } from "../api";
import { ago } from "../format";

const hostOf = (url: string) => { try { return new URL(url).host; } catch { return url; } };

/** Response times across the window as a line; down checks are marks on the baseline. */
function Spark({ points }: { points: SiteSummary["points"] }) {
  const W = 220, H = 36;
  if (points.length < 2) return <svg className="spark" viewBox={`0 0 ${W} ${H}`} aria-hidden="true" />;
  const max = Math.max(200, ...points.map((p) => p.ms ?? 0));
  const x = (i: number) => (i / (points.length - 1)) * W;
  const y = (ms: number) => H - 3 - (ms / max) * (H - 8);
  const line = points.map((p, i) => (p.ms !== undefined && p.ok ? `${x(i).toFixed(1)},${y(p.ms).toFixed(1)}` : null)).filter(Boolean).join(" ");
  return (
    <svg className="spark" viewBox={`0 0 ${W} ${H}`} preserveAspectRatio="none" role="img" aria-label={`response time, up to ${max} ms`}>
      <polyline points={line} />
      {points.map((p, i) => !p.ok && <rect key={i} className="down" x={x(i) - 1.5} y={H - 6} width={3} height={6} />)}
    </svg>
  );
}

function certText(checked: boolean, days?: number) {
  if (!checked) return { text: "cert –", tone: "dim2" };
  if (days === undefined) return { text: "no TLS", tone: "dim2" };
  if (days < 0) return { text: "certificate expired", tone: "danger-text" };
  return { text: `cert ${days}d`, tone: days < 7 ? "danger-text" : days < 14 ? "warn-text" : "dim" };
}

function SiteCard({ s, now }: { s: SiteSummary; now: number }) {
  const state = !s.last ? "unknown" : s.last.ok ? "up" : "down";
  const cert = certText(!!s.last, s.certDays);
  return (
    <a className={`site ${state}`} href={s.url} target="_blank" rel="noopener noreferrer" title={`open ${s.url}`}>
      <span className="site-top">
        <span className="site-name"><i className={`dot ${state === "up" ? "online" : state === "down" ? "rejected" : ""}`} />{s.name}</span>
        <span className="dim2">{hostOf(s.url)}</span>
      </span>
      <span className="site-state">
        {state === "unknown" ? <span className="dim">not checked yet</span>
          : state === "up" ? <b>{s.last?.ms} ms</b>
          : <b className="danger-text">down · {s.last?.error ?? "no answer"}</b>}
        {s.last?.status !== undefined && state === "up" && <span className="dim2">HTTP {s.last.status}</span>}
      </span>
      <Spark points={s.points} />
      <span className="row">
        <span>24h <b>{s.uptime === undefined ? "–" : `${s.uptime}%`}</b></span>
        <span>avg <b>{s.avgMs ?? "–"}{s.avgMs !== undefined && " ms"}</b></span>
        <span className={cert.tone}>{cert.text}</span>
      </span>
      <span className="row dim2">
        <span className="site-note">{s.last?.version ? `version ${s.last.version}` : s.since ? `${state} for ${ago(s.since, now)}` : `every ${s.everyMinutes} min`}</span>
        {s.last && <span title={new Date(s.last.at).toLocaleString()}>checked {ago(s.last.at, now)} ago</span>}
      </span>
    </a>
  );
}

/** Public websites (hub.sites), checked from the hub: up, response time, certificate, version. */
export function SitesView({ admin, now }: { admin: boolean; now: number }) {
  const [sites, setSites] = useState<SiteSummary[]>();
  const [err, setErr] = useState<string>();
  const [checking, setChecking] = useState(false);
  const load = useCallback(() => fetchSites().then((s) => { setSites(s); setErr(undefined); }, (e: Error) => setErr(e.message)), []);
  useEffect(() => {
    void load();
    const t = setInterval(() => void load(), 60_000);
    return () => clearInterval(t);
  }, [load]);
  const checkNow = async () => {
    setChecking(true);
    try { await checkSitesNow(); await load(); } catch (e) { setErr((e as Error).message); } finally { setChecking(false); }
  };

  if (err) return <p className="empty">{err}</p>;
  if (!sites) return <p className="empty">loading…</p>;
  if (sites.length === 0) {
    return (
      <p className="empty">
        No sites yet. List public websites in the hub config, e.g.{" "}
        <code>"sites": [{"{"} "name": "shop", "url": "https://shop.example.com", "provider": "digitalocean", "health": "/healthz" {"}"}]</code>{" "}
        under <code>hub</code>. The hub checks each one every few minutes from outside: no credentials, any host.
      </p>
    );
  }
  const groups = [...new Set(sites.map((s) => s.provider ?? ""))];
  const down = sites.filter((s) => s.last && !s.last.ok).length;
  return (
    <div className="sites">
      <div className="sites-bar">
        <span className="dim2">{sites.length} sites · {down === 0 ? "all up" : <span className="danger-text">{down} down</span>} · checked from the hub</span>
        {admin && <button className="pill" onClick={() => void checkNow()} disabled={checking}>{checking ? "checking…" : "check now"}</button>}
      </div>
      {groups.map((g) => (
        <section key={g} className="site-group">
          {groups.length > 1 && <div className="label">{g || "other"}</div>}
          <div className="grid site-grid">{sites.filter((s) => (s.provider ?? "") === g).map((s) => <SiteCard key={s.name} s={s} now={now} />)}</div>
        </section>
      ))}
    </div>
  );
}
