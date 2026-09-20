# 06 · Seguridad

TicketNow · SDD · v1.0

---

## 1. Modelo de amenazas (simplificado, STRIDE-lite)

| Amenaza | Ejemplo en TicketNow | Mitigación (§) |
|---|---|---|
| Spoofing | Bot reusa admission tokens / sessiones | 2, 6 |
| Tampering | Alterar el QR o el precio en tránsito | 2, 5 (TLS + firma) |
| Repudiation | "Yo no compré esas 4 entradas" | Ledger + auditoría (3-patrones §3) |
| Information disclosure | Ver tickets ajenos, stock interno | 2 (authz por recurso), OWASP (5) |
| Denial of service | El onsale mismo es un DDoS legítimo; bots lo agravan | Fila + rate limits (4) |
| Elevation of privilege | Llamar a `/api/admin/*` como fan | Roles + gateway (2) |

Activo más valioso: **el stock** (venderlo mal = dinero) y **los tickets** (QR reproducible = reventa/falsificación).

## 2. Autenticación y autorización

| Aspecto | Decisión |
|---|---|
| Identidad fan/staff/organizador | JWT de usuario (Fase 6, mock didáctico: `POST /api/auth/token`, HS256, TTL 12 h, sub=email; en producción: IdP con access corto + refresh rotativo). Roles/refresh quedan para producción |
| Sesión anónima de fila | `sessionId` en Redis — la fila no exige registro; la compra sí (policy `purchase` = admission + user) |
| Roles | No implementados (una sola clase de usuario; admin abierto en dev, documentado) |
| Authz por recurso | Todo endpoint de hold/orden/ticket valida **ownership** (`userId == recurso.userId`); nunca confiar solo en que "el id existe" |
| Admission token | Corto (5 min), scope al onsale, emitido solo por queue-service, verificado en el gateway (ADR-004) |
| Entre servicios | Red interna de Docker (sin exposición); no hay mTLS en v1 (documentado como gap de producción) |

## 3. Hardening del hot path

| Control | Valor |
|---|---|
| Rate limit por IP (gateway) | 600 req/min por IP real (vía `X-Forwarded-For`; el edge es el único ingreso), fail fast 429 + Retry-After, sin cola de espera (Fase 5: antes 100/min por IP del proxy + cola de 30 que aparcaba clientes hasta el timeout) |
| Rate limit por cuenta (rutas compra) | 30/min, bucket manual post-auth en el gateway (Fase 6 implementado: el `User` se puebla en la evaluación de la policy) |
| Límite por cuenta/evento | 4 entradas (FR-33) |
| Límite por payment token (hash tarjeta mock) | 8 entradas |
| Posiciones de fila | 1 por (cuenta \| dispositivo-hash); PoW opcional (patrones §12) |
| Idempotency-Key | Obligatorio en `POST /orders` y pagos (400 si falta) |

## 4. Protección de datos

- **Principio de minimización:** nombre, email, historial. Nada más. Sin PII en JWTs, en logs ni en QRs (el QR lleva solo ids y nonce).
- **Contraseñas:** ASP.NET Core Identity (PBKDF2 default) o Argon2id; lockout progresivo.
- **Datos del mock de pago:** nunca PAN/CVV; el "exchange" de `/api/payments/token` simula tokenización (la tarjeta mock viaja una vez y se descarta; solo queda `provider_ref`).
- **Retención:** logs 30 d; auditoría de ledger por siempre (es el propósito); derecho al olvido = anonimizar `user_id` (el ledger se conserva, el usuario queda irrevinculado).

## 5. OWASP Top 10 — aplicación concreta

| Riesgo | Cómo lo cubrimos |
|---|---|
| A01 Broken Access Control | Policies por rol + ownership por recurso; tests de authz en CI |
| A02 Cryptographic Failures | TLS en el borde; claves JWT/QR fuera del repo (env); Argon2/PBKDF2 |
| A03 Injection | EF Core parametrizado SIEMPRE; Serilog sin interpolar SQL; React escapa por defecto; CSP estricta en nginx |
| A04 Insecure Design | Este documento: threat model, invariantes, caos |
| A05 Security Misconfiguration | Compose sin puertos internos en prod-profile; errores RFC 7807 sin stack traces; headers (HSTS, X-Content-Type-Options, CSP) |
| A06 Vulnerable Components | `dotnet list package --vulnerable` + Dependabot en CI |
| A07 AuthN Failures | Lockout, refresh rotativo con detección de reuso, cookies httpOnly |
| A08 Integrity Failures | Imágenes firmadas en CI (provenance), QR firmado HS256 (§6) |
| A09 Logging Failures | Logs estructurados inmutables con correlation id; alertas de seguridad (login bruteforce) |
| A10 SSRF | Sin fetch de URLs provistas por el usuario (image_url solo del organizador, validado contra allowlist) |

## 6. Entradas (QR) — ciclo de vida seguro

1. Emisión: al confirmar la orden; JWT HS256 (clave `Tickets__QrKey` dedicada), sin PII, `nonce` único, `exp` = evento + 6 h.
2. Portada: pantalla con QR + código humano de respaldo (últimos 6 de `ticketId`).
3. Verificación staff: firma offline (±5 min de tolerancia) + estado `redeemed` online; el canje es una operación idempotente por `jti` (race: dos puertas escaneando el mismo QR a la vez → exactamente un canje exitoso).
4. Anti-screenshot/reenvío (didáctico): el QR muestra "titular + zona", no secretos extra; el nonce hace que re-emisiones (p. ej. email reenviado) invaliden la anterior si se configura así.

## 7. Secretos y configuración

- Nada de secretos en el repo (`.env` en `.gitignore`, ejemplo `.env.example`); en CI, GitHub Secrets.
- Rotación documentada de: `Jwt__Key`, `Tickets__QrKey`, `Queue__AdmissionSigningKey` (doble clave durante rotación: validar vieja+nueva, firmar con nueva).
- `docker compose` en producción real: secrets de Docker o vault externo (fuera de alcance, documentado).

## 8. Checklist de seguridad por fase (CI lo verifica)

- [ ] Fase 1: TLS local (mkcert), headers, cookies httpOnly, `/api/admin/*` con rol.
- [ ] Fase 2: holds con authz de dueño; rate limits activos en gateway.
- [ ] Fase 3: idempotencia obligatoria; sin PII en logs/QRs; refresh rotativo.
- [ ] Fase 4: admission token firmado y verificado; PoW opcional.
- [ ] Fase 6: auditoría anti-bot (flags), reporte de seguridad del onsale simulado.
