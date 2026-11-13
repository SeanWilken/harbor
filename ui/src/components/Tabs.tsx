import { useState } from "react";
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
  /** The browser's own machine, shown as "This machine". */
  self?: MachineSummary;
  pins: number[];
  now: number;
  onTab: (t: Tab) => void;
  onPin: (id: number) => void;
  onReorder: (pins: number[]) => void;
};

const Dot = ({ m, now }: { m: MachineSummary; now: number }) =>
  <i className={`dot ${liveness(m, now).key}`} title={liveness(m, now).title} />;

/** Moves `id` to where `target` is, shifting the rest. */
export const moveBefore = (ids: number[], id: number, target: number) => {
  if (id === target) return ids;
  const rest = ids.filter((x) => x !== id);
  const i = rest.indexOf(target);
  const at = ids.indexOf(id) < ids.indexOf(target) ? i + 1 : i;
  return [...rest.slice(0, at), id, ...rest.slice(at)];
};

/** Fixed tabs (this machine, network, pending), then pinned machines (drag to reorder) and the one opened from the grid (☆ saves it). */
export function Tabs({ tab, machines, self, pins, now, onTab, onPin, onReorder }: Props) {
  const [dragging, setDragging] = useState<number | null>(null);
  const pending = machines.filter((m) => m.status === "pending").length;
  const opened = tab.kind === "machine" && !pins.includes(tab.id) ? [tab.id] : [];
  const extra = [...pins, ...opened].map((id) => machines.find((m) => m.id === id)).filter((m) => m && m.id !== self?.id) as MachineSummary[];
  const button = (t: Tab, body: React.ReactNode, cls = "", drag?: React.HTMLAttributes<HTMLButtonElement> & { draggable?: boolean }) => (
    <button key={tabHash(t)} role="tab" aria-selected={sameTab(t, tab)} className={`mtab ${cls} ${sameTab(t, tab) ? "on" : ""}`}
      onClick={() => onTab(t)} {...drag}>{body}</button>
  );
  const dragProps = (id: number) => ({
    draggable: true,
    title: "drag to reorder",
    onDragStart: (e: React.DragEvent) => { setDragging(id); e.dataTransfer.effectAllowed = "move"; },
    onDragOver: (e: React.DragEvent) => { if (dragging !== null) e.preventDefault(); },
    onDrop: (e: React.DragEvent) => { e.preventDefault(); if (dragging !== null) onReorder(moveBefore(pins, dragging, id)); setDragging(null); },
    onDragEnd: () => setDragging(null),
  });
  return (
    <nav className="mtabs" role="tablist" aria-label="views">
      <div className="fixed">
        {self && button({ kind: "this" }, <><Dot m={self} now={now} />This machine</>)}
        {button({ kind: "network" }, <>Network <small>{machines.filter((m) => m.status !== "rejected").length}</small></>)}
        {button({ kind: "pending" }, <>Pending <small className={pending ? "pend" : ""}>{pending}</small></>)}
      </div>
      <div className={`pinned ${extra.length ? "" : "none"}`}>
        {extra.map((m) => {
          const pinned = pins.includes(m.id);
          const save = !pinned && m.status === "approved" && (
            <span className="tabpin" role="button" tabIndex={0} title="keep this tab" aria-label={`pin ${m.name}`}
              onClick={(e) => { e.stopPropagation(); onPin(m.id); }}
              onKeyDown={(e) => { if (e.key === "Enter" || e.key === " ") { e.preventDefault(); e.stopPropagation(); onPin(m.id); } }}>☆</span>
          );
          return button({ kind: "machine", id: m.id }, <><Dot m={m} now={now} />{m.name}{save}</>,
            `${pinned ? "" : "temp"} ${dragging === m.id ? "dragging" : ""}`, pinned ? dragProps(m.id) : undefined);
        })}
        <button className="mtab add" onClick={() => onTab({ kind: "network" })} title="open a machine from the network grid, then ☆ to keep its tab">+</button>
      </div>
    </nav>
  );
}
