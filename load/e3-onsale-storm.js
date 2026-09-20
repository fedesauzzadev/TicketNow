import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter, Rate, Trend } from 'k6/metrics';
import { target, createFixture, uniqueKey, fakeIp, loginAs, solveCaptcha } from './lib.js';

// E3 Onsale storm (docs/01 §7, docs/05 §5.2): la puerta se abre y la tormenta
// entra por la fila. Flujo por VU: enter → poll /me (+heartbeat) → hold →
// pago → orden → poll estado terminal. 10% de tarjetas con rechazo forzado
// (last4 0002) para ejercitar la compensación de la saga.
//
// Va DIRECTO al gateway (no al edge): el rate limit por IP necesita usuarios
// distintos y cada VU lleva su X-Forwarded-For (red de test cerrada).
// Uso (escala real): ... -e K6_SCRIPT=e3-onsale-storm.js -e TARGET=http://gateway:8080 -e VUS=5000 -e STORM=10m k6
// Uso (humo local):  k6 run -e TARGET=http://localhost:<puerto-gateway> load/e3-onsale-storm.js

const timeToAdmit = new Trend('time_to_admit', true);
const checkoutOk = new Counter('checkout_ok');
const checkoutRejected = new Counter('checkout_rejected');
// Errores REALES (5xx, timeouts, 4xx inesperados). Los 409 (soldout) y 404
// (poll temprano, la orden nace async) son protocolo, no errores: k6 los
// cuenta en http_req_failed igual, por eso se miden acá aparte.
const stormErrors = new Rate('storm_errors');

function noteUnexpected(res, okStatuses) {
  if (!okStatuses.includes(res.status)) {
    stormErrors.add(1);
  }
}

export const options = {
  scenarios: {
    storm: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '30s', target: 50 }, // rumor de apertura
        { duration: '60s', target: Number(__ENV.VUS || 20) }, // la puerta se abre
        { duration: __ENV.STORM || '1m', target: Number(__ENV.VUS || 20) }, // tormenta sostenida
        { duration: '1m', target: 0 }, // la cola drena
      ],
    },
  },
  thresholds: {
    storm_errors: ['rate<0.02'],
    time_to_admit: [`p(95)<${Number(__ENV.ADMIT_P95_MS || 120000)}`],
    checkout_ok: ['count>0'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const fx = createFixture({
    title: 'Storm',
    admissionRatePerSec: Number(__ENV.ADMIT_RATE || 50),
    maxConcurrentInside: Number(__ENV.MAX_INSIDE || 2000),
    zones: [{ name: 'Campo', capacity: Number(__ENV.CAPACITY || 20000), price: 85 }],
    requiresQueue: true,
  });
  console.log(`fixture storm: event=${fx.eventId} zone=${fx.zones[0].id}`);
  return fx;
}

function headers(user, token, userJwt) {
  return {
    'Content-Type': 'application/json',
    'X-User-Id': user,
    'X-Forwarded-For': fakeIp(),
    ...(userJwt ? { Authorization: `Bearer ${userJwt}` } : {}),
    ...(token ? { 'X-Admission-Token': token } : {}),
  };
}

export default function (data) {
  const user = `storm-${__VU}-${__ITER}`;
  const zoneId = data.zones[0].id;
  const T = target();
  const userJwt = loginAs(user);

  // 1) Entrar a la fila.
  let res = http.post(`${T}/api/queue/enter`, JSON.stringify({ onsaleId: data.eventId }), {
    headers: headers(user, null, userJwt),
    timeout: '15s',
    tags: { name: 'queue-enter' },
  });
  noteUnexpected(res, [200]);
  if (!check(res, { 'enter 200': (r) => r.status === 200 })) return;
  let entered = res.json();
  const t0 = Date.now();
  const sid = entered.sessionId;
  let token = entered.admissionToken || null;
  let beats = 0;

  // 2) Esperar admisión (poll /me + heartbeat cada ~30 s).
  const admitDeadline = Date.now() + Number(__ENV.ADMIT_TIMEOUT_MS || 600000);
  while (!token && Date.now() < admitDeadline) {
    sleep(2);
    beats += 1;
    if (beats % 15 === 0) {
      const hb = http.post(`${T}/api/queue/heartbeat`, JSON.stringify({ sessionId: sid }), {
        headers: headers(user, null, userJwt),
        timeout: '10s',
        tags: { name: 'queue-hb' },
      });
      noteUnexpected(hb, [200, 404]);
    }
    res = http.get(`${T}/api/queue/me?sessionId=${sid}`, { timeout: '10s', tags: { name: 'queue-me' } });
    noteUnexpected(res, [200, 404]);
    if (res.status === 200 && res.json().admitted) {
      token = res.json().admissionToken;
      break;
    }
  }
  if (!token) {
    stormErrors.add(1);
    return; // fuera del presupuesto de espera: el VU muere en la fila (también es dato)
  }
  timeToAdmit.add(Date.now() - t0);

  // 3) Hold.
  res = http.post(
    `${T}/api/inventory/holds`,
    JSON.stringify({ eventId: data.eventId, zoneId, qty: 1 }),
    { headers: headers(user, token, userJwt), timeout: '15s', tags: { name: 'hold' } },
  );
  if (res.status === 409) return; // sin stock: tormenta realista, el VU termina acá
  noteUnexpected(res, [201]);
  if (!check(res, { 'hold 201': (r) => r.status === 201 })) return;
  const holdId = res.json().holdId;

  // 4) Pago (10% rechazo forzado) + orden. Captcha mock: se resuelve "a + b".
  const decline = Math.random() < 0.1;
  const captcha = solveCaptcha();
  res = http.post(
    `${T}/api/payments/token`,
    JSON.stringify({ last4: decline ? '0002' : '4242', ...captcha }),
    {
      headers: headers(user, token, userJwt),
      timeout: '15s',
      tags: { name: 'pay-token' },
    },
  );
  noteUnexpected(res, [200]);
  if (!check(res, { 'token 200': (r) => r.status === 200 })) return;
  const paymentToken = res.json().paymentToken;

  res = http.post(
    `${T}/api/orders`,
    JSON.stringify({
      holdId,
      eventId: data.eventId,
      maxPerAccount: 4,
      items: [{ zoneId, qty: 1, unitPrice: 85 }],
      paymentToken,
    }),
    {
      headers: Object.assign(headers(user, token, userJwt), { 'Idempotency-Key': uniqueKey('storm') }),
      timeout: '15s',
      tags: { name: 'order' },
    },
  );
  noteUnexpected(res, [202]);
  if (!check(res, { 'orden aceptada': (r) => r.status === 202 })) return;
  const orderId = res.json().orderId;

  // 5) Poll hasta estado terminal (404 temprano = la orden nace async: esperado).
  const orderDeadline = Date.now() + 120000;
  while (Date.now() < orderDeadline) {
    sleep(2);
    res = http.get(`${T}/api/orders/${orderId}`, {
      headers: headers(user, token, userJwt),
      timeout: '10s',
      tags: { name: 'order-poll' },
    });
    noteUnexpected(res, [200, 404]);
    if (res.status === 200) {
      const st = res.json().status;
      if (st === 'Confirmed') {
        checkoutOk.add(1);
        return;
      }
      if (st === 'Rejected' || st === 'Expired') {
        checkoutRejected.add(1);
        return;
      }
    }
  }
  stormErrors.add(1); // la orden nunca llegó a estado terminal
}
