import type { Disk } from "../types";
import { bytes } from "../format";

export function DisksTable({ disks }: { disks?: Disk[] }) {
  if (!disks) return <p className="empty">This agent doesn't report disks yet; update it to see them.</p>;
  if (disks.length === 0) return <p className="empty">No disks reported.</p>;
  return (
    <ul className="table disks">
      <li className="thead"><span>mount</span><span className="opt">format</span><span>used</span><span className="r">free</span></li>
      {disks.map((d) => {
        const used = d.totalBytes ? (100 * (d.totalBytes - d.freeBytes)) / d.totalBytes : 0;
        return (
          <li key={d.mount} className="trow">
            <span className="name mono">{d.mount}</span>
            <span className="opt mono dim">{d.format}</span>
            <span className="usage">
              <span className={`bar ${used >= 90 ? "warn" : ""}`}><span style={{ width: `${used}%` }} /></span>
              <span className="mono dim">{Math.round(used)}% of {bytes(d.totalBytes)}</span>
            </span>
            <span className="r mono">{bytes(d.freeBytes)}</span>
          </li>
        );
      })}
    </ul>
  );
}
