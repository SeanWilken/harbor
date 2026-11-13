import type { MachineSummary, SharedRepo } from "../types";
import { ago, plural } from "../format";
import { activity, liveness } from "../status";

const pct = (n?: number) => (n === undefined ? "–" : String(Math.round(n)));
import { SharedView } from "./SharedView";
import { CompareView } from "./CompareView";
import { RecapView } from "./RecapView";

export type NetView = "grid" | "map" | "repos" | "compare" | "recap";
export const NET_VIEWS: NetView[] = ["grid", "map", "repos", "compare", "recap"];

type Props = {
  machines: MachineSummary[];
  shared: SharedRepo[];
  admin: boolean;
  view: NetView;
  pins: number[];
  /** The browser's own machine: it is the "this machine" tab, so it can't be pinned. */
  selfId?: number;
  now: number;
  onView: (v: NetView) => void;
  onOpen: (m: MachineSummary) => void;
  onPin: (id: number) => void;
  /** Admins: pull one shared repo on the machines whose copy can fast-forward. */
  onPullCopies?: (copies: { machineId: number; path: string }[]) => void;
};

/** Every machine at once: small cards, a map of how they connect, or repos cloned on several. */
export function NetworkView({ machines, shared, admin, view, pins, selfId, now, onView, onOpen, onPin, onPullCopies }: Props) {
  const shown = machines.filter((m) => m.status !== "rejected");
  const count = (k: string) => shown.filter((m) => liveness(m, now).key === k).length;
  const differ = shared.filter((g) => !g.sameHead).length;
  return (
    <>
      <div className="panelbar">
        <span className="label">
          {plural(shown.length, "machine")} · {count("online")} online
          {count("stale") > 0 && ` · ${count("stale")} stale`}{count("pending") > 0 && ` · ${count("pending")} pending`}
        </span>
        <div className="seg" role="tablist">
          {NET_VIEWS.map((v) => (
            <button key={v} role="tab" aria-selected={v === view} className={v === view ? "on" : ""} onClick={() => onView(v)}>
              {v}{v === "repos" && differ > 0 && <b> {differ}</b>}
            </button>
          ))}
        </div>
      </div>
      {view === "grid" && (
        <div className="grid">
          {shown.map((m) => <MiniCard key={m.id} m={m} now={now} pinned={pins.includes(m.id)} pinnable={m.id !== selfId} onOpen={() => onOpen(m)} onPin={() => onPin(m.id)} />)}
          {shown.length === 0 && <p className="empty">No machines have pushed yet.</p>}
        </div>
      )}
      {view === "map" && <NetMap machines={shown} now={now} onOpen={onOpen} />}
      {view === "repos" && <SharedView groups={shared} now={now} onPull={onPullCopies} />}
      {view === "compare" && <CompareView machines={machines} admin={admin} />}
      {view === "recap" && <RecapView now={now} />}
    </>
  );
}

function MiniCard({ m, now, pinned, pinnable, onOpen, onPin }: { m: MachineSummary; now: number; pinned: boolean; pinnable: boolean; onOpen: () => void; onPin: () => void }) {
  const l = liveness(m, now);
  return (
    <div className={`mini ${l.key}`}>
      <button className="mini-main" onClick={onOpen}>
        <span className="mini-top"><span>{m.os ?? "unknown os"}</span><i className={`dot ${l.key}`} title={l.title} /></span>
        <span className="mini-name">{m.name.toUpperCase()}{m.local && <small>hub</small>}</span>
        <span className="row"><span>{l.key === "online" ? activity(m.idleSeconds) ?? `pushed ${ago(m.lastPushAt, now)} ago` : l.label}</span>
          <span>cpu <b>{pct(m.cpuPercent)}</b> gpu <b>{pct(m.gpuPercent)}</b> ram <b>{pct(m.memPercent)}</b></span></span>
        <span className="row"><span>repos <b>{m.repos}</b></span><span>dirty <b>{m.dirty}</b></span><span>sync <b>{m.unsynced}</b></span></span>
        <span className="row"><span>services <b>{m.servicesUp}/{m.servicesTotal}</b></span><span>apps <b>–</b></span><span>doi <b>–</b></span></span>
      </button>
      {pinnable && m.status === "approved" && (
        <button className={`pin ${pinned ? "on" : ""}`} onClick={onPin} title={pinned ? "unpin tab" : "pin as a tab"}>{pinned ? "★" : "☆"}</button>
      )}
    </div>
  );
}

/** Hub in the middle, agents around it. Every agent reaches the hub over the tailnet; LAN grouping
 *  needs network facts the agents don't report yet. */
function NetMap({ machines, now, onOpen }: { machines: MachineSummary[]; now: number; onOpen: (m: MachineSummary) => void }) {
  const hub = machines.find((m) => m.local);
  const others = machines.filter((m) => !m.local);
  const W = 900, H = 440, cx = W / 2, cy = H / 2;
  const at = (i: number) => {
    const a = -Math.PI / 2 + (2 * Math.PI * i) / Math.max(others.length, 1);
    return { x: cx + Math.cos(a) * 300, y: cy + Math.sin(a) * 150 };
  };
  const node = (m: MachineSummary, x: number, y: number, r: number) => {
    const l = liveness(m, now);
    return (
      <g key={m.id} className={`node ${l.key} ${m.local ? "hub" : ""}`} transform={`translate(${x} ${y})`}
        onClick={() => onOpen(m)} role="button" tabIndex={0} onKeyDown={(e) => e.key === "Enter" && onOpen(m)}>
        <title>{l.title}</title>
        <circle r={r} />
        <text y={-2}>{m.name.toUpperCase()}</text>
        <text y={15} className="sub">{l.key !== "online" ? l.key : m.cpuPercent !== undefined ? `cpu ${Math.round(m.cpuPercent)}%` : m.local ? "hub" : `${m.repos} repos`}</text>
      </g>
    );
  };
  return (
    <div className="mapwrap">
      <svg className="map" viewBox={`0 0 ${W} ${H}`} role="img" aria-label="machines and how they reach the hub">
        <text x={24} y={30} className="zl">TAILNET</text>
        {others.map((m, i) => {
          const p = at(i);
          return <line key={m.id} x1={cx} y1={cy} x2={p.x} y2={p.y} className={`link ${liveness(m, now).key}`} />;
        })}
        {hub && node(hub, cx, cy, 48)}
        {others.map((m, i) => { const p = at(i); return node(m, p.x, p.y, 36); })}
        {!hub && others.length === 0 && <text x={cx} y={cy} className="zl" textAnchor="middle">no machines yet</text>}
      </svg>
      <div className="legend"><span><i className="ln tail" />tailnet</span><span><i className="ln stale" />stale</span><span><i className="ln pending" />pending</span>
        <span className="dim2">local networks appear once agents report them</span></div>
    </div>
  );
}
