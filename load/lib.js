import http from 'k6/http';
import { check } from 'k6';

// Helpers compartidos de la suite de carga TicketNow (docs/05 §5.2).
// Sin imports remotos: la corrida debe funcionar offline (profile load de compose).

// Destino: el edge para E1/E2 (se mide el CDN); el gateway directo para la
// tormenta E3/E4 (el rate limit por IP necesita usuarios distintos: cada VU
// lleva su X-Forwarded-For; red de test cerrada — ver load/README.md).
export function target() {
  return __ENV.TARGET || 'http://edge-cache:80';
}

// IP sintética por VU: simula usuarios distintos tras el único contenedor k6.
export function fakeIp() {
  const a = 10;
  const b = 99;
  const c = (__VU % 200) + 1;
  const d = (Math.floor(__VU / 200) % 200) + 1;
  return `${a}.${b}.${c}.${d}`;
}

function req(method, path, body, extraHeaders) {
  const headers = Object.assign({ 'Content-Type': 'application/json' }, extraHeaders || {});
  const url = `${target()}${path}`;
  const payload = body === undefined || body === null ? null : JSON.stringify(body);
  const params = { headers, timeout: '15s' };
  if (method === 'GET') return http.get(url, params);
  if (method === 'POST') return http.post(url, payload, params);
  throw new Error(`método no soportado: ${method}`);
}

export function adminPost(path, body) {
  return req('POST', path, body);
}

export function uniqueKey(prefix) {
  return `${prefix}-${__VU}-${__ITER}-${Date.now()}-${Math.floor(Math.random() * 1e9)}`;
}

// Login mock (Fase 6): un JWT de usuario por VU. Sin esto el gateway responde
// 401 en rutas de compra (policy "purchase" = turno + identidad).
// Lleva XFF propio: si no, todos los logins saldrían de la IP del contenedor.
export function loginAs(user) {
  const res = req(
    'POST',
    '/api/auth/token',
    { email: `${user}@k6.test`, name: user },
    { 'X-Forwarded-For': fakeIp() },
  );
  if (res.status !== 200) {
    throw new Error(`login falló: ${res.status} ${res.body}`);
  }
  return res.json().token;
}

// Captcha mock (Fase 6): resuelve "a + b" y devuelve {captchaId, captchaAnswer}.
export function solveCaptcha() {
  const res = http.get(`${target()}/api/payments/captcha`, { timeout: '10s' });
  if (res.status !== 200) {
    throw new Error(`captcha falló: ${res.status}`);
  }
  const body = res.json();
  const parts = body.question.split('+').map((s) => s.trim());
  return { captchaId: body.captchaId, captchaAnswer: String(Number(parts[0]) + Number(parts[1])) };
}

function spinMs(ms) {
  const until = Date.now() + ms;
  while (Date.now() < until) { /* busy-wait: en setup() no hay sleep() */ }
}

// Sonda de siembra: entra a la fila (la tasa inicial con fila vacía admite en
// ~1 tick), toma el token y prueba un hold por el gateway. 201 => stock listo.
// El hold de sonda se libera para no contaminar el stock del escenario.
function probeSeeded(eventId, zoneId) {
  const userJwt = loginAs('k6-probe');
  const auth = { 'X-User-Id': 'k6-probe', Authorization: `Bearer ${userJwt}` };
  const enter = req('POST', '/api/queue/enter', { onsaleId: eventId }, auth);
  if (enter.status !== 200) return false;
  const entered = enter.json();
  let token = entered.admissionToken || null;
  const dl = Date.now() + 15000;
  while (!token && Date.now() < dl) {
    const me = http.get(`${target()}/api/queue/me?sessionId=${entered.sessionId}`, { timeout: '10s' });
    if (me.status === 200) {
      const snap = me.json();
      if (snap.admitted) {
        token = snap.admissionToken;
        break;
      }
    }
    spinMs(1000);
  }
  if (!token) return false;
  const hold = req(
    'POST',
    '/api/inventory/holds',
    { eventId, zoneId, qty: 1 },
    { ...auth, 'X-Admission-Token': token },
  );
  if (hold.status !== 201) return false;
  const holdId = hold.json().holdId;
  http.del(`${target()}/api/inventory/holds/${holdId}`, null, {
    headers: { ...auth, 'X-Admission-Token': token },
    timeout: '10s',
  });
  return true;
}

// Fixture fresco por corrida vía APIs de admin: venue + evento + zonas +
// onsale ya abierto (opensAt en el pasado → el opener lo publica en ≤5 s,
// inventory siembra y queue habilita). Devuelve { eventId, zones }.
export function createFixture(opts) {
  const o = Object.assign(
    { title: 'k6', zones: [{ name: 'Campo', capacity: 20000, price: 85 }], requiresQueue: true },
    opts || {},
  );

  let res = adminPost('/api/admin/venues', {
    name: `Estadio k6 ${Date.now()}`,
    city: 'k6',
    capacityTotal: o.zones.reduce((a, z) => a + z.capacity, 0),
  });
  check(res, { 'venue creado': (r) => r.status === 201 });
  const venueId = res.json().id;

  res = adminPost('/api/admin/events', {
    title: `${o.title} ${Date.now()}`,
    artist: 'k6',
    venueId,
    startsAt: new Date(Date.now() + 90 * 24 * 3600 * 1000).toISOString(),
    zones: o.zones,
  });
  check(res, { 'evento creado': (r) => r.status === 201 });
  const eventId = res.json().id;

  res = adminPost(`/api/admin/events/${eventId}/onsale`, {
    opensAt: new Date(Date.now() - 60 * 1000).toISOString(),
    admissionRatePerSec: o.admissionRatePerSec || 50,
    maxConcurrentInside: o.maxConcurrentInside || 2000,
    maxPerAccount: 4,
    requiresQueue: o.requiresQueue,
  });
  check(res, { 'onsale configurado': (r) => r.status === 200 });

  let zones = [];
  const deadline = Date.now() + 90000;
  while (Date.now() < deadline) {
    const detail = http.get(`${target()}/api/events/${eventId}`, { timeout: '10s' });
    if (detail.status === 200) {
      zones = (detail.json().zones || []).map((z) => ({ id: z.id, capacity: z.capacity }));
      if (zones.length > 0 && probeSeeded(eventId, zones[0].id)) {
        break;
      }
    }
    zones = [];
    spinMs(1000);
  }
  if (zones.length === 0) {
    throw new Error('el fixture no quedó sembrado en 90 s (¿opener/inventory/queue caídos?)');
  }
  return { eventId, zones };
}
