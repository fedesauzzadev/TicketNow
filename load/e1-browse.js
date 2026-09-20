import http from 'k6/http';
import { check, sleep } from 'k6';
import { target } from './lib.js';

// E1 Navegación normal (docs/01 §7): 100 RPS sostenido, 95% lectura de catálogo.
// Todo pasa por el edge-cache: el primer MISS calienta, el resto debe ser HIT.
// Uso: k6 run load/e1-browse.js   (TARGET=http://localhost:8090 en host)

export const options = {
  scenarios: {
    browse: {
      executor: 'constant-arrival-rate',
      rate: Number(__ENV.RPS || 100),
      timeUnit: '1s',
      duration: __ENV.DURATION || '2m',
      preAllocatedVUs: 50,
      maxVUs: 200,
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.01'],
    http_req_duration: ['p(95)<800'],
    checks: ['rate>0.99'],
  },
};

export function setup() {
  const res = http.get(`${target()}/api/events?page=1`, { timeout: '10s' });
  check(res, { 'catálogo responde': (r) => r.status === 200 });
  const items = res.json().items || [];
  if (items.length === 0) throw new Error('sin eventos para navegar (¿seed corrido?)');
  return { ids: items.map((i) => i.id) };
}

export default function (data) {
  const id = data.ids[Math.floor(Math.random() * data.ids.length)];

  let res = http.get(`${target()}/api/events?page=1`, { timeout: '10s', tags: { name: 'list' } });
  check(res, { 'listado 200': (r) => r.status === 200 });

  res = http.get(`${target()}/api/events/${id}`, { timeout: '10s', tags: { name: 'detail' } });
  check(res, { 'detalle 200': (r) => r.status === 200 });

  sleep(0.5);
}
