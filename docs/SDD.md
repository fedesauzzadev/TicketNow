# SDD — Software Design Document · TicketNow

| Campo | Valor |
|---|---|
| Producto | TicketNow — venta de entradas para eventos y recitales |
| Versión del documento | 1.0 |
| Fecha | 2026-09-18 |
| Estado | Borrador aprobado para Fase 0–1 |
| Tipo de proyecto | Didáctico con criterios de producción |
| Stack | .NET 8 · React + TypeScript · PostgreSQL · Redis · RabbitMQ · Docker |

---

## 1. Resumen ejecutivo

El 15 de noviembre de 2022, la preventa del tour de Taylor Swift (Eras Tour) colapsó Ticketmaster: 3.500 millones de requests en un día, 14 millones de usuarios (incluyendo bots) compitiendo por ~900.000 localidades disponibles. La lección arquitectónica central no fue "faltaban servidores": fue que **la demanda de un onsale es un evento físico — un pico de varios órdenes de magnitud — y no se responde sobreaprovisionando infraestructura, sino controlando la admisión y protegiendo los invariantes del sistema** (nunca vender dos veces la misma entrada).

TicketNow replica ese problema a escala didáctica y lo resuelve con los patrones que usan los sistemas reales de ticketing:

1. **Fila virtual (virtual waiting room):** el exceso de demanda espera afuera del sistema; solo entra tanta gente como el sistema puede atender.
2. **Reserva atómica de inventario:** la disponibilidad se decrementa con una operación atómica en Redis (script Lua); es imposible vender de más por construcción.
3. **Saga con compensaciones:** la compra (reservar → cobrar → confirmar → emitir entrada) es un proceso distribuido tolerante a fallas, con devoluciones automáticas de stock.
4. **Outbox transaccional + eventos de dominio:** el estado se propaga de forma confiable entre servicios sin llamadas síncronas frágiles.
5. **Observabilidad de extremo a extremo:** cada compra se puede seguir a través de todos los servicios con un solo trace ID.

El sistema se entrega por fases (ver roadmap), completamente dockerizado, con escenarios de carga y de caos para verificar el invariante sagrado: **zero double-sell**.

## 2. Decisiones clave

| # | Decisión | Fundamento resumido | ADR |
|---|---|---|---|
| D1 | Microservicios con hot path corto | Aislamiento de falla y escalado independiente; el camino del usuario tiene máx. 1 hop síncrono + 1 op Redis | [ADR-001](02-arquitectura.md#adr-001-microservicios-vs-monolito-modular) |
| D2 | PostgreSQL como source of truth + Redis en el hot path | Dinero = ACID y auditoría; velocidad = memoria. Escritura a PG fuera del camino crítico del usuario | [ADR-002](02-arquitectura.md#adr-002-postgresql--redis-roles-y-no-solo-nosql) |
| D3 | RabbitMQ + MassTransit | Sagas state machine y outbox transaccional integrados en .NET | [ADR-003](02-arquitectura.md#adr-003-rabbitmq--masstransit-como-broker) |
| D4 | YARP como gateway | Rate limiting y control de admisión en el borde, en .NET | [ADR-004](02-arquitectura.md#adr-004-yarp-como-gateway) |
| D5 | Fila virtual propietaria sobre Redis | Control total del UX (posición en tiempo real vía SignalR) y de la tasa de admisión adaptativa | [ADR-005](02-arquitectura.md#adr-005-fila-virtual-propietaria-vs-servicio-externo) |
| D6 | Ledger append-only de inventario | Auditoría total del stock: cada entrada vendida/liberada tiene registro inmutable | [ADR-006](02-arquitectura.md#adr-006-ledger-append-only-de-inventario) |
| D7 | Outbox transaccional | Estado en PG y eventos en RabbitMQ de forma atómica (sin mensajes fantasma ni perdidos) | [ADR-007](02-arquitectura.md#adr-007-outbox-transaccional) |
| D8 | CDN en el borde (emulado localmente con nginx) | Assets y catálogo GET cacheados afuera del origen; rutas de compra y fila jamás cacheadas | [ADR-008](02-arquitectura.md#adr-008-cdn-en-el-borde-y-su-emulación-local) |
| D9 | Checkout asíncrono (SubmitOrder + doble validación + idempotencia en 3 capas) | Atomicidad total sin acoplar la API; UX determinista ante reintentos | [ADR-009](02-arquitectura.md#adr-009-checkout-asíncrono-con-comando-submitorder--validación-en-dos-tiempos) |
| D10 | Fila virtual en Redis con tasa adaptativa y free-pass por onsale | E3 se serializa en la puerta (el borde decide una vez); eventos sin tormenta no pagan fila | [ADR-010](02-arquitectura.md#adr-010-fila-virtual-con-free-pass-configurable-por-onsale) |
| D11 | Identidad JWT + límites por cuenta con techo aportado por el cliente | Cupo y rate limit reales sin acoplar servicios al IdP/catálogo; conteo siempre server-side | [ADR-011](02-arquitectura.md#adr-011-identidad-y-límites-de-fase-6-jwt-usuario--cupo-por-cuenta-aportado-por-el-cliente) |

## 3. Objetivos

### Objetivos de negocio (simulados)

- Vender entradas de eventos con demanda extrema sin caídas ni sobreventa.
- Experiencia justa y transparente: posición real en fila, tiempo estimado, sin "carritos fantasma".
- Panel de organizador: configurar onsales (hora de inicio, aforo, límites por cuenta) y monitoreo en vivo.

### Objetivos didácticos (reales)

- Entender y aplicar: waiting room, backpressure, reservation pattern, saga, outbox, CQRS-lite, idempotencia, rate limiting, cache-aside, observabilidad distribuida.
- Verificar invariantes bajo caos: zero double-sell aunque se caigan nodos, se sature Redis o se pierdan mensajes.
- Practicar operación: escalar réplicas en caliente, leer dashboards, diagnosticar con traces.

## 4. Qué NO es TicketNow (anti-alcance)

- No es un sistema multi-tenant multi-organizador de mercado (un solo operador simulado).
- No procesa pagos reales ni datos de tarjetas (provider mock tokenizado; integrar Stripe en modo test es un stretch goal).
- No incluye kubernetes, service mesh ni multi-región (se documenta el camino, no se implementa).
- No combate bots a nivel de red/ML (se aplican controles básicos didácticos: límites por cuenta, PoW opcional, captcha mock).

## 5. Invariantes sagrados

Estas propiedades se verifican con pruebas automatizadas y de caos; ninguna entrega puede romperlas:

| ID | Invariante | Verificación |
|---|---|---|
| INV-1 | **Zero double-sell:** jamás se confirma más entradas que el aforo de una zona | Prueba de concurrencia + reconciliador continuo |
| INV-2 | **Todo hold expira o se confirma:** no hay stock "retenido para siempre" | Sweeper de TTLs + reconciliador |
| INV-3 | **Sin pago no hay entrada:** ticket emitido si y solo si payment confirmed en ledger | Auditoría de saga |
| INV-4 | **La fila es FIFO justa:** nadie que llegó después entra antes (salvo reconexión dentro de gracia) | Pruebas de orden en la fila |
| INV-5 | **Idempotencia:** reintentos de checkout/pago nunca crean órdenes duplicadas | Pruebas con Idempotency-Key repetido |

## 6. Índice de documentos

1. [Visión y requisitos](01-vision-y-requisitos.md) — personas, historias, FR/NFR medibles, escenarios de carga.
2. [Arquitectura](02-arquitectura.md) — principios, C4, servicios, flujo crítico, mensajería, ADRs.
3. [Patrones](03-patrones.md) — el corazón didáctico: cada patrón con su modo de falla.
4. [Datos y APIs](04-datos-y-apis.md) — modelo, keyspace, contratos REST/SignalR/eventos.
5. [Despliegue y observabilidad](05-despliegue-y-observabilidad.md) — Docker, CI/CD, métricas, carga y caos.
6. [Seguridad](06-seguridad.md) — amenazas, authN/Z, pagos, QR, OWASP.

## 7. Roadmap

| Fase | Entregable | Patrones que introduce | Criterio de salida |
|---|---|---|---|
| 0 | Fundación: repo, Docker Compose base, CI, observabilidad inicial | Health checks, structured logging | `docker compose up` verde con 6 servicios + infra |
| 1 | Catálogo + admin de eventos (sin presión de demanda) | Cache-aside, CQRS-lite | Navegar eventos con p95 < 150 ms |
| 2 | Inventario: holds atómicos + ledger | Lua atómico, reservation, sweeper, reconciliador | Prueba de concurrencia sin INV-1 violado |
| 3 | Checkout: saga + pagos mock + tickets QR | Saga, outbox, idempotencia | Compra e2e con reintentos, INV-3 e INV-5 |
| 4 | Fila virtual + control de admisión en gateway | Waiting room, backpressure, admission token | Onsale simulado 5.000 usuarios, INV-4 |
| 5 | Carga + caos + afinado | Rate limiting adaptativo, circuit breakers | k6 storm + chaos sin invariantes rotos |
| 6 | Hardening: anti-bot básico, límites, panel en vivo | — | SLOs cumplidos en scenario final |

## 8. Riesgos principales

| Riesgo | Impacto | Mitigación |
|---|---|---|
| Redis es single point of failure | Crítico (fila + stock en memoria) | AOF + réplica, verificación periódica ledger→Redis, documentar failover manual |
| Hot key: un evento ultra popular concentra operaciones en una clave | Degradación de Redis | Operaciones O(log n) cortas (ZSET/Lua), sharding por zona, medir en fase 5 |
| Complejidad didáctica: 13 contenedores | Abandono/frustración | Fases incrementales; cada fase funciona sola |
| MassTransit v9 pasa a licencia comercial | Dependencia | Fijar v8 LTS (OSS); contratos propios desacoplan el transporte |
| Clock skew en TTLs de holds | Expiraciones injustas | TTL con margen (gracia), reloj con NTP, expiración autoritativa del sweeper |
| Sobreenfoque en patrones (over-engineering) | Nunca termina | Invariantes + roadmap como contrato de alcance |

## 9. Glosario

| Término | Definición |
|---|---|
| Onsale | Ventana de tiempo en que se habilita la compra de un evento; el momento del pico de demanda |
| Waiting room / fila virtual | Sala de espera previa al sistema; regula cuántos usuarios acceden simultáneamente |
| Admission token | JWT de vida corta que acredita que el usuario fue admitido desde la fila; el gateway lo exige en rutas protegidas |
| Hold | Reserva temporal de N entradas de una zona, con TTL (8 min), a nombre de un usuario |
| Oversell / double-sell | Vender la misma entrada a dos compradores; el invariante INV-1 lo prohíbe |
| Ledger | Registro append-only de movimientos de inventario (HOLD / CONFIRM / RELEASE / EXPIRE) |
| Saga | Secuencia distribuida de pasos con compensaciones (no una transacción ACID distribuida) |
| Outbox | Patrón: escribir en la DB y el evento pendiente en la misma transacción; un proceso lo publica luego al broker |
| Backpressure | Capacidad de ralentizar la admisión de trabajo cuando el sistema muestra saturación |
| CQRS-lite | Separar modelos de lectura (proyecciones desnormalizadas) de escritura, sin dos stores completos |
| Idempotencia | Mismo request repetido produce el mismo efecto único (no duplicados) |
| Trace | Seguimiento de un request a través de todos los servicios (OpenTelemetry) |
| Sweeper | Job de fondo que detecta holds expirados y libera el stock |
| Reconciliador | Job que compara ledger (verdad) contra Redis (velocidad) y repara divergencias |
| SLO / SLI | Objetivo / indicador de nivel de servicio (ej. p95, disponibilidad) |

---

*Próximo paso sugerido: leer [01-vision-y-requisitos.md](01-vision-y-requisitos.md) y validar alcance antes de escribir código.*
