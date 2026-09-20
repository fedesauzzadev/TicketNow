import { useState } from 'react';
import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { api } from '../api';
import Countdown from '../components/Countdown';
import { useQueueStore } from '../store/queueStore';
import { useAuthStore } from '../store/authStore';
import { setUserTokenReader } from '../api';

setUserTokenReader(() => useAuthStore.getState().userToken);

type BuyState =
  | { step: 'idle' }
  | { step: 'working'; message: string }
  | { step: 'captcha'; question: string }
  | { step: 'done'; orderId: string; status: string }
  | { step: 'error'; message: string };

export default function EventDetailPage() {
  const { id } = useParams<{ id: string }>();
  const { admissionToken, clear } = useQueueStore();
  const { userId } = useAuthStore();
  const [zoneId, setZoneId] = useState('');
  const [qty, setQty] = useState(2);
  const [captchaAnswer, setCaptchaAnswer] = useState('');
  const [pendingCaptcha, setPendingCaptcha] = useState<{ captchaId: string } | null>(null);
  const [pendingHold, setPendingHold] = useState<{ holdId: string; zoneId: string; price: number } | null>(null);
  const [buy, setBuy] = useState<BuyState>({ step: 'idle' });
  const { data, isLoading, isError } = useQuery({
    queryKey: ['event', id],
    queryFn: () => api.event(id ?? ''),
    enabled: !!id,
  });

  if (isLoading) return <p>Cargando…</p>;
  if (isError || !data) return <p>Evento no encontrado.</p>;

  const open = data.status === 'onsale';
  const needsQueue = open && !!data.onsale?.requiresQueue;

  async function buyTickets(): Promise<void> {
    if (!id) return;
    const zone = data?.zones.find((z) => z.id === zoneId) ?? data?.zones[0];
    if (!zone) {
      setBuy({ step: 'error', message: 'Elegí una zona.' });
      return;
    }
    if (!userId) {
      setBuy({ step: 'error', message: 'Entrá con tu cuenta para comprar.' });
      return;
    }
    try {
      setBuy({ step: 'working', message: 'Reservando…' });
      const hold = await api.createHold({ eventId: id, zoneId: zone.id, qty });
      setBuy({ step: 'working', message: 'Captcha…' });
      const captcha = await api.captcha();
      setPendingHold({ holdId: hold.holdId, zoneId: zone.id, price: zone.price });
      setPendingCaptcha({ captchaId: captcha.captchaId });
      setBuy({ step: 'captcha', question: captcha.question });
    } catch (e) {
      handleBuyError(e as Error);
    }
  }

  async function payWithCaptcha(e: React.FormEvent): Promise<void> {
    e.preventDefault();
    if (!id || !pendingHold || !pendingCaptcha) return;
    try {
      setBuy({ step: 'working', message: 'Procesando pago…' });
      const payment = await api.paymentToken('4242', {
        captchaId: pendingCaptcha.captchaId,
        captchaAnswer,
      });
      const order = await api.createOrder({
        holdId: pendingHold.holdId,
        eventId: id,
        maxPerAccount: data?.onsale?.maxPerAccount ?? 4,
        items: [{ zoneId: pendingHold.zoneId, qty, unitPrice: pendingHold.price }],
        paymentToken: payment.paymentToken,
      });
      setBuy({ step: 'working', message: 'Confirmando…' });
      for (let i = 0; i < 40; i++) {
        await new Promise((r) => setTimeout(r, 1500));
        const status = await api.order(order.orderId);
        if (status.status === 'Confirmed' || status.status === 'Rejected' || status.status === 'Expired') {
          setBuy({ step: 'done', orderId: order.orderId, status: status.status });
          return;
        }
      }
      setBuy({ step: 'error', message: 'La confirmación está demorando; revisá "Mis compras" (pronto).' });
    } catch (err) {
      handleBuyError(err as Error);
    }
  }

  function handleBuyError(e: Error): void {
    const message = e.message;
    if (message.includes('429')) {
      clear();
      setBuy({ step: 'error', message: 'Tu turno expiró: volvé a entrar a la fila.' });
    } else if (message.includes('401')) {
      setBuy({ step: 'error', message: 'Entrá con tu cuenta para comprar.' });
    } else if (message.includes('409') && message.includes('límite')) {
      setBuy({ step: 'error', message: 'Llegaste al límite de entradas para este evento.' });
    } else {
      setBuy({ step: 'error', message });
    }
  }

  return (
    <article>
      <h2>{data.title}</h2>
      <p className="muted">
        {data.artist} · {data.venue}, {data.city}
      </p>
      <p>{new Date(data.startsAt).toLocaleString('es-AR')}</p>
      {data.onsale && data.status === 'announced' && (
        <p>
          La venta abre en <Countdown targetIso={data.onsale.opensAt} />
        </p>
      )}
      {open && needsQueue && !admissionToken && (
        <p>
          <Link to={`/queue/${data.id}`} className="button">
            Entrar a la fila virtual
          </Link>
        </p>
      )}
      {open && needsQueue && admissionToken && (
        <p className="muted">Turno válido: podés comprar.</p>
      )}
      <h3>Zonas</h3>
      <table className="zones">
        <thead>
          <tr>
            <th>Zona</th>
            <th>Capacidad</th>
            <th>Precio</th>
            <th>Disponibilidad</th>
          </tr>
        </thead>
        <tbody>
          {data.zones.map((z) => (
            <tr key={z.id}>
              <td>{z.name}</td>
              <td>{z.capacity}</td>
              <td>${z.price}</td>
              <td>{z.availability}</td>
            </tr>
          ))}
        </tbody>
      </table>
      {open && (!needsQueue || admissionToken) && (
        <section className="buy">
          <h3>Comprar</h3>
          <label>
            Zona
            <select value={zoneId} onChange={(e) => setZoneId(e.target.value)}>
              <option value="">— elegir —</option>
              {data.zones.map((z) => (
                <option key={z.id} value={z.id}>
                  {z.name} — ${z.price}
                </option>
              ))}
            </select>
          </label>
          <label>
            Cantidad
            <input
              type="number"
              min={1}
              max={4}
              value={qty}
              onChange={(e) => setQty(Number(e.target.value))}
            />
          </label>
          <button onClick={buyTickets} disabled={buy.step === 'working' || buy.step === 'captcha'}>
            {buy.step === 'working' ? buy.message : 'Reservar y comprar'}
          </button>
          {!userId && (
            <p className="muted">
              <Link to="/login">Entrá con tu cuenta</Link> para comprar.
            </p>
          )}
          {buy.step === 'captcha' && (
            <form onSubmit={payWithCaptcha} className="buy">
              <p>
                Verificación humana: ¿cuánto es <strong>{buy.question}</strong>?
              </p>
              <label>
                Respuesta
                <input
                  value={captchaAnswer}
                  onChange={(e) => setCaptchaAnswer(e.target.value)}
                  inputMode="numeric"
                  required
                />
              </label>
              <button type="submit">Pagar</button>
            </form>
          )}
          {buy.step === 'done' && (
            <p>
              Orden <code>{buy.orderId}</code>: <strong>{buy.status}</strong>
            </p>
          )}
          {buy.step === 'error' && <p className="error">{buy.message}</p>}
        </section>
      )}
      {!open && <p className="muted">La venta no está abierta para este evento.</p>}
    </article>
  );
}
