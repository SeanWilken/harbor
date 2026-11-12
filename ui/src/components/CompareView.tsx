import { useEffect, useState } from "react";
import type { MachineSummary, ManifestDiff } from "../types";
import { fetchDiff, fetchManifests } from "../api";
import { ChangesList } from "./ChangesList";

/** Two machines' latest manifests side by side: OS and package differences. */
export function CompareView({ machines, admin }: { machines: MachineSummary[]; admin: boolean }) {
  const approved = machines.filter((m) => m.status === "approved");
  const [a, setA] = useState<number | undefined>(approved[0]?.id);
  const [b, setB] = useState<number | undefined>(approved[1]?.id);
  const [diff, setDiff] = useState<ManifestDiff | null>(null);
  const [note, setNote] = useState<string | null>(null);

  useEffect(() => {
    setDiff(null); setNote(null);
    if (!admin || a === undefined || b === undefined || a === b) return;
    let live = true;
    Promise.all([fetchManifests(a), fetchManifests(b)]).then(([ha, hb]) => {
      if (!live) return;
      if (!ha[0] || !hb[0]) { setNote("One of these machines hasn't sent a manifest yet."); return; }
      fetchDiff(ha[0].id, hb[0].id).then((d) => live && setDiff(d), (e: Error) => live && setNote(e.message));
    }, (e: Error) => live && setNote(e.message));
    return () => { live = false; };
  }, [a, b, admin]);

  if (!admin) return <p className="empty">Comparing package lists is for admins only.</p>;
  if (approved.length < 2) return <p className="empty">Comparing needs two approved machines; this hub has {approved.length}.</p>;
  const name = (id?: number) => approved.find((m) => m.id === id)?.name ?? "?";
  const pick = (value: number | undefined, set: (n: number) => void, label: string) => (
    <label className="pick"><span className="label">{label}</span>
      <select value={value} onChange={(e) => set(Number(e.target.value))}>
        {approved.map((m) => <option key={m.id} value={m.id}>{m.name}</option>)}
      </select>
    </label>
  );
  const onlyA = diff ? diff.managers.a.filter((x) => !diff.managers.b.includes(x)) : [];
  const onlyB = diff ? diff.managers.b.filter((x) => !diff.managers.a.includes(x)) : [];
  return (
    <div className="compare">
      <div className="compare-picks">{pick(a, setA, "machine a")}{pick(b, setB, "machine b")}</div>
      {a === b && <p className="empty">Pick two different machines.</p>}
      {note && <p className="empty">{note}</p>}
      {diff && <>
        <p className="mono dim compare-os">
          {diff.os ? <>OS differs: {diff.os.a.prettyName} ({diff.os.a.kernel}) vs {diff.os.b.prettyName} ({diff.os.b.kernel})</> : <>Same OS: {diff.a.os}</>}
          {(onlyA.length > 0 || onlyB.length > 0) && <> · not compared: {[...onlyA.map((x) => `${x} (only ${name(a)})`), ...onlyB.map((x) => `${x} (only ${name(b)})`)].join(", ")}</>}
        </p>
        <ChangesList changes={diff.changes} left={name(a)} right={name(b)} />
      </>}
    </div>
  );
}
