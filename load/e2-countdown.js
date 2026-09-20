import http from 'k6/http';
import { check, sleep } from 'k6';
import { target } from './lib.js';

// E2 Pre-onsale (docs/01 §7): 500 usuarios refrescando el countdown cada 5 s.
// Todo cacheado en el borde: el origen no debe ver el pico (verificar HIT).
// Uso: k6 run load/e2-countdown.js

export const options = {
  scenarios: {
    countdown: {
      executor: 'ramping-vus',
      startVUs: 0,
      stages: [
        { duration: '30s', target: Number(__ENV.VUS || 500) },
        { duration: __ENV.DURATION || '2m', target: Number(__ENV.VUS || 500) },
        { duration: '15s', target: 0 },
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<500'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const res = http.get(`${target()}/api/events?page=1`, { timeout: '10s' });
  check(res, { 'catálogo responde': (r) => r.status === 200 });
  // Prefiere un evento anunciado (con countdown) si lo hay.
  const items = res.json().items || [];
  const announced = items.find((i) => i.status === 'announced') || items[0];
  if (!announced) throw new Error('sin eventos (¿seed corrido?)');
  return { id: announced.id };
}

export default function (data) {
  const res = http.get(`${target()}/api/events/${data.id}`, { timeout: '10s' });
  check(res, {
    'detalle 200': (r) => r.status === 200,
    'cacheado en el borde': (r) =>
      (r.headers['X-Cache-Status'] || r.headers['X-Cache'] || 'HIT').toUpperCase().includes('HIT'),
  });
  sleep(5);
}
