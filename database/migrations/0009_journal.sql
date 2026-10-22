-- Album journal (Phase 6): the Idempotency-Key replay store and the indexes the journal queries need.
-- Album rows come from the catalog (ADR 0006); logs award points through the triggers in 0001.

-- One stored response per user and Idempotency-Key, kept 24 hours (openapi conventions). A row is inserted
-- before the work and filled in with the response in the same transaction, so a concurrent repeat waits on
-- the primary key and then replays instead of doing the work twice.
CREATE TABLE idempotency_keys (
  user_id        uuid NOT NULL REFERENCES users ON DELETE CASCADE,
  key            text NOT NULL CHECK (char_length(key) BETWEEN 1 AND 64),
  request_sha256 bytea NOT NULL CHECK (octet_length(request_sha256) = 32),
  status_code    integer,                            -- NULL while the first call is still running
  response       json,                               -- the exact original body, replayed byte for byte
  created_at     timestamptz NOT NULL DEFAULT now(),
  PRIMARY KEY (user_id, key),
  CHECK ((status_code IS NULL) = (response IS NULL))
);
CREATE INDEX idempotency_keys_created_idx ON idempotency_keys (created_at);

-- Relisten check and "my latest log" per album; community logs per album, live rows only.
CREATE INDEX album_logs_user_album_idx ON album_logs (user_id, album_id, created_at DESC) WHERE deleted_at IS NULL;
CREATE INDEX album_logs_album_live_idx ON album_logs (album_id, created_at DESC) WHERE deleted_at IS NULL;
-- Album search by title prefix (live albums).
CREATE INDEX albums_title_idx ON albums (lower(title) text_pattern_ops) WHERE delisted_at IS NULL;
