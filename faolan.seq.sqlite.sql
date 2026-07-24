-- GENERATED (#411): SQLite half of the seed - the AUTOINCREMENT high-water marks.
-- Run after faolan.sql. Pointless on a db with no AUTOINCREMENT tables; harmless there.
BEGIN TRANSACTION;
DELETE FROM sqlite_sequence WHERE name = 'AppearanceChoices';
INSERT INTO sqlite_sequence (name, seq) VALUES ('AppearanceChoices', 634);
DELETE FROM sqlite_sequence WHERE name = 'NpcMorphs';
INSERT INTO sqlite_sequence (name, seq) VALUES ('NpcMorphs', 2);
DELETE FROM sqlite_sequence WHERE name = 'Shops';
INSERT INTO sqlite_sequence (name, seq) VALUES ('Shops', 29);
DELETE FROM sqlite_sequence WHERE name = 'NpcEquipment';
INSERT INTO sqlite_sequence (name, seq) VALUES ('NpcEquipment', 10);
DELETE FROM sqlite_sequence WHERE name = 'PatrolPaths';
INSERT INTO sqlite_sequence (name, seq) VALUES ('PatrolPaths', 422);
DELETE FROM sqlite_sequence WHERE name = 'PatrolNodes';
INSERT INTO sqlite_sequence (name, seq) VALUES ('PatrolNodes', 2460);
COMMIT;
