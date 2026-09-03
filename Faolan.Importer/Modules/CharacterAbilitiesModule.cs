using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using Faolan.Importer.Cli;
using Faolan.Importer.Extractors;
using Faolan.Importer.Rdb;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Importer.Modules
{
	// Per-character ABILITY sets (0x2052) + COMBO books (ComboBookIIR) re-granted on login. Abilities and combos are
	// NOT in the snapshot known-list, so retail re-sends them on every world entry; this persists what to replay (the
	// action bar is only shortcuts, never the grant source). Needs characters (attribution) + abilities (the catalog
	// the ability grants resolve against).
	public sealed class CharacterAbilitiesModule : IImportModule
	{
		// CharacterStateModule too: the combo pass reads the imported action bars (a type-3 shortcut is a combo), which
		// is the only per-character combo evidence for a character past the create flow.
		public IReadOnlyList<string> DependsOn => [nameof(CharactersModule), nameof(AbilitiesModule), nameof(CharacterStateModule)];

		public IReadOnlySet<WriteMode> SupportedModes => new HashSet<WriteMode>
		{
			WriteMode.Insert,
			WriteMode.Upsert
		};

		public ImportSource Source => ImportSource.Captures;

		public string Description => "Per-character ability sets (0x2052) + combo books (ComboBookIIR + action-bar combos).";
		public string Name => "character-abilities";

		public async Task<ImportResult> RunAsync(ImportContext ctx)
		{
			var (aChars, aRows) = await CaptureAbilityImporter.ImportCharacterAbilitiesAsync(ctx.Db, ctx.RawPackets);
			var (cChars, cRows) = await CaptureAbilityImporter.ImportCharacterCombosAsync(ctx.Db, ctx.RawPackets);
			// The class<->ability access map (ClassAbility), from two sources. FIRST the CREATE captures: all 12 classes
			// have a create dump and all 12 resolve, so this covers the whole roster with each class's level-1 kit.
			// THEN the per-character grants, which can only see the four classes we imported a character for but do
			// carry their real gameplay levels. Creates-first because the character pass only inserts what it can see:
			// on its own it left DarkTemplar with ZERO abilities (its create dump has 14) and seven other classes
			// holding rows derived from characters that no longer exist - unreproducible from a clean rebuild (#296).
			var (createInserted, _) = await CaptureAbilityImporter.DeriveClassAbilitiesFromCreatesAsync(ctx.Db, ctx.CreateCaptures);
			var (caInserted, _) = await CaptureAbilityImporter.DeriveClassAbilitiesAsync(ctx.Db);
			// LAST, because it reads what the two passes above wrote: the corpus has a levelled character for only four
			// of the twelve classes, so an ability every one of them earned (Search, Hide, Path of Asura) would
			// otherwise be withheld from the other eight - a hole in the CAPTURES served to the client as a rule (#303).
			var (universalInserted, _) = await CaptureAbilityImporter.DeriveUniversalLevelledAbilitiesAsync(ctx.Db);
			caInserted += createInserted + universalInserted;
			// ClassCombo from two sources, mirroring the abilities above. The catalog's capture-validated class-name
			// prefixes (e.g. Herald_ = HoX, confirmed by Hakotep's 0x2076 combo book) give each class its own lines -
			// plus, since #581, the ARCHETYPE-shared ones (`Soldier_`/`Rogue_`/`Priest_`) fanned out to that
			// archetype's three classes; the create dumps give the UNIVERSAL kit those prefixes can't express, because
			// it is named by RACE (Cimmerian_/Khitan_/Stygian_/Aquilonian_Dance) yet granted to every class alike (#416).
			var (ccInserted, _) = await CaptureAbilityImporter.DeriveClassCombosAsync(ctx.Db);
			var (ucInserted, _) = await CaptureAbilityImporter.DeriveUniversalCombosFromCreatesAsync(ctx.Db, ctx.CreateCaptures);
			ccInserted += ucInserted;
			return new ImportResult($"{aRows} ability rows ({aChars} chars), {cRows} combo rows ({cChars} chars); {caInserted} ClassAbility, {ccInserted} ClassCombo rows.");
		}
	}
}
