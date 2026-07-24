-- GENERATED (#411): Postgres half of the seed - advances each identity sequence past the
-- explicit ids inserted by faolan.sql, so the next INSERT does not collide. Run after it.
BEGIN;
SELECT setval(pg_get_serial_sequence('"AppearanceChoices"', 'Id'), 634);
SELECT setval(pg_get_serial_sequence('"MapLootEntries"', 'Id'), 160);
SELECT setval(pg_get_serial_sequence('"NpcChatter"', 'Id'), 16);
SELECT setval(pg_get_serial_sequence('"NpcMorphs"', 'Id'), 2);
SELECT setval(pg_get_serial_sequence('"NpcSpawnAreas"', 'Id'), 5328);
SELECT setval(pg_get_serial_sequence('"NpcSpells"', 'Id'), 42);
SELECT setval(pg_get_serial_sequence('"Shops"', 'Id'), 29);
SELECT setval(pg_get_serial_sequence('"WorldObjects"', 'Id'), 1870);
SELECT setval(pg_get_serial_sequence('"ConversationNodes"', 'Id'), 8235);
SELECT setval(pg_get_serial_sequence('"NpcEquipment"', 'Id'), 10);
SELECT setval(pg_get_serial_sequence('"NpcSpawns"', 'Id'), 16827);
SELECT setval(pg_get_serial_sequence('"RecipeItems"', 'Id'), 7406);
SELECT setval(pg_get_serial_sequence('"ShopItems"', 'Id'), 13619);
SELECT setval(pg_get_serial_sequence('"ConversationPackets"', 'Id'), 31890);
SELECT setval(pg_get_serial_sequence('"NpcSpawnEquipment"', 'Id'), 222391);
SELECT setval(pg_get_serial_sequence('"NpcSpawnMorphs"', 'Id'), 355133);
SELECT setval(pg_get_serial_sequence('"PatrolPaths"', 'Id'), 422);
SELECT setval(pg_get_serial_sequence('"ConversationScreens"', 'Id'), 2067);
SELECT setval(pg_get_serial_sequence('"PatrolNodes"', 'Id'), 2460);
SELECT setval(pg_get_serial_sequence('"ConversationScreenParameters"', 'Id'), 19360);
COMMIT;
