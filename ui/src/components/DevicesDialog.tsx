import { useEffect, useState } from "react";
import type { AccessEntry, MachineSummary, Me } from "../types";
import { fetchSnapshot } from "../api";
import { Qr } from "./Qr";

const SERVE = "sudo tailscale serve --bg --https=7443 http://127.0.0.1:7425";

/** Reachable from this browser? no-cors: an answer of any kind counts, content is never read. */
function useReachable(url?: string) {
  const [up, setUp] = useState<boolean>();
  useEffect(() => {
    if (!url) return;
    let live = true;
    fetch(url, { mode: "no-cors", cache: "no-store", signal: AbortSignal.timeout(5000) })
      .then(() => live && setUp(true), () => live && setUp(false));
    return () => { live = false; };
  }, [url]);
  return up;
}

type Viewer = { machine: string; entry: AccessEntry };

/** Getting an iPad (or phone) onto harbor, then onto each machine's desktop viewer. */
export function DevicesDialog({ me, machines, onClose }: { me?: Me; machines: MachineSummary[]; onClose: () => void }) {
  const url = me?.browserUrl ?? (location.protocol === "https:" ? location.origin : undefined);
  const up = useReachable(url);
  const [viewers, setViewers] = useState<Viewer[]>([]);
  const [shown, setShown] = useState<string>();

  useEffect(() => {
    const approved = machines.filter((m) => m.status === "approved");
    Promise.all(approved.map((m) => fetchSnapshot(m.id).then(
      (s) => (s.access ?? []).filter((a) => a.kind === "display" && a.url).map((entry) => ({ machine: m.name, entry })),
      () => [] as Viewer[])))
      .then((vs) => setViewers(vs.flat()));
  }, [machines]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  return (
    <div className="modal" role="dialog" aria-modal="true" aria-label="open on an iPad" onClick={onClose}>
      <div className="modal-box" onClick={(e) => e.stopPropagation()}>
        <div className="panelbar"><span className="label">open on an iPad or phone</span><button className="link" onClick={onClose}>close</button></div>
        <div className="pair-steps">
          {url ? (
            <div className="qr-row">
              <Qr text={url} />
              <div>
                <p className="mono qr-url">{url}</p>
                <p className="dim2">{up === undefined ? "checking from this browser…" : up ? "answering over HTTPS" : "no answer yet: HTTPS isn't set up on the hub"}</p>
              </div>
            </div>
          ) : <p>The hub doesn't know its HTTPS address. Set <code>hub.browserUrl</code> or <code>hub.serveListen</code> in its config.</p>}
          {up === false && <><p>Once, on the hub machine:</p><pre>{SERVE}</pre></>}
          <ol>
            <li>Install <b>Tailscale</b> on the iPad and sign in to the same tailnet. Your Tailscale login is who harbor sees: admins get everything, logins in <code>hub.viewers</code> get a read-only view.</li>
            <li>Point the camera at the code and open the link in Safari.</li>
            <li>Share → <b>Add to Home Screen</b> for a harbor app icon.</li>
          </ol>
          <div className="label section">desktop viewers</div>
          {viewers.length === 0
            ? <p className="dim2">No machine lists one yet. Add <code>{`"access": [{ "kind": "display", "url": "https://…" }]`}</code> to a machine's harbor config.</p>
            : (
              <ul className="viewers">
                {viewers.map((v) => (
                  <li key={v.entry.url}>
                    <button className="link" onClick={() => setShown(shown === v.entry.url ? undefined : v.entry.url)}>{v.machine}</button>
                    <span className="mono dim2">{v.entry.url}</span>
                    {shown === v.entry.url && v.entry.url && <div className="qr-row"><Qr text={v.entry.url} size={140} /></div>}
                  </li>
                ))}
              </ul>
            )}
          <p className="dim2">Each viewer pairs a device itself, once, with a one-time code from that machine. When a viewer is mounted under harbor (Desktop view says \"through harbor\"), it opens inside the harbor app and pairs there; harbor carries that pairing but never reads or keeps it. Otherwise add the viewer to the Home Screen as its own icon.</p>
        </div>
      </div>
    </div>
  );
}
