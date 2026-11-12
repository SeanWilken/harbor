import type { ReactNode } from "react";

type Props = { children: ReactNode; onClick: () => void; on?: boolean; primary?: boolean; title?: string };

/** Small rounded button: outlined when idle, accent fill when selected or primary.
 *  Buttons only; status is shown with <Mark>. */
export function Pill({ children, onClick, on, primary, title }: Props) {
  return <button className={`pill ${on || primary ? "on" : ""}`} title={title} onClick={onClick}>{children}</button>;
}
