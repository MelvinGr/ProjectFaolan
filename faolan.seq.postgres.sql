-- GENERATED (#411): Postgres half of the seed - advances each identity sequence past the
-- explicit ids inserted by faolan.sql, so the next INSERT does not collide. Run after it.
BEGIN;
SELECT setval(pg_get_serial_sequence('"FeatIdMaps"', 'WireFeatId'), 2000500);
COMMIT;
