import type { ServiceStatus } from "../types";
import { Mark } from "./Mark";

const TONE = { running: "ok", stopped: "danger", unknown: "warn" } as const;

export function ServicesTable({ services }: { services: ServiceStatus[] }) {
  if (services.length === 0) return <p className="empty">No services configured. Add "services" to this machine's harbor.json.</p>;
  return (
    <ul className="table services">
      <li className="thead"><span>service</span><span className="opt">check</span><span className="r">state</span></li>
      {services.map((s) => (
        <li key={s.name} className="trow">
          <span className="name">{s.name}</span>
          <span className="opt mono dim" title={s.detail}>{s.check}</span>
          <span className="r"><Mark tone={TONE[s.state]} title={s.detail}>{s.state}</Mark></span>
        </li>
      ))}
    </ul>
  );
}
