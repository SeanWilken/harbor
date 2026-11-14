import { useEffect, useRef, useState } from "react";
import type { MouseEvent as ReactMouseEvent, PointerEvent as ReactPointerEvent } from "react";
import type { RoleInfo, RoleState, Work } from "../types";
import { ago } from "../format";

export const workText: Record<Work, string> = {
  full: "takes heavy work",
  light: "light work only",
  none: "takes no outside work",
};

/** What each work level means to the rest of the suite (and to harbor itself). */
const capability: Record<Work, string[]> = {
  full: ["Spare capacity: builds, batch jobs and AI inference may be sent here.", "Harbor runs its fetches and package scans as usual."],
  light: ["Small jobs only; heavy work and inference go to other machines.", "Harbor runs its fetches and package scans as usual."],
  none: ["Busy with its own duty: no outside work is sent here.", "Harbor pauses its own git fetches and package scans; it keeps reporting."],
};

const titleOf = (d: RoleInfo) => d.title ?? d.name[0]!.toUpperCase() + d.name.slice(1);

type Drag = { x: number; y: number; t: number; axis?: "x" | "y"; dx: number };

/** Horizontal swipes (touch, pen or mouse) page the track; vertical ones scroll the page as usual.
 *  A swipe counts past a fifth of the width or on a quick flick; the ends resist. */
function useSwipe(count: number, page: number, setPage: (n: number) => void) {
  const viewport = useRef<HTMLDivElement>(null);
  const drag = useRef<Drag | null>(null);
  const moved = useRef(false);
  const [offset, setOffset] = useState(0);

  const onPointerDown = (e: ReactPointerEvent) => {
    if (e.pointerType === "mouse" && e.button !== 0) return;
    drag.current = { x: e.clientX, y: e.clientY, t: performance.now(), dx: 0 };
    moved.current = false;
  };
  const onPointerMove = (e: ReactPointerEvent) => {
    const d = drag.current;
    if (!d) return;
    const dx = e.clientX - d.x, dy = e.clientY - d.y;
    if (!d.axis) {
      if (Math.abs(dx) > 8 && Math.abs(dx) > Math.abs(dy)) {
        d.axis = "x";
        viewport.current?.setPointerCapture(e.pointerId);
      } else if (Math.abs(dy) > 8) d.axis = "y";
    }
    if (d.axis !== "x") return;
    moved.current = true;
    const atEdge = (page === 0 && dx > 0) || (page === count - 1 && dx < 0);
    d.dx = dx;
    setOffset(atEdge ? dx * 0.3 : dx);
  };
  const onPointerUp = () => {
    const d = drag.current;
    drag.current = null;
    if (!d || d.axis !== "x") return;
    const width = viewport.current?.clientWidth ?? 1;
    const speed = d.dx / Math.max(1, performance.now() - d.t); // px per ms
    const step = d.dx < -width / 5 || speed < -0.45 ? 1 : d.dx > width / 5 || speed > 0.45 ? -1 : 0;
    setPage(Math.max(0, Math.min(count - 1, page + step)));
    setOffset(0);
  };
  // a drag that ended on a button mustn't also press it
  const onClickCapture = (e: ReactMouseEvent) => {
    if (moved.current) { e.preventDefault(); e.stopPropagation(); moved.current = false; }
  };
  return { viewport, offset, dragging: offset !== 0, handlers: { onPointerDown, onPointerMove, onPointerUp, onPointerCancel: onPointerUp, onClickCapture } };
}

/** The machine's roles as cards: swipe or page left and right, inspect what a role does, activate one. */
export function RoleBar({ machine, role, now, onSwitch, switching }: {
  machine: string; role?: RoleState; now: number; onSwitch?: (name: string) => void; switching?: string;
}) {
  const declared = role?.declared ?? [];
  const current = Math.max(0, declared.findIndex((d) => d.name === role?.name));
  const [page, setPage] = useState(current);
  // open on the active role, and follow it when it changes
  useEffect(() => setPage(current), [role?.name, declared.length]);
  const { viewport, offset, dragging, handlers } = useSwipe(declared.length, page, setPage);

  if (!role) return null;
  const go = (n: number) => setPage(Math.max(0, Math.min(declared.length - 1, n)));
  return (
    <div className="roles">
      <div className="roles-head">
        <span className="label">role</span>
        {declared.length > 1 && (
          <span className="pager">
            <button className="link" onClick={() => go(page - 1)} disabled={page === 0} aria-label="previous role">‹</button>
            <span className="dots" role="tablist" aria-label={`${machine} roles`}>
              {declared.map((d, i) => (
                <button key={d.name} role="tab" aria-selected={i === page} aria-label={titleOf(d)}
                  className={`dotbtn ${i === page ? "on" : ""} ${d.name === role.name ? "active" : ""}`} onClick={() => go(i)} />
              ))}
            </span>
            <button className="link" onClick={() => go(page + 1)} disabled={page >= declared.length - 1} aria-label="next role">›</button>
          </span>
        )}
      </div>
      <div className="role-viewport" ref={viewport} {...handlers} tabIndex={0}
        onKeyDown={(e) => { if (e.key === "ArrowLeft") go(page - 1); if (e.key === "ArrowRight") go(page + 1); }}>
        <div className={`role-track ${dragging ? "dragging" : ""}`} style={{ transform: `translateX(calc(${-100 * page}% + ${offset}px))` }}>
          {declared.map((d, i) => (
            <RoleCard key={d.name} machine={machine} d={d} state={d.name === role.name ? role : undefined} now={now}
              shown={i === page} onActivate={onSwitch} switching={switching} />
          ))}
        </div>
      </div>
    </div>
  );
}

function RoleCard({ machine, d, state, now, shown, onActivate, switching }: {
  machine: string; d: RoleInfo; state?: RoleState; now: number; shown: boolean; onActivate?: (name: string) => void; switching?: string;
}) {
  const [inspect, setInspect] = useState(false);
  const title = titleOf(d);
  const activate = () => {
    if (window.confirm(`Switch ${machine.toUpperCase()} to ${title}? It runs this role's duties on ${machine}.`)) onActivate?.(d.name);
  };
  const duties = d.start === undefined ? undefined : [
    ...(d.stop ?? []).map((s) => `stop ${s}`),
    ...(d.power ? [`power profile: ${d.power}`] : []),
    ...d.start.map((s) => `start ${s}`),
  ];
  return (
    <section className={`role-card w-${d.work} ${state ? "active" : ""} ${shown ? "shown" : ""}`} aria-label={title} inert={!shown}>
      <div className="role-top">
        <span className={`work-chip w-${d.work}`}>{workText[d.work]}</span>
        {state && <span className="role-active"><i />active</span>}
      </div>
      <h3 className="role-title">{title}</h3>
      {d.title && <div className="role-name dim2">{d.name}</div>}
      <p className="role-desc">{d.description ?? capability[d.work][0]}</p>
      <div className={`role-detail ${inspect ? "open" : ""}`}>
        <div>
          <div className="label">capabilities</div>
          <ul>{capability[d.work].map((c) => <li key={c}>{c}</li>)}</ul>
          <div className="label">duties when activated</div>
          {duties === undefined
            ? <p className="dim2">Visible to hub admins.</p>
            : duties.length === 0
              ? <p className="dim2">None: switching only changes what the machine reports.</p>
              : <ul className="mono">{duties.map((x) => <li key={x}>{x}</li>)}</ul>}
          {state && state.applied.length > 0 && (
            <>
              <div className="label">last switch</div>
              <ul className="mono">{state.applied.map((x) => <li key={x} className={x.includes("failed") ? "warn-text" : undefined}>{x}</li>)}</ul>
            </>
          )}
        </div>
      </div>
      <div className="role-foot">
        <button className="link" onClick={() => setInspect(!inspect)} aria-expanded={inspect}>{inspect ? "hide details" : "inspect"}</button>
        {state
          ? <span className="dim2">{state.by === "default" ? "default role" : `since ${ago(state.since, now)} ago · ${state.by}`}</span>
          : onActivate && (switching
            ? <span className="dim">{switching === d.name ? "activating…" : `switching to ${switching}…`}</span>
            : <button className="role-go" onClick={activate}>Activate</button>)}
      </div>
    </section>
  );
}
