import type { MachineSummary, Resources, Snapshot } from "../types";
import { ago, bytes, duration } from "../format";
import { activity, fullestDisk, isDirty, liveness, memPercent, oldestFetch, primaryAddress } from "../status";
import { Pill } from "./Pill";

type Props = {
  summary: MachineSummary;
  snap: Snapshot;
  now: number;
  pinned?: boolean;
  onPin?: () => void;
  /** Absent when this machine can't take jobs (not approved). */
  onFetchAll?: () => void;
  fetchingAll: boolean;
  /** Only the hub's own machine can be rescanned on demand. */
  onScan?: () => void;
  scanning: boolean;
};

/** A big figure with its label; `of` adds a quiet denominator and a bar. */
function Figure({ label, n, of, warn }: { label: string; n: number; of?: number; warn?: boolean }) {
  return (
    <div className="figure">
      <div className="label">{label}</div>
      <div className={`num ${warn && n > 0 ? "warn" : ""}`}>{n}{of !== undefined && <small> / {of}</small>}</div>
      {of !== undefined && <div className="bar"><span style={{ width: `${of ? (100 * n) / of : 0}%` }} /></div>}
    </div>
  );
}

/** A percentage with a bar; "–" when unknown. Warns from 90%. */
function Pct({ label, value, title }: { label: string; value?: number; title?: string }) {
  const v = value === undefined ? undefined : Math.round(value);
  return (
    <div className="figure" title={title}>
      <div className="label">{label}</div>
      <div className={`num ${v !== undefined && v >= 90 ? "warn" : ""}`}>{v ?? "–"}{v !== undefined && <small>%</small>}</div>
      <div className="bar"><span style={{ width: `${v ?? 0}%` }} /></div>
    </div>
  );
}

function Text({ label, value, small, title }: { label: string; value: string; small?: string; title?: string }) {
  return (
    <div className="figure" title={title}>
      <div className="label">{label}</div>
      <div className="num text">{value}{small && <small> {small}</small>}</div>
    </div>
  );
}

function ResourceFigures({ r }: { r: Resources }) {
  const gpu = r.gpus[0];
  const disk = fullestDisk(r);
  const mem = memPercent(r);
  return (
    <div className="figures">
      <Pct label="cpu" value={r.cpuPercent} title={`${r.cores} cores · average since the previous push`} />
      <Pct label="gpu" value={gpu?.percent} title={gpu ? `${gpu.name} · ${bytes(gpu.memUsedBytes)} / ${bytes(gpu.memTotalBytes)} VRAM` : "no GPU reported"} />
      <Pct label="ram" value={mem} title={r.memTotalBytes ? `${bytes(r.memTotalBytes - (r.memAvailableBytes ?? 0))} of ${bytes(r.memTotalBytes)}` : undefined} />
      {disk
        ? <Pct label={`disk ${disk.mount}`} value={100 * (1 - disk.freeBytes / disk.totalBytes)} title={`${bytes(disk.freeBytes)} free of ${bytes(disk.totalBytes)} (${disk.format}) · fullest disk`} />
        : <Text label="disk" value="–" />}
      {r.load1 !== undefined ? <Text label="load" value={r.load1.toFixed(1)} small={`/ ${r.cores}`} title="1-minute load average / cores" /> : <Text label="cores" value={String(r.cores)} />}
      <Text label="up" value={duration(r.uptimeSeconds)} />
    </div>
  );
}

function RepoFigures({ repos, up, total }: { repos: Snapshot["repos"]; up: number; total: number }) {
  return (
    <div className="figures">
      <Figure label="repos" n={repos.length} />
      <Figure label="dirty" n={repos.filter(isDirty).length} warn />
      <Figure label="behind" n={repos.filter((r) => (r.branch?.behind ?? 0) > 0).length} warn />
      <Figure label="unpushed" n={repos.filter((r) => (r.branch?.ahead ?? 0) > 0).length} warn />
      <Figure label="local only" n={repos.filter((r) => r.remotes.length === 0).length} />
      <Figure label="services up" n={up} of={total} />
    </div>
  );
}

function TopProcesses({ r }: { r: Resources }) {
  if (r.top.length === 0) return null;
  return (
    <div className="procs">
      <div className="label">top processes</div>
      <table>
        <tbody>
          {r.top.map((p) => (
            <tr key={p.name}>
              <td>{p.name}{p.count > 1 && <span className="dim2"> ×{p.count}</span>}</td>
              <td className="r">{p.cpuPercent.toFixed(1)}%</td>
              <td className="r">{bytes(p.rssBytes)}</td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}

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

/** The top of a machine's card: identity and connection, headline figures, access and actions. */
export function MachinePanel({ summary, snap, now, pinned, onPin, onFetchAll, fetchingAll, onScan, scanning }: Props) {
  const repos = snap.repos.filter((r) => !summary.hidden.includes(r.path));
  const up = snap.services.filter((s) => s.state === "running").length;
  const fetched = oldestFetch(repos);
  const l = liveness(summary, now);
  const res = snap.resources;
  const addr = primaryAddress(snap.networks);
  const nets = (snap.networks ?? []).map((n) => `${n.kind} ${n.name}: ${n.addresses.map((a) => `${a.address}/${a.prefixLength}`).join(", ")}`);
  const act = l.key === "online" ? activity(res?.idleSeconds) : undefined;
  return (
    <div className="machine">
      <div className="idline">
        <span className="os">
          {snap.machine.os}
          {summary.agentCommit && <span className="dim2" title={`harbor build ${summary.agentCommit}`}> · harbor {summary.agentCommit.slice(0, 7)}</span>}
          {summary.update && <span className="warn-text" title={`latest ${summary.update.latest} (${new Date(summary.update.latestAt).toLocaleString()})`}> · {summary.update.behind} newer</span>}
        </span>
        <span className={`conn ${l.key}`} title={[l.title, summary.connected ? "live link to the hub: jobs arrive at once" : "no live link: jobs wait for the next push", ...nets].join("\n")}>
          <i />{l.label}{summary.connected && !summary.local ? " · linked" : ""}{addr ? ` · ${addr}` : summary.nodeName ? ` · ${summary.nodeName}` : ""}
        </span>
      </div>
      <div className="who-col">
        <h1 className="hostname">
          {summary.name.toUpperCase()}
          {onPin && <button className={`pin ${pinned ? "on" : ""}`} onClick={onPin} title={pinned ? "unpin tab" : "pin as a tab"}>{pinned ? "★" : "☆"}</button>}
        </h1>
        <div className="status">
          <span className="label">status</span>
          <b>{act ?? l.key}</b>
          <span className="quiet">
            {res?.idleSeconds !== undefined && l.key === "online" ? `input ${res.idleSeconds < 60 ? `${res.idleSeconds}s` : duration(res.idleSeconds)} ago · ` : ""}
            pushed {ago(summary.lastPushAt, now)} ago · scanned in {snap.machine.scanMs} ms
            {" · "}{fetched ? `fetched within ${ago(fetched, now)}` : "some repos never fetched"}
          </span>
        </div>
        {l.key === "pending" && <p className="note">Preview of what {summary.nodeName ?? summary.name} pushed. Nothing is stored until you approve it.</p>}
        {l.key === "stale" && <p className="note">Not pushing: this is the last snapshot it sent, {ago(summary.lastPushAt, now)} ago.</p>}
        {res ? <ResourceFigures r={res} /> : <RepoFigures repos={repos} up={up} total={snap.services.length} />}
      </div>
      <div className="side">
        <div className="act off" title="RDP and other remote-control entries will come from harbor's own access catalog">
          <MouseIcon /><span><strong>Remote control</strong><small>not configured</small></span>
        </div>
        <div className="act off" title="optional display support (DisplayOverInternet's runtime) is not installed in this harbor">
          <ScreenIcon /><span><strong>Desktop view</strong><small>display support not installed</small></span>
        </div>
        {res && <TopProcesses r={res} />}
        {(onFetchAll || onScan) && (
          <div className="actions">
            {onFetchAll && (
              <Pill onClick={onFetchAll} on={fetchingAll} title="git fetch every repo with a remote on this machine (F)">
                {fetchingAll ? "fetching…" : "fetch all"}
              </Pill>
            )}
            {onScan && <Pill onClick={onScan} on={scanning} title="rescan repos now (r)">{scanning ? "scanning…" : "rescan"}</Pill>}
          </div>
        )}
      </div>
    </div>
  );
}
