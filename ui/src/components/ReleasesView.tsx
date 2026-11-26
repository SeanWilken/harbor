import { useCallback, useEffect, useMemo, useState } from "react";
import type { JobInfo, ReleaseStep, UnitView } from "../types";
import { fetchReleases, requestRelease, saveRecipe } from "../api";
import { ago } from "../format";

const STEP_HINT: Record<ReleaseStep, string> = {
  pull: "pull images from their registry",
  build: "build images that have a build section",
  migrate: "apply pending migrations with the repo's runner",
  up: "recreate the containers",
};

/** How many migrations a status check found pending, from its JSON: one object or one per line
 * (events), using the last that says `pending` (a list or a count) or `upToDate`. Undefined if none does. */
export function pendingOf(job?: JobInfo): number | undefined {
  if (!job?.ok) return undefined;
  let found: number | undefined;
  for (const line of (job.output ?? "").split("\n")) {
    const t = line.trim();
    if (!t.startsWith("{")) continue;
    try {
      const o = JSON.parse(t) as { pending?: unknown; upToDate?: unknown };
      if (Array.isArray(o.pending)) found = o.pending.length;
      else if (typeof o.pending === "number") found = o.pending;
      else if (o.upToDate === true) found = 0;
    } catch { /* not a JSON line */ }
  }
  return found;
}

const running = (j?: JobInfo) => !!j && !j.finishedAt;
const shortImage = (i?: string) => i?.replace(/^docker\.io\//, "").replace(/^library\//, "") ?? "build only";

function RunLine({ label, job, now }: { label: string; job?: JobInfo; now: number }) {
  if (!job) return null;
  const state = running(job) ? (job.sentAt ? "running…" : "waiting for the machine…") : job.ok ? "worked" : "failed";
  const when = job.finishedAt ?? job.requestedAt;
  return (
    <details className={`relrun ${running(job) ? "busy" : job.ok ? "ok" : "bad"}`}>
      <summary>
        <span>{label} <b>{state}</b></span>
        <span className="dim2" title={new Date(when).toLocaleString()}>{ago(when, now)} ago{job.requestedBy && ` · ${job.requestedBy.replace(/@.*/, "")}`}</span>
      </summary>
      {job.output ? <pre>{job.output}</pre> : <p className="dim2">{running(job) ? "Output arrives when it finishes." : "No output kept."}</p>}
    </details>
  );
}

function UnitCard({ v, admin, now, onChanged }: { v: UnitView; admin: boolean; now: number; onChanged: () => void }) {
  const u = v.unit;
  const [profile, setProfile] = useState(v.recipe.profile);
  const [steps, setSteps] = useState<ReleaseStep[]>(v.recipe.steps.filter((s) => u.steps.includes(s)));
  const [err, setErr] = useState<string>();
  const [busy, setBusy] = useState(false);
  useEffect(() => { setProfile(v.recipe.profile); setSteps(v.recipe.steps.filter((s) => u.steps.includes(s))); }, [v.recipe, u.steps]);

  const edited = profile !== v.recipe.profile || steps.join() !== v.recipe.steps.join();
  const pending = pendingOf(v.lastStatus);
  const statusFresh = v.lastStatus?.finishedAt && now - Date.parse(v.lastStatus.finishedAt) < 10 * 60_000;
  const inFlight = running(v.lastRun) || running(v.lastStatus);
  const byService = new Map(u.running.map((c) => [c.service, c]));
  const name = u.path ? `${v.repoName} / ${u.path}` : v.repoName;
  const hasProfiles = u.profiles.length > 0;
  const noProfileOk = u.services.some((s) => s.profiles.length === 0);

  const act = async (work: () => Promise<unknown>) => {
    setBusy(true);
    try { await work(); setErr(undefined); onChanged(); } catch (e) { setErr((e as Error).message); } finally { setBusy(false); }
  };
  const toggle = (s: ReleaseStep) => setSteps((xs) => (xs.includes(s) ? xs.filter((x) => x !== s) : u.steps.filter((x) => x === s || xs.includes(x))));
  const checkStatus = () => act(() => requestRelease(v.machineId, v.repoPath, u.path, undefined, ["status"]));
  const release = () => {
    if (steps.includes("migrate") && !statusFresh) {
      setErr("Check migrations first, so you can see what will be applied.");
      return;
    }
    const mig = steps.includes("migrate") ? `migrate (${pending === undefined ? "pending unknown" : `${pending} pending`})` : undefined;
    const list = steps.map((s) => (s === "migrate" ? mig : s)).join(", ");
    if (!window.confirm(`Release ${name} on ${v.machine}${profile ? ` with profile ${profile}` : ""}: ${list}?`)) return;
    void act(() => requestRelease(v.machineId, v.repoPath, u.path, profile, steps));
  };

  return (
    <li className="relunit">
      <div className="relhead">
        <div>
          <h4>{name}</h4>
          <span className="dim2" title={`${v.repoPath}${u.path ? `/${u.path}` : ""}/${u.compose}`}>
            {v.machine} · {v.repoPath.split(/[\\/]/).slice(-2).join("/")}{hasProfiles && ` · profiles ${u.profiles.join(", ")}`}
          </span>
        </div>
        <span className={u.running.length === 0 ? "dim2" : u.running.every((c) => c.state === "running") ? "ok-text" : "warn-text"}>
          {u.running.length === 0 ? "not running" : `${u.running.filter((c) => c.state === "running").length}/${u.running.length} running`}
        </span>
      </div>

      <ul className="relsvcs">
        {u.services.map((s) => {
          const c = byService.get(s.name);
          const drift = c && s.image && shortImage(c.image) !== shortImage(s.image);
          return (
            <li key={s.name}>
              <i className={`dot ${c?.state === "running" ? "online" : c ? "rejected" : ""}`} title={c ? c.state : "no container"} />
              <span>{s.name}</span>
              <span className="mono dim" title={drift ? `running ${c.image}; compose names ${s.image}` : undefined}>
                {shortImage(c?.image ?? s.image)}{drift && <b className="warn-text"> → {shortImage(s.image)}</b>}
              </span>
              {s.profiles.length > 0 && <span className="dim2">{s.profiles.join(" ")}</span>}
            </li>
          );
        })}
      </ul>

      {u.migrations && (
        <p className="relmig">
          <span>{u.migrations.files} migrations in <span className="mono">{u.migrations.dir}</span>{u.migrations.latest && <>, latest <span className="mono">{u.migrations.latest}</span></>}</span>
          {pending !== undefined && <b className={pending ? "warn-text" : "ok-text"}>{pending ? `${pending} pending` : "all applied"}</b>}
        </p>
      )}
      {u.problems.map((p) => <p key={p} className="warn-text relprob">{p}</p>)}

      {admin && u.steps.length > 0 && (
        <div className="relrecipe">
          {hasProfiles && (
            <label className="relprofile">profile{" "}
              <select value={profile ?? ""} onChange={(e) => setProfile(e.target.value || undefined)}>
                {noProfileOk && <option value="">(services without one)</option>}
                {u.profiles.map((p) => <option key={p} value={p}>{p}</option>)}
              </select>
            </label>
          )}
          <span className="relsteps" role="group" aria-label="steps">
            {u.steps.map((s) => (
              <button key={s} className={`chip ${steps.includes(s) ? "on" : ""}`} aria-pressed={steps.includes(s)} title={STEP_HINT[s]} onClick={() => toggle(s)}>{s}</button>
            ))}
          </span>
          {edited && <button className="link" disabled={busy || steps.length === 0} onClick={() => void act(() => saveRecipe(v.repoKey, u.path, profile, steps))}
            title="every clone of this repo starts from this recipe">save as this repo's recipe</button>}
        </div>
      )}
      {admin && (
        <div className="relactions">
          {u.steps.includes("migrate") && <button className="btn" disabled={busy || inFlight} onClick={() => void checkStatus()}>Check migrations</button>}
          <button className="btn primary" disabled={busy || inFlight || steps.length === 0} onClick={release}>Release</button>
          {!v.saved && <span className="dim2">harbor's default recipe</span>}
        </div>
      )}
      {err && <p className="warn-text">{err}</p>}
      <RunLine label="release" job={v.lastRun} now={now} />
      <RunLine label="migration check" job={v.lastStatus} now={now} />
    </li>
  );
}

/** Compose folders found in tracked repos, on every machine: what runs, how it's released, and the last runs. */
export function ReleasesView({ admin, now }: { admin: boolean; now: number }) {
  const [units, setUnits] = useState<UnitView[]>();
  const [err, setErr] = useState<string>();
  const load = useCallback(() => fetchReleases().then((u) => { setUnits(u); setErr(undefined); }, (e: Error) => setErr(e.message)), []);
  const busy = useMemo(() => units?.some((v) => running(v.lastRun) || running(v.lastStatus)) ?? false, [units]);
  useEffect(() => {
    void load();
    const t = setInterval(() => void load(), busy ? 4_000 : 30_000);
    return () => clearInterval(t);
  }, [load, busy]);

  if (!units) return <p className="empty">{err ?? "loading…"}</p>;
  if (units.length === 0)
    return <p className="empty">No compose files in tracked repos yet. Harbor looks for compose.yaml or docker-compose.yml at a repo's root and up to three folders down.</p>;
  return (
    <div className="releases">
      {err && <p className="warn-text">{err}</p>}
      <ul className="relunits">
        {[...units].sort((a, b) => Number(b.unit.running.length > 0) - Number(a.unit.running.length > 0) || a.repoName.localeCompare(b.repoName)).map((v) => <UnitCard key={`${v.machineId}:${v.repoPath}:${v.unit.path}`} v={v} admin={admin} now={now} onChanged={() => void load()} />)}
      </ul>
      <p className="dim2 relnote">A machine runs releases only if its config allows them (<span className="mono">"release"</span> in agent.allow). See docs/RELEASE_INTEGRATION.md.</p>
    </div>
  );
}
