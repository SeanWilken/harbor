import qrcode from "qrcode-generator";

/** A QR code as SVG: dark modules on a light tile, since phone cameras read that most reliably. */
export function Qr({ text, size = 176 }: { text: string; size?: number }) {
  const qr = qrcode(0, "M");
  qr.addData(text);
  qr.make();
  const n = qr.getModuleCount();
  let d = "";
  for (let r = 0; r < n; r++) for (let c = 0; c < n; c++) if (qr.isDark(r, c)) d += `M${c} ${r}h1v1h-1z`;
  return (
    <svg className="qr" width={size} height={size} viewBox={`-2 -2 ${n + 4} ${n + 4}`} role="img" aria-label={`QR code for ${text}`}
      shapeRendering="crispEdges">
      <rect x={-2} y={-2} width={n + 4} height={n + 4} fill="#f4f1e6" />
      <path d={d} fill="#0d1210" />
    </svg>
  );
}
