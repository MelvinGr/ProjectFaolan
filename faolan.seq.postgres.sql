-- GENERATED (#411): Postgres half of the seed - advances each identity sequence past the
-- explicit ids inserted by faolan.sql, so the next INSERT does not collide. Run after it.
BEGIN;
SELECT setval(pg_get_serial_sequence('"Shops"', 'Id'), 29);
SELECT setval(pg_get_serial_sequence('"PatrolPaths"', 'Id'), 422);
SELECT setval(pg_get_serial_sequence('"PatrolNodes"', 'Id'), 2460);
COMMIT;
