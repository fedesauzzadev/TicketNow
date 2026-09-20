# Carga k6 — escenarios E1–E4 (Fase 5, docs/05 §5.2)

Cada escenario crea su propio fixture (venue + evento + zonas + onsale ya
abierto) en `setup()` vía APIs de admin, así que las corridas son repetibles
y no dependen del seed. La siembra de stock y la habilitación de la fila las
hace el propio sistema (opener → `OnsaleOpened` → inventory/queue): la carga
ejercita el camino real de producción.

## Destinos: edge vs gateway directo

- **E1/E2 van al edge** (`TARGET=http://edge-cache:80`, default): se mide el
  CDN (MISS → HIT). Las lecturas cacheadas ni llegan al origen.
- **E3/E4 van directo al gateway** (`TARGET=http://gateway:8080`): miden el
  camino de compra real. Cada VU lleva su `X-Forwarded-For` sintético
  (`fakeIp()` en `lib.js`) para simular usuarios distintos — el rate limit por
  IP del gateway necesita IPs distintas y todos los VUs salen de un solo
  contenedor. Red de test cerrada: documentado, no apto para prod.

## Identidad y captcha en la tormenta (Fase 6)

Cada VU hace `loginAs()` (JWT de usuario) y resuelve el captcha mock
(`solveCaptcha()`) antes de tokenizar: sin ambos, el gateway responde
401/400. La orden lleva `eventId` + `maxPerAccount` (límite por cuenta).

## Corridas

```bash
# E1/E2 por el edge (default)
docker compose --profile load run --rm k6
docker compose --profile load run --rm -e K6_SCRIPT=e2-countdown.js k6

# E3 tormenta (gateway directo + IPs por VU)
docker compose --profile load run --rm -e K6_SCRIPT=e3-onsale-storm.js -e TARGET=http://gateway:8080 -e VUS=500 -e STORM=5m k6
# E4 drenaje (gateway directo)
docker compose --profile load run --rm -e K6_SCRIPT=e4-flash-drain.js -e TARGET=http://gateway:8080 -e REQUESTS=4999 -e CAPACITY=10 k6

# En host (k6 instalado): k6 run -e TARGET=http://localhost:<puerto> load/e1-browse.js
```

Humo rápido (valida el harness sin gastar la máquina):

```bash
docker compose --profile load run --rm -e K6_SCRIPT=e3-onsale-storm.js -e TARGET=http://gateway:8080 -e VUS=20 -e STORM=1m k6
```

## Qué valida cada uno

| Escenario | Carga | Thresholds | Invariante |
|---|---|---|---|
| `e1-browse.js` | 100 RPS lectura catálogo, 2 min | failed < 1%, p95 < 800 ms | Edge absorbe (MISS → HIT) |
| `e2-countdown.js` | 500 VUs × refresh /5 s | failed < 1%, p95 < 500 ms | Todo HIT en el borde |
| `e3-onsale-storm.js` | rampa a VUS (def. 20, real 5000), STORM sostenido | failed < 5%, `checkout_ok` > 0, `time_to_admit` p95 | Órdenes confirman; 10% rechazos compensan |
| `e4-flash-drain.js` | N holds contra 10 unidades | `holds_granted == CAPACITY` | INV-1: ni una unidad de más |

Validación post-carga (invariantes, docs/05 §5.2): stock final por zona =
`capacity + SUM(ledger.delta)` con cero divergencias
(`inventory_divergence_total` en `/metrics`); toda orden `Confirmed` tiene pago
autorizado y ticket.

## Afinación de la admisión (el "afino" de Fase 5)

Perillas (override admin de fila o config del onsale en catálogo):

| Perilla | Efecto | Cuándo moverla |
|---|---|---|
| `initialRate` (tasa K inicial) | Cuántos admite el 1er tick | E3: subir hasta que `POST /holds` p99 < 50 ms se rompa, luego −20% |
| `minRate` / `maxRate` | Piso/techo de la histéresis | Techo = holds/s que el interior sostiene sin degradar |
| `maxConcurrentInside` | Cupo de admitidos simultáneos | Subir con réplicas de inventory/orders |
| `Catalog:OnsaleOpenerIntervalSeconds` | Latencia apertura→siembra | Default 5 s; no bajar de 1 s en prod |

Lecturas durante la tormenta: `admission:{onsale}:rate` (Redis),
`GET /api/queue/admin/{onsaleId}` (waiting/admitted/rate),
`/metrics` (holds, saga, divergencias), Grafana (profile `obs`).

Regla de oro: la tasa baja rápido (K/2 ante salud degradada) y sube lento
(+10%/ciclo). Ante duda, tasa inicial conservadora: la fila es la que absorbe,
no el interior.
