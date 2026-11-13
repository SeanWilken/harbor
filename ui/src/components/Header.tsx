import type { MachineSummary, Me } from "../types";

type Props = { machines: MachineSummary[]; me?: Me; onPending: () => void; onDevices: () => void };

/** Brand, fleet counts and who is looking. Network choice and the VPN switch land here later. */
export function Header({ machines, me, onPending, onDevices }: Props) {
  const pending = machines.filter((m) => m.status === "pending").length;
  const known = machines.filter((m) => m.status === "approved").length;
  return (
    <header className="top">
      <div className="brand" aria-label="harbor">HARB<span>⚓</span>R</div>
      <div className="fleet">
        <b>{known}</b> {known === 1 ? "machine" : "machines"}
        {pending > 0 && <> · <button className="link pend" onClick={onPending}>{pending} pending</button></>}
        {" · "}<button className="link" onClick={onDevices} title="QR codes for opening harbor and the desktop viewers on an iPad">on iPad</button>
      </div>
      {me && (
        <div className="viewer" title={me.node ? `${me.login} on ${me.node}` : "on the hub itself"}>
          <b>{me.login.replace(/@.*/, "")}</b>{me.role === "viewer" && <span className="role">view only</span>}
          <span className="avatar">{me.login.slice(0, 1)}</span>
        </div>
      )}
    </header>
  );
}
