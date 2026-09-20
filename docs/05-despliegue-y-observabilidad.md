# 05 · Despliegue, observabilidad y pruebas

TicketNow · SDD · v1.0

---

## 1. Topología Docker Compose

Un solo `docker-compose.yml` base + **profiles**: `debug` (puertos internos expuestos), `obs` (prometheus, grafana, jaeger), `loadtest` (k6, generador de tormenta).

| Contenedor | Imagen / build | Puertos host | Healthcheck |
|---|---|---|---|
| `edge-cache` | nginx 1.27 (`proxy_cache`) | 8090 | `wget /` (emula el CDN, ADR-008) |
| `gateway` | build `src/Gateway` | interno (tras edge-cache) | `/health/ready` |
| `spa` | build `src/spa` (nginx + dist Vite) | interno (tras edge-cache); dev 5173 | `wget /` |
| `queue-service` | build | — | `/health/ready` (DependsOf redis+rabbit) |
| `catalog-service` | build | — | idem |
| `inventory-service` | build | — | idem + `/health/deps` (ping Redis/PG) |
| `orders-service` | build | — | idem |
| `notifications-service` | build | — | idem |
| `postgres` | `postgres:16` | 5435 (solo dev) | `pg_isready` |
| `redis` | `redis:7` (AOF everysec) | 6382 (solo dev) | `redis-cli ping` |
| `rabbitmq` | `rabbitmq:3.13-management` | 5673, 15673 | `rabbitmq-diagnostics ping` |
| `prometheus` | `prom/prometheus` | 9090 | — |
| `grafana` | `grafana/grafana` (prov. dashboards) | 3001 | — |
| `jaeger` | `jaegertracing/all-in-one` | 16686 | — |
| `k6` (profile loadtest) | `grafana/k6` | — | — |

Boceto (abreviado):

```yaml
services:
  edge-cache:                      # emulación local del CDN (ADR-008)
    image: nginx:1.27
    ports: ["8080:80"]             # única puerta pública HTTP
    volumes:
      - ./deploy/edge-cache/nginx.conf:/etc/nginx/conf.d/default.conf:ro
    depends_on: [gateway]

  gateway:                          # interno: detrás del edge-cache
    build: src/Gateway
    environment:
      - Redis__ConnectionString=redis:6379
      - Routes__Queue=http://queue-service:8080
      # ...
    depends_on:
      redis: { condition: service_healthy }
      rabbitmq: { condition: service_healthy }
    healthcheck:
      test: ["CMD", "wget", "-qO-", "http://localhost:8080/health/ready"]
      interval: 5s

  inventory-service:
    build: src/InventoryService
    deploy: { replicas: 2 }        # docker compose up -d --scale inventory-service=4
    depends_on:
      postgres: { condition: service_healthy }
      redis: { condition: service_healthy }
      rabbitmq: { condition: service_healthy }

  redis:
    image: redis:7
    command: ["redis-server", "--appendonly", "yes", "--appendfsync", "everysec"]
    volumes: [redisdata:/data]

  # ... postgres, rabbitmq, servicios restantes, stack obs (profile: obs)
volumes: { pgdata: {}, redisdata: {} }
```

Reglas operativas:

- Servicios sin estado → escalar con `--scale` sin downtime (healthchecks + gateway retry).
- Redes: `frontend` (spa↔gateway) y `backend` (gateway↔servicios↔infra); infra no expuesta salvo profile `debug`.
- Config 12-factor: todo por variables de entorno (`ConnectionStrings__*`, `Features__*`); migraciones EF Core automáticas al arrancar (didáctico; en prod, job aparte).

## 2. Variables de entorno principales

| Variable | Default dev | Descripción |
|---|---|---|
| `ConnectionStrings__Postgres` | `Host=postgres;Database=ticketnow;…` | PG (schemas por servicio) |
| `ConnectionStrings__Redis` | `redis:6379` | Hot path |
| `RabbitMq__Host` / `__User` / `__Pass` | rabbitmq / ticketnow / … | Broker |
| `Onsale__HoldTtlMinutes` | `8` | TTL de hold |
| `Onsale__MaxPerAccount` | `4` | Límite por cuenta |
| `Queue__AdmissionRatePerSec` | `50` | K inicial |
| `Queue__MaxConcurrentInside` | `2000` | Aforo del "adentro" |
| `Payment__Mock__LatencyMs` / `__DeclineRate` | `1500` / `0.10` | Mock de PSP |
| `Otel__Endpoint` | `http://jaeger:4317` | Traces OTLP |

## 3. CI/CD (GitHub Actions, boceto)

```text
PR:   build + `dotnet format --verify` + unit tests + integration (Testcontainers)
      + SPA lint/build + compose config check
main: imagen docker por servicio → GHCR + deploy automático a entorno docker local/VM de pruebas
      semanal o pre-release: smoke de carga (E1) + prueba de invariantes (INV-1..5) como suite
```

## 4. Observabilidad

### 4.1 Pilares

| Pilar | Herramienta | Nota |
|---|---|---|
| Logs | Serilog estructurado (JSON) → stdout → Loki/Docker | Enfoque: eventos con `OrderId`/`HoldId`/`SessionId` siempre presentes |
| Métricas | prometheus-net + OTel → Prometheus | RED por servicio + métricas de dominio (abajo) |
| Trazas | OpenTelemetry (OTLP) → Jaeger | Propagación automática HTTP + MassTransit (bags: orderId) |

### 4.2 Métricas de dominio (las que importan del negocio)

| Métrica | Tipo | Fuente | Para qué |
|---|---|---|---|
| `queue_waiting_count{onsale}` | gauge | queue | Longitud de fila en vivo |
| `queue_admission_rate{onsale}` | gauge | queue | Ver backpressure actuando |
| `queue_admitted_total` / `queue_abandoned_total` | counter | queue | Conversión de la fila |
| `inventory_holds_active{event,zone}` | gauge | inventory | Presión sobre el stock |
| `inventory_hold_attempts_total{result=ok|denied}` | counter | inventory | Demanda satisfecha vs insatisfecha |
| `inventory_divergence_total` | counter | reconciliador | **Canario anti-oversell (INV-1/2)** — debe ser 0 |
| `orders_saga_state{state}` | gauge | orders | Distribución de estados de saga (estancamiento visible) |
| `payments_result_total{authorized|declined|timeout}` | counter | orders | Salud del checkout |
| `tickets_issued_total` | counter | orders | Velocidad real de ventas |
| `outbox_pending{service}` | gauge | todos | Broker caído/demorado |

### 4.3 Dashboards (Grafana, provisionados)

1. **Onsale Live** (el organizador): fila, K de admisión, ventas/min por zona, disponibilidad, errores.
2. **SLO & RED**: latencias p50/p95/p99 por endpoint, RPS, error rate, saturación (contra NFR-01..06).
3. **Inventario**: holds activos, expiraciones, divergencia (grande y verde o explicación).
4. **Saga & Pagos**: estados, DLQ, tiempos por paso del checkout.

### 4.4 Alertas mínimas

| Condición | Severidad | Acción |
|---|---|---|
| `inventory_divergence_total` aumenta | page | Pausar admisión manual (feature flag), investigar ledger |
| Error rate > 2% 5 min en gateway | page | Revisar réplicas; el backpressure ya debió actuar |
| `outbox_pending` > 1000 o creciendo 5 min | page | Broker caído: la fila sigue, la confirmación espera |
| DLQ con mensajes | warn | Inspeccionar poison messages |
| p95 de holds > 40 ms 5 min | warn | Saturación de Redis / hot key |

## 5. Pruebas

### 5.1 Estrategia

| Nivel | Herramienta | Qué cubre |
|---|---|---|
| Unitarias (xUnit) | reglas de dominio, cálculo de K, scoring de fila | Rápido, sin IO |
| Concurrencia (xUnit + Tasks) | **INV-1**: N tareas simultáneas contra el Lua con stock=X → exactamente X ok | El test estrella del proyecto |
| Integración | Testcontainers (PG, Redis) + RabbitMQ real + PG/Redis de compose vía env | Migraciones, outbox, saga e2e |
| Contrato | Spa contracts vs OpenAPI (`Microsoft.OpenApi.Readers` en CI) | Anti-drift frontend/backend |
| Carga | k6 (escenarios E1–E4) | NFR-01..06 |
| Caos | scripts (kill/latencia/partición) | NFR-08..09 e invariantes |

### 5.1bis Aislamiento de los tests de integración (lecciones Fase 3)

Los tests multi-servicio comparten proceso, broker y (a veces) PG. Sin aislamiento total, se envenenan entre sí. Reglas vigentes:

| Riesgo | Mitigación implementada |
|---|---|
| Misma cola consumida por buses de distintos tests/corridas | `Messaging:EndpointSuffix` único por fixture (colas `*-<suffix>`); `PurgeOnStartup` en Testing |
| Colas huérfanas acumuladas entre corridas | Limpieza manual (`DELETE /api/queues`) — NO usar `auto_delete` solo en el lado receive: el lado send re-declara con otros args y RabbitMQ responde 406 PRECONDITION_FAILED |
| Misma DB con schemas distintos + caché estática de MassTransit (lock statements por TIPO CLR) | DbContexts derivados en tests con TODO en `public` (`TestDbContexts.cs`) + una DB por servicio + migraciones `TestInit` propias |
| Hosts levantados por test (churn de migraciones/buses) | `CheckoutEnvironment` compartido (IClassFixture): hosts una vez por clase |
| Buses que nunca arrancan | Jamás `RemoveAll<IHostedService>` (mata el host de MassTransit); remover workers puntuales |
| Broker/DBs de la corrida vs compose | Mismos nombres de colas que producción (sin sufijo) solo en compose; tests siempre con sufijo |
| **Cross-talk por exchanges de tipo compartidos (lección Fase 4)** | El sufijo aísla COLAS, pero los `Publish` van a exchanges fanout compartidos. Si los servicios de app de compose están corriendo, la saga de Docker también consume `OrderSubmitted` del test, su inventory responde `HoldInvalid` (no conoce el hold del test) y la saga del test rechaza la orden: e2e fallidos "intermitentes" según quién gane la carrera. **Regla: al correr la suite en host, la app de compose debe estar DETENIDA (solo postgres/rabbitmq/redis)**: `docker compose stop catalog-service inventory-service orders-service notifications-service queue-service gateway spa edge-cache` |

Comandos:

```bash
# Suite completa en host (requiere compose con postgres/redis/rabbitmq arriba
# y los servicios de ABAJO — ver cross-talk arriba):
$env:TICKETNOW_TEST_POSTGRES = "Host=localhost;Port=5435;..."
$env:TICKETNOW_TEST_RABBITMQ_PORT = "5673" # + USER/PASS
dotnet test -c Release

# Suite hermética en red Docker (profile test, sin port-forward):
docker compose --profile test run --rm test-runner
```

### 5.2 k6 — escenarios E1–E4 (suite en `load/`, Fase 5)

Cada escenario crea su propio fixture en `setup()` (venue + evento + onsale
ya abierto): la siembra y la habilitación de la fila las hace el propio
sistema vía `OnsaleOpened`. E1/E2 van al edge (miden el CDN); E3/E4 van
directo al gateway con `X-Forwarded-For` por VU (el rate limit por IP necesita
usuarios distintos; red de test cerrada). Ver `load/README.md`.

```bash
docker compose --profile load run --rm k6  # E1
docker compose --profile load run --rm -e K6_SCRIPT=e2-countdown.js k6
docker compose --profile load run --rm -e K6_SCRIPT=e3-onsale-storm.js -e TARGET=http://gateway:8080 -e VUS=500 -e STORM=5m k6
docker compose --profile load run --rm -e K6_SCRIPT=e4-flash-drain.js -e TARGET=http://gateway:8080 -e REQUESTS=4999 -e CAPACITY=10 k6
```

Resultados de referencia (laptop dev, 2026-09-19): E1 100 RPS p95 < 1 ms;
E2 50 VUs p95 < 1 ms todo HIT; E3 humo (20 VUs, CAPACITY=200) 200 confirmados
+ 57 rechazados (compensaciones OK), `time_to_admit` p95 = 2 s, 0 errores
reales; E4 500 peticiones contra 10 unidades → exactamente 10 otorgados
(INV-1 bajo contención real).

Métricas propias de E3 (los 409 soldout y 404 de poll temprano son protocolo,
no errores): `checkout_ok`, `checkout_rejected`, `time_to_admit`,
`storm_errors` (< 2%).

Lección Fase 5 (k6 la encontró): el rate limiter del gateway era 100 req/min
por IP **de conexión** — todo el tráfico del edge comparte la IP del nginx, y
`QueueLimit = 30` aparcaba requests hasta el timeout del cliente (429 + stalls
a la vez). Ahora: `ForwardedHeaders` (partición por IP real), 600 req/min por
IP, `QueueLimit = 0` (fail fast con 429 + Retry-After). Ver docs/06 §3.

Validación post-carga (script SQL + Prometheus query): stock final por zona = `capacity + SUM(ledger.delta)` con **cero** divergencias; toda orden `confirmed` tiene pago autorizado y ticket.

### 5.3 Experimentos de caos (fase 5)

| Experimento | Comando (host) | Invariante a observar |
|---|---|---|
| Matar una réplica de inventory en pleno storm | `docker kill compose-docker-inventory-service-1` | INV-1, INV-2; sin 5xx masivos |
| Cortar RabbitMQ 60 s | `docker pause rabbitmq` | Outbox retiene; al volver, se drena sin duplicados (INV-3) |
| Latencia artificial en payment mock | subir `Payment__Mock__LatencyMs` | Timeouts de saga → compensaciones correctas |
| Restart de Redis | `docker restart redis` | Recuperación desde ledger; divergencia reparada y alertada |
| Pérdida de paquetes al gateway | `tc netem` (o toxiproxy) | 429 ordenados, cola no se corrompe (INV-4) |

## 6. SLOs y error budget

| SLI | Objetivo (ventana onsale) |
|---|---|
| Disponibilidad gateway | ≥ 99,9% |
| `POST /holds` p99 < 50 ms | ≥ 99% de minutos |
| Checkout completado (submit→confirm) sin intervención | ≥ 99,5% |
| Divergencia de inventario | = 0 (sin budget: es invariante, no SLO) |

Presupuesto de error agotado durante un onsale real = se congela deploy y se hace post-mortem (cultura SRE didáctica incluida).

## 7. Runbook mínimo (a completar por fase)

- Onsale saturado: subir réplicas de inventory/orders; bajar `Queue__AdmissionRatePerSec`.
- DLQ con `ConfirmOrder` fallidos: reintentar tras inspección; el ledger permite reconstruir estado.
- Redis degradado: activar modo "venta pausada" (feature flag) → fila comunica demora; reconciliar tras recuperación.
