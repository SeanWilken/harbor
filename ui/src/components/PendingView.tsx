import type { MachineSummary } from "../types";
import { ago } from "../format";
import { useState } from "react";
import { Pill } from "./Pill";
import { PairDialog } from "./PairDialog";

type Props = {
  machines: MachineSummary[];
  now: number;
  onOpen: (m: MachineSummary) => void;
  /** Absent for viewers: only admins decide (and pair). */
  onDecide?: (id: number, verb: "approve" | "reject") => void;
  onPaired: () => void;
  agentUrl?: string;
};

/** Machines that pushed but aren't approved, and the ones turned away. */
export function PendingView({ machines, now, onOpen, onDecide, onPaired, agentUrl }: Props) {
  const [pairing, setPairing] = useState(false);
  // agents push to the hub's tailnet address, not the browser's (which may be Serve or loopback)
  const hubUrl = agentUrl ?? `http://${location.hostname}:7420`;
  const rows = machines.filter((m) => m.status !== "approved")
    .sort((a, b) => (a.status === b.status ? b.firstSeenAt.localeCompare(a.firstSeenAt) : a.status === "pending" ? -1 : 1));
  return (
    <>
      <div className="panelbar">
        <span className="label">machines that pushed but are not approved</span>
        {onDecide && <Pill primary onClick={() => setPairing(true)}>pair a device</Pill>}
      </div>
      {pairing && <PairDialog hubUrl={hubUrl} onClose={() => setPairing(false)} onPaired={onPaired} />}
      <ul className="table pending">
        {rows.length > 0 && <li className="thead"><span>machine</span><span className="opt">tailscale identity</span><span className="opt">first push</span><span className="r">decision</span></li>}
        {rows.map((m) => (
          <li key={m.id} className={`trow ${m.status}`}>
            <button className="link name" onClick={() => onOpen(m)} title="preview what it pushed">
              {m.name}<small>{m.os ?? ""}</small>
            </button>
            <span className="opt mono dim">{[m.login, m.nodeName].filter(Boolean).join(" · ") || "—"}</span>
            <span className="opt dim">{ago(m.firstSeenAt, now)} ago</span>
            <span className="r decide">
              {!onDecide ? <span className="dim">{m.status} · admins decide</span>
                : m.status === "pending"
                ? <><Pill primary onClick={() => onDecide(m.id, "approve")}>approve</Pill><Pill onClick={() => onDecide(m.id, "reject")}>reject</Pill></>
                : <><span className="dim">rejected</span><Pill onClick={() => onDecide(m.id, "approve")}>approve</Pill></>}
            </span>
          </li>
        ))}
        {rows.length === 0 && <li className="empty">Nothing waiting. New machines show up here after their first push.</li>}
      </ul>
    </>
  );
}
