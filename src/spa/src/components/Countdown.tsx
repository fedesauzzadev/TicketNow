import { useEffect, useState } from 'react';

function format(totalSeconds: number): string {
  const s = Math.max(0, totalSeconds);
  const days = Math.floor(s / 86400);
  const hours = Math.floor((s % 86400) / 3600);
  const minutes = Math.floor((s % 3600) / 60);
  const rest = s % 60;
  if (days > 0) return `${days}d ${hours}h ${minutes}m`;
  if (hours > 0) return `${hours}h ${minutes}m ${rest}s`;
  return `${minutes}m ${rest}s`;
}

/** Cuenta regresiva hasta la apertura del onsale (FR-12). */
export default function Countdown({ targetIso }: { targetIso: string }) {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);

  const diff = Math.max(0, Math.floor((new Date(targetIso).getTime() - now) / 1000));
  return <span className="countdown">{diff === 0 ? '¡Abierto!' : format(diff)}</span>;
}
