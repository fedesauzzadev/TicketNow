import { useCallback, useEffect, useRef, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import * as signalR from '@microsoft/signalr';
import { useQueueStore } from '../store/queueStore';

interface AdmittedPayload {
  admissionToken: string;
  expiresAt: string;
}

interface PositionPayload {
  position: number;
  etaSeconds: number;
  aheadCount: number;
}

interface EnteredDto {
  sessionId: string;
  position: number;
  etaSeconds: number;
  admitted: boolean;
  admissionToken?: string;
}

/** Resuelve el proof-of-work del servidor (Fase 6): SHA-256 hex con N ceros
 *  al inicio. Por chunks para no congelar la pestaña. */
async function solvePow(challengeId: string, difficulty: number): Promise<string> {
  const subtle = crypto.subtle;
  let nonce = 0;
  for (;;) {
    for (let i = 0; i < 2000; i++) {
      const text = `${challengeId}:${nonce}`;
      const digest = await subtle.digest('SHA-256', new TextEncoder().encode(text));
      const bytes = new Uint8Array(digest);
      let ok = true;
      let bits = difficulty;
      for (let b = 0; b < bytes.length && bits > 0; b++) {
        const want = Math.min(8, bits);
        const mask = 0xff << (8 - want);
        if ((bytes[b] & mask) !== 0) {
          ok = false;
          break;
        }
        bits -= want;
      }
      if (ok) return String(nonce);
      nonce++;
    }
    await new Promise((r) => setTimeout(r, 0));
  }
}

/** Sala de espera virtual (docs/03 §1): posición en vivo y admisión por push. */
export default function QueuePage() {
  const { onsaleId = '' } = useParams<{ onsaleId: string }>();
  const navigate = useNavigate();
  const { sessionId, position, etaSeconds, admitted, setQueue } = useQueueStore();
  const [error, setError] = useState('');
  const [connecting, setConnecting] = useState(true);
  const connectionRef = useRef<signalR.HubConnection | null>(null);

  const setupRealtime = useCallback(
    async (entered: EnteredDto) => {
      if (entered.admitted && entered.admissionToken) {
        setQueue({
          sessionId: entered.sessionId,
          onsaleId,
          admissionToken: entered.admissionToken,
          admitted: true,
        });
        navigate(`/events/${onsaleId}`);
        return;
      }
      setQueue({
        sessionId: entered.sessionId,
        onsaleId,
        position: entered.position,
        etaSeconds: entered.etaSeconds,
        admitted: false,
      });

      const connection = new signalR.HubConnectionBuilder()
        .withUrl('/hubs/queue')
        .withAutomaticReconnect()
        .build();

      connection.on('OnPositionChanged', (payload: PositionPayload) => {
        setQueue({ position: payload.position, etaSeconds: payload.etaSeconds });
      });
      connection.on('OnAdmitted', (payload: AdmittedPayload) => {
        setQueue({ admissionToken: payload.admissionToken, admitted: true });
        navigate(`/events/${onsaleId}`);
      });
      connection.onclose(() => setError('Conexión perdida. Reintentando…'));

      await connection.start();
      connectionRef.current = connection;
      await connection.invoke('JoinQueue', onsaleId, entered.sessionId);
      setConnecting(false);

      const timer = setInterval(() => {
        connection.invoke('Heartbeat', entered.sessionId).catch(() => undefined);
      }, 30000);
      (connection as unknown as { __timer: unknown }).__timer = timer;
    },
    [onsaleId, navigate, setQueue],
  );

  const enter = useCallback(async () => {
    setError('');
    setConnecting(true);
    try {
      // Entrar por REST (crea sesión, funciona sin websockets).
      // Con PoW exigido (Fase 6) el primer intento devuelve 428 + challenge:
      // se resuelve en el cliente y se reintenta con la prueba.
      let payload: { onsaleId: string; challengeId?: string; nonce?: string } = { onsaleId };
      for (let attempt = 0; attempt < 2; attempt++) {
        const res = await fetch('/api/queue/enter', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify(payload),
        });
        if (res.status === 428) {
          const challenge: { challengeId: string; difficulty: number } = await res.json();
          setError(`Verificación anti-bot (${challenge.difficulty} bits)…`);
          const nonce = await solvePow(challenge.challengeId, challenge.difficulty);
          payload = { onsaleId, challengeId: challenge.challengeId, nonce };
          continue;
        }
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        setError('');
        await setupRealtime(await res.json());
        return;
      }
      throw new Error('no se pudo completar la prueba anti-bot');
    } catch (e) {
      setError(`No se pudo entrar a la fila: ${(e as Error).message}`);
      setConnecting(false);
    }
  }, [onsaleId, setupRealtime]);

  useEffect(() => {
    enter();
    return () => {
      const connection = connectionRef.current;
      if (connection) {
        const timer = (connection as unknown as { __timer?: number }).__timer;
        if (timer) clearInterval(timer);
        const sid = useQueueStore.getState().sessionId;
        if (sid) connection.invoke('LeaveQueue', sid).catch(() => undefined);
        connection.stop().catch(() => undefined);
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [onsaleId]);

  if (admitted) {
    return (
      <section>
        <h2>¡Admitido!</h2>
        <p>Redirigiendo a la compra…</p>
      </section>
    );
  }

  return (
    <section>
      <h2>Sala de espera</h2>
      {error && <p className="error">{error}</p>}
      {connecting || position == null ? (
        <p>Conectando…</p>
      ) : (
        <>
          <p className="position">
            Puesto <strong>#{position}</strong>
          </p>
          <p className="muted">
            Tiempo estimado: {etaSeconds != null && etaSeconds > 0 ? `${Math.ceil(etaSeconds / 60)} min` : 'menos de un minuto'}
          </p>
          <p className="muted">No cierres esta pestaña: tu lugar se conserva 60 s ante reconexiones.</p>
          {sessionId && <p className="muted">Sesión: {sessionId.slice(0, 8)}…</p>}
        </>
      )}
    </section>
  );
}
