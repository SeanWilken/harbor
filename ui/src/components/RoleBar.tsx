import type { RoleState, Work } from "../types";
import { ago } from "../format";

export const workText: Record<Work, string> = {
  full: "takes heavy work",
  light: "light work only",
  none: "takes no outside work",
};

/** The machine's current role and what it means for outside work; admins can switch it. */
export function RoleBar({ machine, role, now, onSwitch, switching }: {
  machine: string; role?: RoleState; now: number; onSwitch?: (name: string) => void; switching?: string;
}) {
  if (!role) return null;
  const others = role.declared.filter((d) => d.name !== role.name);
  const ask = (name: string) => {
    if (window.confirm(`Switch ${machine.toUpperCase()} to ${name}? It runs that role's start and stop actions on ${machine}.`)) onSwitch?.(name);
  };
  return (
    <div className={`rolebar w-${role.work}`}>
      <div className="label">role</div>
      <div className="role-now">
        <b>{role.name}</b>
        <span className="role-work">{workText[role.work]}</span>
      </div>
      <div className="dim2 role-since" title={role.applied.join("\n") || undefined}>
        {role.by === "default" ? "default" : `since ${ago(role.since, now)} ago · ${role.by}`}
      </div>
      {onSwitch && others.length > 0 && (
        <div className="role-switch">
          {switching
            ? <span className="dim">switching to {switching}…</span>
            : others.map((d) => (
                <button key={d.name} className="link" onClick={() => ask(d.name)} title={workText[d.work]}>→ {d.name}</button>
              ))}
        </div>
      )}
    </div>
  );
}
