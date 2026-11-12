import { useEffect, useState } from "react";
import { createPairingCode, pairingState, type PairingCode } from "../api";

/** A one-time code for a new machine, the commands to use it, and live "paired with …" once used. */
export function PairDialog({ hubUrl, onClose, onPaired }: { hubUrl: string; onClose: () => void; onPaired: () => void }) {
  const [code, setCode] = useState<PairingCode | null>(null);
  const [usedBy, setUsedBy] = useState<string | null>(null);
  const [err, setErr] = useState<string | null>(null);
  const [now, setNow] = useState(Date.now());

  const fresh = () => { setCode(null); setUsedBy(null); setErr(null); createPairingCode().then(setCode, (e: Error) => setErr(e.message)); };
  useEffect(fresh, []);
  useEffect(() => {
    if (!code || usedBy) return;
    const t = setInterval(() => {
      setNow(Date.now());
      pairingState(code.code).then((s) => { if (s.usedBy) { setUsedBy(s.usedBy); onPaired(); } }, () => undefined);
    }, 2000);
    return () => clearInterval(t);
  }, [code, usedBy, onPaired]);
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => { if (e.key === "Escape") onClose(); };
    window.addEventListener("keydown", onKey);
    return () => window.removeEventListener("keydown", onKey);
  }, [onClose]);

  const left = code ? Math.max(0, Math.round((Date.parse(code.expiresAt) - now) / 1000)) : 0;
  const expired = !!code && left === 0 && !usedBy;
  return (
    <div className="modal" role="dialog" aria-modal="true" aria-label="pair a device" onClick={onClose}>
      <div className="modal-box" onClick={(e) => e.stopPropagation()}>
        <div className="panelbar"><span className="label">pair a device</span><button className="link" onClick={onClose}>close</button></div>
        {err && <p className="empty">{err}</p>}
        {!code && !err && <p className="empty">creating a code…</p>}
        {code && (usedBy ? (
          <div className="pair-done"><span className="dot online" /> Paired: <b>{usedBy}</b> is approved and reporting.</div>
        ) : (
          <>
            <div className={`pair-code ${expired ? "expired" : ""}`}>{code.code}</div>
            <p className="pair-ttl mono">{expired ? "expired" : `one use · expires in ${Math.floor(left / 60)}:${String(left % 60).padStart(2, "0")}`}</p>
            <div className="pair-steps">
              <p>On the new machine, with <code>harbor-agent</code> installed:</p>
              <pre>harbor-agent pair {code.code} --hub {hubUrl}</pre>
              <p>then start it (or restart the service): <code>harbor-agent run</code>. On a desktop, the harbor tray's "Pair with hub…" does the same.</p>
              <p className="dim2">The machine is approved the moment its agent presents the code; it is still identified by its Tailscale node.</p>
            </div>
          </>
        ))}
        <div className="actions pair-actions">
          {(expired || usedBy) && <button className="pill on" onClick={fresh}>{usedBy ? "pair another" : "new code"}</button>}
        </div>
      </div>
    </div>
  );
}
