-- Initial migration for TodoSync production database
-- Generated: 2026-03-14

-- ==================== TODOS TABLE ====================
CREATE TABLE IF NOT EXISTS todos (
    id VARCHAR(36) PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL DEFAULT 'default',
    title VARCHAR(500) NOT NULL,
    priority VARCHAR(20) NOT NULL DEFAULT 'MEDIUM',
    day_key VARCHAR(10) NOT NULL,
    sort_order INTEGER NOT NULL DEFAULT 0,
    completed BOOLEAN NOT NULL DEFAULT FALSE,
    deleted BOOLEAN NOT NULL DEFAULT FALSE,
    created_at BIGINT NOT NULL,
    updated_at BIGINT NOT NULL,
    version INTEGER NOT NULL DEFAULT 1
);

CREATE INDEX IF NOT EXISTS idx_todos_tenant_updated ON todos(tenant_id, updated_at);
CREATE INDEX IF NOT EXISTS idx_todos_tenant_day_sort ON todos(tenant_id, day_key, sort_order);
CREATE INDEX IF NOT EXISTS idx_todos_deleted ON todos(deleted) WHERE deleted = FALSE;

-- ==================== SYNC_CHANGES TABLE ====================
CREATE TABLE IF NOT EXISTS sync_changes (
    change_id UUID PRIMARY KEY,
    tenant_id VARCHAR(36) NOT NULL DEFAULT 'default',
    entity_type VARCHAR(50) NOT NULL DEFAULT 'todo',
    entity_id VARCHAR(36) NOT NULL,
    operation VARCHAR(20) NOT NULL,
    payload_json TEXT NOT NULL,
    created_at BIGINT NOT NULL,
    trace_id VARCHAR(64),
    event_id VARCHAR(36)
);

CREATE INDEX IF NOT EXISTS idx_sync_changes_tenant_changeid ON sync_changes(tenant_id, change_id);
CREATE INDEX IF NOT EXISTS idx_sync_changes_tenant_createdat ON sync_changes(tenant_id, created_at);
CREATE INDEX IF NOT EXISTS idx_sync_changes_entityid ON sync_changes(entity_id);

-- ==================== OUTBOX_EVENTS TABLE ====================
CREATE TABLE IF NOT EXISTS outbox_events (
    id UUID PRIMARY KEY,
    event_id VARCHAR(36) NOT NULL UNIQUE,
    topic VARCHAR(100) NOT NULL,
    key VARCHAR(100) NOT NULL,
    payload_json TEXT NOT NULL,
    status VARCHAR(20) NOT NULL DEFAULT 'pending',
    retry_count INTEGER NOT NULL DEFAULT 0,
    created_at BIGINT NOT NULL,
    sent_at BIGINT,
    error_message VARCHAR(1000)
);

CREATE INDEX IF NOT EXISTS idx_outbox_status_created ON outbox_events(status, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS idx_outbox_eventid ON outbox_events(event_id);

-- ==================== PROCESSED_EVENTS TABLE ====================
CREATE TABLE IF NOT EXISTS processed_events (
    id UUID PRIMARY KEY,
    event_id VARCHAR(36) NOT NULL UNIQUE,
    processed_at BIGINT NOT NULL,
    tenant_id VARCHAR(36) NOT NULL DEFAULT 'default'
);

CREATE UNIQUE INDEX IF NOT EXISTS idx_processed_eventid ON processed_events(event_id);
CREATE INDEX IF NOT EXISTS idx_processed_tenant ON processed_events(tenant_id, processed_at);

-- ==================== PERFORMANCE TUNING ====================

-- Vacuum settings for high-write tables
ALTER TABLE sync_changes SET (autovacuum_vacuum_scale_factor = 0.05);
ALTER TABLE outbox_events SET (autovacuum_vacuum_scale_factor = 0.05);

-- Enable auto-vacuum for processed_events (cleanup old entries)
ALTER TABLE processed_events SET (autovacuum_vacuum_scale_factor = 0.1);

-- ==================== CLEANUP POLICY ====================

-- Cleanup old processed events (keep last 30 days)
-- Run this via cron job or scheduled task
-- DELETE FROM processed_events WHERE processed_at < EXTRACT(EPOCH FROM NOW() - INTERVAL '30 days') * 1000;

-- Cleanup old outbox events (keep last 7 days)
-- DELETE FROM outbox_events WHERE status = 'sent' AND sent_at < EXTRACT(EPOCH FROM NOW() - INTERVAL '7 days') * 1000;

-- ==================== COMMENTS ====================
COMMENT ON TABLE todos IS 'Main todo items table with soft delete support';
COMMENT ON TABLE sync_changes IS 'Change log for delta sync - ordered by change_id for efficient pagination';
COMMENT ON TABLE outbox_events IS 'Transactional outbox pattern - ensures reliable event publishing';
COMMENT ON TABLE processed_events IS 'Idempotency tracking - prevents duplicate event processing';
