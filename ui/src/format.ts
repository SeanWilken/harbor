export function ago(iso: string | undefined, now = Date.now()): string {
  if (!iso) return "—";
  const s = Math.round((now - Date.parse(iso)) / 1000);
  if (s < 60) return `${s}s`;
  if (s < 3600) return `${Math.round(s / 60)}m`;
  if (s < 86400) return `${Math.round(s / 3600)}h`;
  if (s < 86400 * 60) return `${Math.round(s / 86400)}d`;
  if (s < 86400 * 730) return `${Math.round(s / (86400 * 30))}mo`;
  return `${Math.round(s / (86400 * 365))}y`;
}

/** "git@github.com:me/x.git" or "https://github.com/me/x" -> "github.com" */
export function remoteHost(url: string): string {
  const m = url.match(/^[\w+.-]+:\/\/(?:[^@/]+@)?([^/:]+)/) ?? url.match(/^(?:[^@]+@)?([^:/]+):/);
  return m?.[1] ?? (url.startsWith("/") ? "local path" : url);
}

export const shortPath = (p: string) => p.replace(/^\/home\/[^/]+/, "~");

export const plural = (n: number, word: string) => `${n} ${word}${n === 1 ? "" : "s"}`;

/** "github · SeanWilken" from the origin remote (or the first one). */
export function owner(remotes: { name: string; url: string }[]): string | undefined {
  const url = (remotes.find((x) => x.name === "origin") ?? remotes[0])?.url;
  if (!url) return undefined;
  const host = remoteHost(url);
  if (host === "local path") return host;
  const path = url.replace(/^[\w+.-]+:\/\/[^/]+\//, "").replace(/^[^@/]+@[^:/]+:/, "");
  const org = path.split("/").filter(Boolean).at(-2);
  const site = host.replace(/^www\./, "").replace(/\.(com|org|net|io)$/, "");
  return org ? `${site} · ${org}` : site;
}

/** 1536 -> "1.5K", in powers of 1024. */
export function bytes(n: number): string {
  const units = ["B", "K", "M", "G", "T"];
  let i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n >= 10 || i === 0 ? Math.round(n) : n.toFixed(1)}${units[i]}`;
}

/** 93784 -> "1d 2h", 4000 -> "1h 6m". */
export function duration(seconds: number): string {
  const d = Math.floor(seconds / 86400), h = Math.floor((seconds % 86400) / 3600), m = Math.floor((seconds % 3600) / 60);
  return d > 0 ? `${d}d ${h}h` : h > 0 ? `${h}h ${m}m` : `${m}m`;
}
