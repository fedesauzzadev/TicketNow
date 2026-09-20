import { useCallback, useEffect, useState } from 'react';
import { api } from '../api';

interface Stats {
  onsaleId: string;
  waiting: number;
  admitted: number;
  rate: number;
  capacity: number;
}

/** Panel en vivo para el organizador (Fase 6): estado de la fila por onsale. */
export default function OpsPage() {
  const [onsaleId, setOnsaleId] = useState('');
  const [stats, setStats] = useState<Stats | null>(null);
  const [error, setError] = useState('');

  const refresh = useCallback(async () => {
    if (!onsaleId) return;
    try {
      setStats(await api.queueStats(onsaleId));
      setError('');
    } catch (e) {
      setError((e as Error).message);
    }
  }, [onsaleId]);

  useEffect(() => {
    if (!onsaleId) return;
    refresh();
    const timer = setInterval(refresh, 5000);
    return () => clearInterval(timer);
  }, [onsaleId, refresh]);

  return (
    <section>
      <h2>Ops en vivo</h2>
      <p className="muted">Estado de la fila por onsale (auto-refresh 5 s).</p>
      <label>
        Onsale ID (== event ID en Fase 5)
        <input value={onsaleId} onChange={(e) => setOnsaleId(e.target.value)} placeholder="uuid del evento" />
      </label>
      {error && <p className="error">{error}</p>}
      {stats && (
        <table className="zones">
          <tbody>
            <tr>
              <td>En espera</td>
              <td>
                <strong>{stats.waiting}</strong>
              </td>
            </tr>
            <tr>
              <td>Admitidos</td>
              <td>
                <strong>{stats.admitted}</strong>
              </td>
            </tr>
            <tr>
              <td>Tasa actual</td>
              <td>
                <strong>{stats.rate}/s</strong>
              </td>
            </tr>
            <tr>
              <td>Cupo</td>
              <td>
                <strong>{stats.capacity}</strong>
              </td>
            </tr>
          </tbody>
        </table>
      )}
    </section>
  );
}
