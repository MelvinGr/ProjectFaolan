-- GENERATED (#411): SQLite half of the seed - the AUTOINCREMENT high-water marks.
-- Run after faolan.sql. Pointless on a db with no AUTOINCREMENT tables; harmless there.
BEGIN TRANSACTION;
DELETE FROM sqlite_sequence WHERE name = 'PatrolPaths';
INSERT INTO sqlite_sequence (name, seq) VALUES ('PatrolPaths', 422);
DELETE FROM sqlite_sequence WHERE name = 'PatrolNodes';
INSERT INTO sqlite_sequence (name, seq) VALUES ('PatrolNodes', 2460);
COMMIT;
