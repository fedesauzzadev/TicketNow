# 01 · Visión y requisitos

TicketNow · SDD · v1.0

---

## 1. Visión

> "Cualquier fan, en el segundo cero de un onsale de un estadio, tiene una experiencia justa, transparente y sin caídas — y el sistema vende hasta la última entrada sin vender ninguna dos veces."

## 2. Problema

La venta de entradas para eventos de alta demanda tiene tres características que la hacen un caso extremo de ingeniería:

1. **Pico absoluto:** la demanda es ~0 antes del onsale y ~100% del total en los primeros minutos. No hay "carga promedio" que dimensionar.
2. **Recurso finito y compartido:** el stock es un contador único que todos quieren modificar al mismo tiempo (contención).
3. **Dinero:** cada error de consistencia es un costo real (sobreventa, cobros sin entrada, entradas sin cobro).

## 3. Personas (stakeholders)

| Persona | Descripción | Necesidades clave |
|---|---|---|
| **Fan (comprador)** | Usuario anónimo o registrado que quiere entradas | Entrar rápido, saber su posición real, no perder el turno, pagar sin errores, recibir su entrada |
| **Organizador** | Crea eventos y configura onsales (staff interno) | Definir zonas/precios/aforo, hora de inicio, tasa de admisión, límites por cuenta; ver métricas en vivo |
| **Staff de puerta** | Valida entradas en el evento | Verificar QR rápido, online y offline con margen |
| **Operador/SRE** | Mantiene el sistema durante el onsale | Dashboards, alertas, escalar réplicas, ver traces, abortar/recuperar |
| **Bot/atacante** (persona no grata) | Script que intenta comprar en masa | Debe encontrar: fila con sesión, límites por cuenta, captcha, pruebas de humanidad, rate limits |

## 4. Alcance

### Dentro de alcance

- Catálogo de eventos con zonas, precios y disponibilidad aproximada.
- Venta numerada por zona (cantidades por zona), no selección de butaca individual (v1).
- Fila virtual para onsales de alta demanda; venta directa para eventos de baja demanda.
- Checkout con reserva temporal (hold), pago mock tokenizado y emisión de entrada con QR firmado.
- Verificación de entradas (staff) con anti-replay.
- Panel de organizador: CRUD de eventos, configuración de onsale, monitor en vivo.
- Notificaciones (email mock + in-app) de confirmación y de expiración de reserva.
- Observabilidad completa, pruebas de carga y de caos.

### Fuera de alcance (v1)

- Selección de butaca individual por asiento (mapa).
- Pagos reales / PCI real (provider mock; Stripe test como stretch goal).
- Reventa, transferencias, devoluciones self-service (solo compensaciones automáticas del sistema).
- Multi-idioma, multi-moneda, accesibilidad avanzada (documentado como deuda).

## 5. Supuestos y restricciones

- Todo corre en Docker Compose sobre una máquina de desarrollo (16 GB RAM recomendado); las cifras de NFR están escaladas a ese entorno.
- Un solo PostgreSQL con **un schema por servicio** (compromiso didáctico; en producción, DB por servicio — ver ADR-002).
- El proveedor de pagos es un mock interno con latencia configurable (1–3 s) y tasa de rechazo configurable (para probar compensaciones).
- La hora de los onsale se configura en UTC; el cliente muestra en zona local.

## 6. Requisitos funcionales

### 6.1 Cuenta y sesión

| ID | Requisito |
|---|---|
| FR-01 | Registrarse/iniciar sesión con email + contraseña (JWT access + refresh) |
| FR-02 | Ver "Mis compras" con estado de cada orden y descargar la entrada |

### 6.2 Catálogo

| ID | Requisito |
|---|---|
| FR-10 | Listar eventos con búsqueda por nombre, ciudad y género, y paginado |
| FR-11 | Ver detalle: fecha, venue, zonas, precios y **disponibilidad aproximada** (indicador, no número exacto) |
| FR-12 | Si el evento tiene onsale no iniciado, mostrar countdown; si está en curso y saturado, derivar a la fila (FR-20) |

### 6.3 Fila virtual (onsale)

| ID | Requisito |
|---|---|
| FR-20 | Entrar a la fila y recibir posición y estimación de espera, actualizadas en tiempo real (SignalR) |
| FR-21 | Al ser admitido, recibir un admission token firmado de vida corta; sin él no se accede a compra |
| FR-22 | Mantener el lugar en fila ante reconexión breve (gracia de 60 s con heartbeat) |
| FR-23 | Si el hold expira o el usuario abandona, liberar el cupo y admitir al siguiente |

### 6.4 Compra / checkout

| ID | Requisito |
|---|---|
| FR-30 | Seleccionar zona y cantidad (1 a 4) y obtener una reserva (hold) con TTL visible (cuenta regresiva) |
| FR-31 | Completar checkout dentro del TTL: datos de pago tokenizado, idempotente ante reintentos |
| FR-32 | Si el pago es rechazado o el hold expira, liberar stock y avisar al usuario |
| FR-33 | Límite configurable por cuenta y por evento (default: 4 entradas) |

### 6.5 Pagos y entradas

| ID | Requisito |
|---|---|
| FR-40 | Autorizar y capturar pago vía provider mock (token, nunca datos de tarjeta) |
| FR-41 | Emitir entrada con QR firmado (JWT) por orden confirmada |
| FR-42 | Staff verifica QR: válido / ya usado / inválido, con margen offline de ±5 min |

### 6.6 Organizador / admin

| ID | Requisito |
|---|---|
| FR-50 | CRUD de eventos, venues, zonas (nombre, capacidad, precio) |
| FR-51 | Configurar onsale: hora inicio, capacidad simultánea dentro, tasa de admisión inicial, límites por cuenta |
| FR-52 | Monitor en vivo: longitud de fila, tasa de admisión, holds activos, ventas por zona, latencias |

### 6.7 Notificaciones

| ID | Requisito |
|---|---|
| FR-60 | Email mock + notificación in-app al confirmar orden (con QR) y al expirar un hold |

## 7. Requisitos no funcionales (medibles)

| ID | Categoría | Objetivo | Cómo se mide |
|---|---|---|---|
| NFR-01 | Performance | Catálogo: p95 < 150 ms (cache hit p95 < 20 ms) | k6 + Prometheus |
| NFR-02 | Performance | `POST /holds`: p99 < 50 ms | k6 |
| NFR-03 | Performance | Fila: push de posición < 1 s tras cambio | Test SignalR |
| NFR-04 | Performance | Checkout submit→confirm p95 < 10 s (dominado por el pago mock) | Trace e2e |
| NFR-05 | Escalabilidad | Onsale didáctico: 5.000 usuarios en fila, 2.000 concurrentes dentro, admisión 50/s | k6 storm |
| NFR-06 | Escalabilidad | 1.000 RPS mixto 80/20 lectura/escritura sostenido 10 min | k6 |
| NFR-07 | Consistencia | INV-1 zero double-sell: 0 casos | Reconciliador + pruebas |
| NFR-08 | Disponibilidad | 99,9% durante ventana de onsale; degradación elegante (fila comunica, no 500) | Chaos + monitoreo |
| NFR-09 | Resiliencia | Caída de una réplica de inventory/orders sin pérdida de órdenes ni invariantes | Chaos |
| NFR-10 | Observabilidad | 100% de requests con trace; RED metrics por servicio | OTel + Grafana |
| NFR-11 | Seguridad | Sin secretos en repo; rate limits activos; QR anti-replay | Auditoría fase 6 |
| NFR-12 | Operabilidad | Levantar todo con un comando; escalar réplicas sin downtime | Runbook fase 0 |

## 8. Historias de usuario clave (con criterios de aceptación)

### HU-01 · El fan entra al onsale

> **Como** fan, **quiero** entrar a la venta apenas se habilita, **para** tener chance de conseguir entradas.

- **Dado** que el onsale abre a las 18:00 UTC y entro a las 18:00:01 con otras 4.999 personas,
  **cuando** el sistema me ubica en fila,
  **entonces** recibo una posición y una estimación, y **nadie que haya llegado después entra antes que yo** (INV-4).
- **Dado** que estoy en fila con pestaña cerrada accidentalmente,
  **cuando** reconecto en menos de 60 s,
  **entonces** conservo mi posición.

### HU-02 · El fan reserva y compra

> **Como** fan admitido, **quiero** reservar entradas mientras pago, **para** que no me las quiten a mitad del checkout.

- **Dado** que selecciono 2 entradas de la Zone A (quedan 5),
  **cuando** solicito el hold,
  **entonces** el sistema me reserva 2 por 8 minutos con cuenta regresiva visible y la disponibilidad cae inmediatamente.
- **Dado** que quedaba exactamente 1 entrada y pido 2,
  **cuando** solicito el hold, **entonces** falla limpio (409) sin decrementar nada.
- **Dado** que mi pago es rechazado,
  **cuando** la saga compensa, **entonces** el stock vuelve y entro a la fila en la posición más cercana al frente (política de gracia).

### HU-03 · El sistema no se vende de más aunque todo arda

> **Como** operador, **quiero** garantía de no sobreventa, **para** no tener que cancelar compras reales.

- **Dado** 500 clientes simultáneos pidiendo las últimas 10 entradas,
  **cuando** se ejecutan los holds,
  **entonces** exactamente 10 tienen éxito, 490 fallan, y el reconciliador reporta 0 divergencias.
- **Dado** que se cae una réplica de inventory con holds activos,
  **cuando** se recupera,
  **entonces** ningún hold se perdió ni se vendió de más (ledger + sweeper).

### HU-04 · El organizador lanza un onsale controlado

> **Como** organizador, **quiero** configurar y monitorear la venta, **para** dormir tranquilo.

- **Dado** un estadio de 60.000 en 5 zonas, **cuando** configuro el onsale (18:00 UTC, 2.000 dentro, 50/s),
  **entonces** a las 18:00 la fila abre sola y el monitor muestra fila/admisión/ventas en vivo.
- **Dado** saturación (p95 > objetivo), **cuando** el backpressure actúa,
  **entonces** la tasa de admisión baja sola y el dashboard lo refleja.

## 9. Escenarios de carga (perfiles de prueba)

| Escenario | Descripción | Perfil |
|---|---|---|
| **E1 Navegación normal** | Día cualquiera, gente mirando eventos | 100 RPS sostenido, 95% lectura catálogo |
| **E2 Pre-onsale** | Página de countdown, F5 frenético | 500 usuarios refrescando cada 5 s; todo cacheado |
| **E3 Onsale storm** (el importante) | La puerta se abre | Rampa 0 → 5.000 usuarios virtuales en 60 s, sostener 10 min, decaimiento. Dentro: 2.000 concurrentes, admisión 50/s, holds 20/s, pagos con rechazos 10% y timeout 5% |
| **E4 Flash-drain** | Se agota una zona en segundos | Contención extrema sobre un contador: 4.999 peticiones contra 10 unidades |

## 10. Dependencias entre requisitos

- FR-30/31 (holds + checkout) dependen de FR-21 (admission token) en eventos con fila.
- FR-41 (QR) depende de FR-40 y de la saga (INV-3).
- Los NFR-05..09 se validan en fases 4–5 (ver roadmap en [SDD.md](SDD.md#7-roadmap)).
