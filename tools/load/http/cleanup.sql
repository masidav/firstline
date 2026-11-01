-- Undoes setup.sql for one run tag (psql variable run) as far as the database allows: registrations and the ledger are
-- never deleted, so the allocation is cancelled (which withdraws every registration), the artist is paused (hidden from
-- fans), and the synthetic fans are deleted the way account deletion does it (handle and email scrubbed, sessions
-- ended), so they vanish from search and their tokens stop working at once. Safe to run twice.
\set ON_ERROR_STOP 1
BEGIN;
UPDATE allocations SET cancelled_at = now()
 WHERE id = md5('fandom-load:' || :'run' || ':allocation')::uuid AND cancelled_at IS NULL;
UPDATE artists SET status = 'paused'
 WHERE id = md5('fandom-load:' || :'run' || ':artist')::uuid AND status <> 'paused';
UPDATE users SET deleted_at = now()
 WHERE handle LIKE 'lt' || :'run' || '\_%' AND email LIKE '%@example.test' AND deleted_at IS NULL;
COMMIT;
SELECT (SELECT count(*) FROM allocation_registrations
         WHERE allocation_id = md5('fandom-load:' || :'run' || ':allocation')::uuid AND withdrawn_at IS NULL) AS live_registrations,
       (SELECT count(*) FROM users WHERE handle LIKE 'lt' || :'run' || '\_%') AS fans_left;
