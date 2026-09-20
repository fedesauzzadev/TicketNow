import http from 'k6/http';
import { check, sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { target, createFixture, fakeIp, loginAs } from './lib.js';

// E4 Flash-drain (docs/01 §7): contención extrema sobre un contador — N
// peticiones contra una zona de 10 unidades. Invariante: EXACTAMENTE 10
// holds otorgados (ni uno más: INV-1) y el resto 409.
// Va directo al gateway con XFF por VU (igual que E3).
// Uso: ... -e K6_SCRIPT=e4-flash-drain.js -e TARGET=http://gateway:8080 -e REQUESTS=4999 -e CAPACITY=10 k6

const granted = new Counter('holds_granted');

export const options = {
  scenarios: {
    drain: {
      executor: 'shared-iterations',
      vus: Number(__ENV.VUS || 200),
      iterations: Number(__ENV.REQUESTS || 4999),
      maxDuration: __ENV.DURATION || '3m',
    },
  },
  thresholds: {
    // 409 (soldout) es protocolo, no error: k6 lo cuenta en http_req_failed
    // igual, por eso el invariante se mide en checks + holds_granted.
    checks: ['rate>0.99'],
    holds_granted: [`count==${Number(__ENV.CAPACITY || 10)}`],
  },
};

export function setup() {
  const fx = createFixture({
    title: 'Drain',
    zones: [{ name: 'Pulguero', capacity: Number(__ENV.CAPACITY || 10), price: 50 }],
    requiresQueue: true,
  });
  console.log(`fixture drain: event=${fx.eventId} zone=${fx.zones[0].id}`);
  return fx;
}

function admitOnce(user, eventId) {
  const T = target();
  const base = {
    'Content-Type': 'application/json',
    'X-User-Id': user,
    'X-Forwarded-For': fakeIp(),
  };
  let res = http.post(`${T}/api/queue/enter`, JSON.stringify({ onsaleId: eventId }), {
    headers: base,
    timeout: '15s',
  });
  if (res.status !== 200) return null;
  const entered = res.json();
  if (entered.admitted) return entered.admissionToken;
  const dl = Date.now() + 60000;
  while (Date.now() < dl) {
    sleep(1); // poll cortés como un humano (sin esto se funde el rate limit)
    res = http.get(`${T}/api/queue/me?sessionId=${entered.sessionId}`, { timeout: '10s' });
    if (res.status === 200 && res.json().admitted) return res.json().admissionToken;
  }
  return null;
}

export default function (data) {
  const user = `drain-${__VU}-${__ITER}`;
  const userJwt = loginAs(user);
  // La tormenta pasa por la fila (tasa alta en el fixture): a lo que importa
  // es la contención en el contador, no saltarse la puerta.
  const token = admitOnce(user, data.eventId);
  if (!token) return;

  const res = http.post(
    `${target()}/api/inventory/holds`,
    JSON.stringify({ eventId: data.eventId, zoneId: data.zones[0].id, qty: 1 }),
    {
      headers: {
        'Content-Type': 'application/json',
        'X-User-Id': user,
        'X-Forwarded-For': fakeIp(),
        Authorization: `Bearer ${userJwt}`,
        'X-Admission-Token': token,
      },
      timeout: '15s',
    },
  );
  check(res, { 'respuesta válida': (r) => r.status === 201 || r.status === 409 });
  if (res.status === 201) granted.add(1);
}
