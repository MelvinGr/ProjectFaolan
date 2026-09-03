using System;
using System.Collections.Concurrent;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Constants;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.CreateFlow
{
	// Committing a new character's NAME - the forbidden-word/length filter (RDB 1065100, stored hashed), the
	// uniqueness check, and on success the one-time seed of everything a character is born with (spawn scaffold,
	// starter equipment, bind point) plus the accept / reject replies the create editor waits for. Off
	// CreateFlowService (#928 item 8): it is the only part of the flow that touches the database, and it is testable
	// without a client script running.
	public sealed class CharacterCreationService(IDatabaseRepository database, ILogger logger)
	{
		private readonly IDatabaseRepository _database = database;
		private readonly ILogger _logger = logger;

		// The outcome of committing a chosen name, mirroring the retail create-editor's NicknameOk / NicknameTaken /
		// NicknameInvalid callbacks (Captures/Stricken_soldier-guardian_create_random-actions: Stricken->taken,
		// empty->invalid, Strickenen->ok).
		public enum NameCommitResult
		{
			Committed,
			Taken,
			Invalid
		}

		// Set on first use by LoadNameFilter; see there.
		private (IReadOnlySet<string> Hashes, IReadOnlySet<int> Lengths)? _nameFilter;

		public async Task<NameCommitResult> CommitCharacterName(INetworkClient client, string? name)
		{
			var character = client.Session.Character;
			if (character == null || string.IsNullOrWhiteSpace(name))
				return NameCommitResult.Invalid;

			if (string.Equals(character.Name, name, StringComparison.Ordinal))
				return NameCommitResult.Committed;

			// Retail's own rules, in the order the client applies them: 4..16 characters (the client states that
			// number itself and enforces it locally, so this only ever fires for a modified client), then the
			// client's shipped forbidden-word list (#884). A server whose database has no imported list runs the
			// length rule alone - the absence is LOGGED once by LoadNameFilter rather than passing silently.
			var (forbidden, lengths) = await LoadNameFilter();
			var verdict = CharacterNameRules.Check(name, forbidden, lengths);
			if (verdict != CharacterNameRules.Verdict.Ok)
			{
				_logger.LogInformation("Character-editor: name rejected as {Verdict}.", verdict);
				return NameCommitResult.Invalid;
			}

			// Check the name is free BEFORE saving (Characters.Name is UNIQUE) so a taken name is rejected cleanly as
			// NicknameTaken rather than blowing up the SaveCharacter unit of work. A row owned by THIS character doesn't
			// count (idempotent re-name to the same value is handled above).
			var existing = await _database.GetCharacterByName(name, character.RealmId);
			if (existing != null && existing.Id != character.Id)
			{
				_logger.LogInformation("Character-editor: name '{Name}' already taken by character {OwnerId:X8}; rejecting.", name, existing.Id);
				return NameCommitResult.Taken;
			}

			character.Name = name;
			// Nothing to regenerate: vitals are DERIVED from (Class, Level) at read time, so the class chosen in the
			// final editor step is reflected the moment it is stored - there is no level-1 rogue baseline left to
			// replace (#578).
			// Persist the whole accumulated editor graph (name + sex/race/morphs Body) as one unit of work.
			await _database.SaveCharacter(character);

			// Seed the spawn scaffold and the starter gear HERE, the first moment either can be right: both are
			// generated from the class (and the scaffold also from the editor body), and those are only known once the
			// editor's final step commits. (Not at row allocation - CreateCharacter hands out a placeholder Cimmerian
			// barbarian with no body, which would bake the wrong skill list, appearance and weapon into every
			// character.) Doing it at creation makes "a character has scaffold rows and its starting gear" an
			// invariant, so world entry is a pure read.
			await SeedSpawnScaffold(character);
			await SeedStarterEquipment(character);
			await CharacterLoadout.Seed(_database, _logger, character);
			await SeedBind(character);

			// Grant the beach starter quest (10238) ONCE, here at character creation - retail auto-grants it on first
			// beach entry; we persist it now so the journal is data-driven from CharacterQuests (delivered live on the
			// create-world entry, then riding the snapshot on later logins) instead of baked into the snapshot. It is
			// NOT re-seeded on world entry, so a completed/removed quest never reappears.
			await _database.SetCharacterQuest(character.Id, QuestContent.BeachStarterQuestId, QuestState.Active);
			_logger.LogInformation("Character-editor: committed character name '{Name}' for character {CharacterId:X8}; granted starter quest {QuestId}.", name, character.Id,
				QuestContent.BeachStarterQuestId);
			return NameCommitResult.Committed;
		}

		// The client's forbidden-word list, loaded ONCE per process. It is ~200 short hashes and it changes only when
		// the importer re-reads the client, so re-querying it per name typed in the editor would be pure noise.
		private async Task<(IReadOnlySet<string> Hashes, IReadOnlySet<int> Lengths)> LoadNameFilter()
		{
			if (_nameFilter != null)
				return _nameFilter.Value;

			var terms = await _database.GetNameFilterTerms();
			_nameFilter = (terms.Select(t => t.TermHash).ToHashSet(StringComparer.Ordinal), terms.Select(t => t.Length).ToHashSet());

			// An empty table is a real state (the RDB module is opt-in and needs the client install), and it changes
			// what this server enforces - so say so rather than letting a name filter that filters nothing look
			// like one that found nothing to filter.
			if (terms.Length == 0)
				_logger.LogInformation("Character-editor: no name-filter terms imported (`--only rdb-name-filter`); only the 4..16 length rule applies.");
			else
				_logger.LogInformation("Character-editor: {Count} name-filter terms loaded.", terms.Length);

			return _nameFilter.Value;
		}


		// Reject a chosen name so the client re-prompts instead of dead-ending (no loading screen). Retail answers a
		// bad TheNameIs with the same 0xA36D3B74 callback as NicknameOk but a different token: NicknameTaken (already in
		// use, echoes the name) or NicknameInvalid (empty/unsaveable, empty value). Verbatim grammar from
		// Captures/Stricken_soldier-guardian_create_random-actions (frames 151 taken / 158 invalid / 193 ok). The client
		// then simply re-sends TheNameIs with a new name - no other packets are needed.
		public static Task SendNicknameRejected(INetworkClient client, string name, bool taken)
		{
			return taken
				? CreateFlowWire.SendA36DCallback(client, "NicknameTaken", name)
				: CreateFlowWire.SendA36DCallback(client, "NicknameInvalid", "");
		}

		// Seeds the fresh-character spawn-scaffold rows onto `character` and persists them. No-op for a character that
		// already has them (TrySeedFreshRows says so), so a re-commit costs nothing.
		//
		// Deliberately NOT wrapped in a catch. "Loud but not fatal" would be a lie here: PlayerSpawnBuilder throws
		// without these rows, so a character committed without them is one that fails at every world entry and every
		// spawn broadcast, forever - a create that half-succeeded is worse than one that visibly failed.
		private async Task SeedSpawnScaffold(Character character)
		{
			// Creation is the one place the character's editor choices are worth checking against the catalog: from
			// here on they are resolved silently on every spawn, so an unimportable ref would show up as a wrong face
			// and nothing else.
			await ReportUnresolvableAppearance(character);

			if (!PlayerSpawnScaffoldRows.TrySeedFreshRows(character))
				return;

			await _database.SaveCharacterScaffold(character);
			_logger.LogInformation("Seeded fresh spawn-scaffold rows for character {CharacterId:X8}.", character.Id);
		}

		// Dresses a newly created character in the #220 starting gear: the manacles both sexes wear, the sex-specific
		// slave outfit (male loincloth / female gown + bottom, colour randomised per character as retail does) and the
		// class's improvised weapon.
		//
		// Keyed on the character being UNDRESSED - having no worn items at all. This used to run on first world entry
		// keyed on the CharacterEquipment ROW being absent, and silently stopped working the moment that row started
		// being created with the character (ce580410): the row always existed, so the seeding branch became unreachable
		// and new characters entered the world naked. It then keyed on the SLOTS being empty; since #750 a naked
		// character simply has no Equipped items, so there is only one thing left to check.
		private async Task SeedStarterEquipment(Character character)
		{
			// Only ever dress an UNDRESSED character. Re-filling emptied slots later would undo the player's own
			// unequips, which is why this cannot simply run on every login.
			if ((await _database.GetEquippedItems(character.Id)).Count > 0)
				return;

			var female = character.Sex == Sex.Female;
			var femaleOutfit = StartingData.StartingFemaleOutfits[Random.Shared.Next(StartingData.StartingFemaleOutfits.Length)];

			// A null template is a slot this sex/class does not get (a male gets no gown, a class the create dumps do
			// not cover gets no weapon), so the slot is simply left unworn.
			var gear = new (CharacterEquipmentSlot Slot, uint? Template)[]
			{
				(CharacterEquipmentSlot.Wrist, StartingData.StartingWristItem),
				(CharacterEquipmentSlot.Chest, female
					? femaleOutfit.Gown
					: null),
				(CharacterEquipmentSlot.Legs, female
					? femaleOutfit.Bottom
					: StartingData.StartingMaleLegsItem),
				// Class-specific improvised weapon (main-hand-left); only the classes the create dumps cover.
				(CharacterEquipmentSlot.MainWeapon, StartingData.StartingWeapon.TryGetValue(character.Class, out var weaponId)
					? weaponId
					: null)
			};

			foreach (var (slot, template) in gear)
			{
				if (template is { } id && await _database.GetItem(id) != null)
					await _database.SetCharacterItemSlot(character.Id, ContainerKind.Equipped, (int)slot, id);
			}

			_logger.LogInformation("Dressed new character {CharacterId:X8} ({Class}, {Sex}) in its starting gear.", character.Id, character.Class, character.Sex);
		}

		// Binds the new character to its starting map's default respawn point, so bind recalls (the Path of Asura spell,
		// .recall) and death-resurrect have somewhere to send it from the very first login. The bind moves after this
		// only when the player uses a bindstone/Wayfarer. The Totem of Origins does NOT read the bind - it goes to the
		// racial home city (StartingData.RacialHomeMap).
		//
		// No bind packet is sent here: the "You are now bound here" banner (#346) belongs to a bindstone USE, and this
		// connection is still the character editor. The client learns the bind with the rest of its state at world
		// entry.
		private async Task SeedBind(Character character)
		{
			var spawn = await _database.GetDefaultSpawn(character.MapId);
			if (spawn == null)
			{
				// Not fatal, and not silent: a map with no respawn point is a content gap, not corruption - the
				// character simply has nowhere to recall to until one is imported for that map.
				_logger.LogWarning("Bind: map {Map} has no default respawn point, so {CharacterId:X8} starts unbound.", character.MapId, character.Id);
				return;
			}

			character.BindRespawnPointId = spawn.Id;
			await _database.SaveCharacterBind(character.Id, spawn.Id);
			_logger.LogInformation("Bind: {Name} bound to respawn point {Point} on map {Map} at creation.", character.Name, spawn.Id, character.MapId);
		}

		// Says out loud when the character's own editor appearance choices will NOT reach its spawn. The spawn resolves
		// them itself, on every send (PlayerSpawnScaffoldRows.BuildWeightedEntries); this runs once, at creation,
		// because that is where a gap is still actionable and where saying it once is not log spam.
		private async Task ReportUnresolvableAppearance(Character character)
		{
			if (character.BodyResources is not { Count: > 0 })
				return;

			var registry = await _database.GetMorphTargetRegistry();

			// A ref the registry does not know is a GAP: MorphTargets ships in the seed (1072 rows), so an unknown ref
			// means the editor sent something we never imported. It is checked HERE, directly, rather than by asking
			// BuildWeightedEntries - which no longer answers "could not build" with a default face but by throwing, and
			// throwing at creation would fail the create over a cosmetic gap. Saying it once, here, is what makes the
			// gap actionable before the character's every future spawn hits it.
			var unresolved = character.BodyResources.Select(r => r.Ref).Where(r => !registry.ContainsKey(r)).ToArray();
			if (unresolved.Length > 0)
			{
				_logger.LogError("Character {CharacterId:X8}: appearance resource(s) {Refs} are not in MorphTargets, so its spawn cannot be built at all. Import them.",
					character.Id, string.Join(", ", unresolved));
			}
		}
	}
}
