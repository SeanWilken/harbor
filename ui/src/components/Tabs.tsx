import type { MachineSummary } from "../types";
import { liveness } from "../status";

export type Tab = { kind: "this" } | { kind: "network" } | { kind: "pending" } | { kind: "machine"; id: number };

export const sameTab = (a: Tab, b: Tab) => a.kind === b.kind && (a.kind !== "machine" || (b.kind === "machine" && a.id === b.id));

export const tabHash = (t: Tab, sub?: string) =>
  t.kind === "machine" ? `#m/${t.id}` : `#${t.kind}${sub ? `/${sub}` : ""}`;

export function parseHash(hash: string): Tab | undefined {
  const m = hash.match(/^#m\/(\d+)$/);
  if (m) return { kind: "machine", id: Number(m[1]) };
  const k = hash.slice(1).split("/")[0];
  return k === "this" || k === "network" || k === "pending" ? { kind: k } : undefined;
}

type Props = {
  tab: Tab;
  machines: MachineSummary[];
  pins: number[];
  now: number;
  onTab: (t: Tab) => void;
};

const Dot = ({ m, now }: { m: MachineSummary; now: number }) =>
  <i className={`dot ${liveness(m, now).key}`} title={liveness(m, now).title} />;

/** Fixed tabs (this machine, network, pending), then pinned machines and the one opened from the grid. */
export function Tabs({ tab, machines, pins, now, onTab }: Props) {
  const local = machines.find((m) => m.local);
  const pending = machines.filter((m) => m.status === "pending").length;
  const opened = tab.kind === "machine" && !pins.includes(tab.id) ? [tab.id] : [];
  const extra = [...pins, ...opened].map((id) => machines.find((m) => m.id === id)).filter((m) => m && !m.local) as MachineSummary[];
  const button = (t: Tab, body: React.ReactNode, cls = "") => (
    <button key={tabHash(t)} role="tab" aria-selected={sameTab(t, tab)} className={`mtab ${cls} ${sameTab(t, tab) ? "on" : ""}`}
      onClick={() => onTab(t)}>{body}</button>
  );
  return (
    <nav className="mtabs" role="tablist" aria-label="views">
      <div className="fixed">
        {local && button({ kind: "this" }, <><Dot m={local} now={now} />This machine</>)}
        {button({ kind: "network" }, <>Network <small>{machines.filter((m) => m.status !== "rejected").length}</small></>)}
        {button({ kind: "pending" }, <>Pending <small className={pending ? "pend" : ""}>{pending}</small></>)}
      </div>
      <div className={`pinned ${extra.length ? "" : "none"}`}>
        {extra.map((m) => button({ kind: "machine", id: m.id }, <><Dot m={m} now={now} />{m.name}</>, pins.includes(m.id) ? "" : "temp"))}
        <button className="mtab add" onClick={() => onTab({ kind: "network" })} title="pin machines from the network grid (☆)">+</button>
      </div>
    </nav>
  );
}
