// Cliente tipado de la API de TicketNow (contratos: docs/04-datos-y-apis.md §5).

export interface EventListItem {
  id: string;
  title: string;
  artist: string;
  startsAt: string;
  venue: string;
  city: string;
  status: string;
  zonesCount: number;
  priceFrom: number | null;
  onsaleOpensAt: string | null;
}

export interface Zone {
  id: string;
  name: string;
  capacity: number;
  price: number;
  availability: string;
}

export interface OnsaleInfo {
  opensAt: string;
  closesAt: string | null;
  maxPerAccount: number;
  requiresQueue: boolean;
  secondsUntilOpen: number;
}

export interface EventDetail {
  id: string;
  title: string;
  artist: string;
  startsAt: string;
  imageUrl: string | null;
  venue: string;
  city: string;
  status: string;
  onsale: OnsaleInfo | null;
  zones: Zone[];
}

export interface Paged<T> {
  items: T[];
  page: number;
  pageSize: number;
  total: number;
}

export interface Venue {
  id: string;
  name: string;
  city: string;
  capacityTotal: number;
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  // JWT de usuario (Fase 6): el gateway lo exige en rutas de compra y propaga
  // el sub como X-User-Id aguas abajo.
  const user = userToken();
  if (user) {
    headers['Authorization'] = `Bearer ${user}`;
  }
  // Token de admisión (Fase 4): lo exige el gateway en rutas de compra.
  const admission = admissionToken();
  if (admission && needsAdmission(url)) {
    headers['X-Admission-Token'] = admission;
  }
  const res = await fetch(url, {
    headers: { ...headers, ...(init?.headers ?? {}) },
    ...init,
  });
  if (!res.ok) {
    const body = await res.text();
    throw new Error(`HTTP ${res.status}: ${body}`);
  }
  // 204/205 sin cuerpo.
  if (res.status === 204 || res.status === 205) {
    return undefined as T;
  }
  return (await res.json()) as T;
}

function needsAdmission(url: string): boolean {
  return (
    url.startsWith('/api/inventory/holds') ||
    url.startsWith('/api/orders') ||
    url.startsWith('/api/payments') ||
    url.startsWith('/api/tickets')
  );
}

// Los tokens viven en stores de zustand (evita import circular: lectura perezosa).
let tokenReader: () => string | null = () => null;
let userTokenReader: () => string | null = () => null;

export function setAdmissionTokenReader(reader: () => string | null): void {
  tokenReader = reader;
}

export function setUserTokenReader(reader: () => string | null): void {
  userTokenReader = reader;
}

function admissionToken(): string | null {
  try {
    return tokenReader();
  } catch {
    return null;
  }
}

function userToken(): string | null {
  try {
    return userTokenReader();
  } catch {
    return null;
  }
}

export const api = {
  events: (params: { search?: string; city?: string; page?: number } = {}) => {
    const q = new URLSearchParams();
    if (params.search) q.set('search', params.search);
    if (params.city) q.set('city', params.city);
    if (params.page) q.set('page', String(params.page));
    return request<Paged<EventListItem>>(`/api/events?${q.toString()}`);
  },
  event: (id: string) => request<EventDetail>(`/api/events/${id}`),
  venues: () => request<Venue[]>(`/api/admin/venues`),
  createEvent: (payload: unknown) =>
    request<{ id: string }>(`/api/admin/events`, { method: 'POST', body: JSON.stringify(payload) }),
  configureOnsale: (eventId: string, payload: unknown) =>
    request<unknown>(`/api/admin/events/${eventId}/onsale`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  createHold: (payload: { eventId: string; zoneId: string; qty: number }) =>
    request<{ holdId: string; expiresAt: string }>(`/api/inventory/holds`, {
      method: 'POST',
      body: JSON.stringify(payload),
    }),
  captcha: () =>
    request<{ captchaId: string; question: string; expiresAt: string }>(`/api/payments/captcha`),
  paymentToken: (last4 = '4242', captcha?: { captchaId: string; captchaAnswer: string }) =>
    request<{ paymentToken: string }>(`/api/payments/token`, {
      method: 'POST',
      body: JSON.stringify({ last4, ...captcha }),
    }),
  createOrder: (payload: {
    holdId: string;
    eventId: string;
    maxPerAccount: number;
    items: unknown[];
    paymentToken: string;
  }) =>
    request<{ orderId: string; status: string }>(`/api/orders`, {
      method: 'POST',
      headers: { 'Idempotency-Key': crypto.randomUUID() },
      body: JSON.stringify(payload),
    }),
  order: (orderId: string) => request<OrderStatus>(`/api/orders/${orderId}`),
  login: (email: string, name?: string) =>
    request<{ userId: string; displayName: string; token: string; expiresAt: string }>(
      `/api/auth/token`,
      { method: 'POST', body: JSON.stringify({ email, name }) },
    ),
  queueStats: (onsaleId: string) =>
    request<{ onsaleId: string; waiting: number; admitted: number; rate: number; capacity: number }>(
      `/api/queue/admin/${onsaleId}`,
    ),
};

export interface OrderStatus {
  orderId: string;
  status: string;
  tickets: string[];
}
