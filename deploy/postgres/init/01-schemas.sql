-- TicketNow: un schema por servicio (ADR-002 · docs/04-datos-y-apis.md §2).
-- Didáctico: un solo cluster, aislamiento lógico por schema.
-- En producción: una base de datos por servicio.

CREATE SCHEMA IF NOT EXISTS catalog;
CREATE SCHEMA IF NOT EXISTS inventory;
CREATE SCHEMA IF NOT EXISTS orders;
CREATE SCHEMA IF NOT EXISTS queue_audit;
CREATE SCHEMA IF NOT EXISTS notifications;
