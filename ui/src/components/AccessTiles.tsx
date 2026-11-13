import { useEffect, useState } from "react";
import type { AccessEntry } from "../types";

const MouseIcon = () => (
  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
    <path d="M4 4l6.5 16 2.3-6.7L19.5 11z" /><path d="M13 13l6 6" />
  </svg>
);
const ScreenIcon = () => (
  <svg width="24" height="24" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="1.4" aria-hidden>
    <rect x="3" y="4" width="18" height="12" rx="1.5" /><path d="M8 20h8M12 16v4" />
  </svg>
);

/** Windows App (Microsoft Remote Desktop) on iPad, Mac and Windows opens rdp:// links. */
export const rdpLink = (host: string, port = 3389) => `rdp://full%20address=s:${host}:${port}`;
const hostOf = (url: string) => { try { return new URL(url).host; } catch { return url; } };

/** Whether this browser gets any answer from the page. no-cors: we only learn reachable or not, never content. */
function useReachable(url?: string) {
  const [state, setState] = useState<"checking" | "up" | "down">();
  useEffect(() => {
    if (!url) return;
    let live = true;
    setState("checking");
    const ctl = new AbortController();
    const t = setTimeout(() => ctl.abort(), 5000);
    fetch(url, { mode: "no-cors", signal: ctl.signal, cache: "no-store" })
      .then(() => live && setState("up"), () => live && setState("down"))
      .finally(() => clearTimeout(t));
    return () => { live = false; ctl.abort(); };
  }, [url]);
  return state;
}

/** The viewer under harbor's own address (/display/<machine>/), when this page is on HTTPS and the
 *  viewer answers there (it has a mount for this machine). One Home Screen app then reaches every
 *  machine; the viewer still pairs each device itself. */
function useMount(machine: string, display?: AccessEntry) {
  const [mount, setMount] = useState<string>();
  useEffect(() => {
    setMount(undefined);
    if (!display?.url || location.protocol !== "https:" || !/^[a-z0-9][a-z0-9-]{0,62}$/.test(machine)) return;
    let live = true;
    const path = `/display/${machine}/`;
    fetch(`${path}api/session`, { credentials: "same-origin", cache: "no-store" })
      .then((r) => live && (r.status === 200 || r.status === 401) && (r.headers.get("content-type") ?? "").includes("json") && setMount(path), () => {});
    return () => { live = false; };
  }, [machine, display?.url]);
  return mount;
}

/** `here`: open in this window. A Home Screen app sends new-tab links to Safari, which has its own cookies. */
function Tile({ href, icon, title, detail, hint, off, here }: { href?: string; icon: React.ReactNode; title: string; detail: string; hint: string; off?: boolean; here?: boolean }) {
  const body = <>{icon}<span><strong>{title}</strong><small>{detail}</small></span></>;
  return href && !off
    ? <a className="act" href={href} target={here ? undefined : "_blank"} rel="noopener noreferrer" title={hint}>{body}</a>
    : <div className="act off" title={hint}>{body}</div>;
}

const howTo = (kind: string) => kind === "display"
  ? 'add to this machine\'s harbor config: "access": [{ "kind": "display", "url": "https://<machine>.<tailnet>.ts.net" }]'
  : 'add to this machine\'s harbor config: "access": [{ "kind": "rdp", "host": "<machine>" }]';

/** Desktop view (a display viewer page that pairs devices itself) and Remote control (RDP), from the machine's config. */
export function AccessTiles({ machine, access }: { machine: string; access?: AccessEntry[] }) {
  const display = access?.find((a) => a.kind === "display");
  const rdp = access?.find((a) => a.kind === "rdp");
  const mount = useMount(machine, display);
  const reach = useReachable(mount ? undefined : display?.url);
  const displayDetail = !display?.url ? "not configured"
    : mount ? "through harbor"
    : `${hostOf(display.url)}${reach === "down" ? " · no answer" : reach === "checking" ? " · checking" : ""}`;
  return (
    <>
      <Tile href={rdp?.host ? rdpLink(rdp.host, rdp.port) : undefined} icon={<MouseIcon />} title={rdp?.name ?? "Remote control"}
        detail={rdp?.host ? `RDP ${rdp.host}${rdp.port && rdp.port !== 3389 ? `:${rdp.port}` : ""}` : "not configured"}
        hint={rdp?.host ? "opens Windows App (Remote Desktop); it asks for the Windows sign-in itself" : howTo("rdp")} />
      <Tile href={mount ?? display?.url} here={!!mount} icon={<ScreenIcon />} title={display?.name ?? "Desktop view"} detail={displayDetail}
        hint={display?.url ? "opens this machine\'s display viewer; each device pairs there once with a code from that machine" : howTo("display")} />
    </>
  );
}
