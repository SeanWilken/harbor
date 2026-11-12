import type { ReactNode } from "react";

/** Status colours from the theme, for state only (style guide §3). Unset = plain fg text. */
export type Tone = "ok" | "warn" | "danger" | "info" | "special" | "dim";

const VAR: Record<Tone, string> = {
  ok: "var(--ok)", warn: "var(--warn)", danger: "var(--danger)", info: "var(--accent)", special: "var(--warn)", dim: "var(--dim1)",
};

/** Inline status text: not a pill, not interactive. */
export function Mark({ children, tone, title }: { children: ReactNode; tone?: Tone; title?: string }) {
  return <span className="mark" style={tone ? { color: VAR[tone] } : undefined} title={title}>{children}</span>;
}
