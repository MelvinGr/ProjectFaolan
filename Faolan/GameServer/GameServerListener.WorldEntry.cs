using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private const uint MountAllowedFlag = 0x1; // Map.PlayfieldFlags bit 0: the playfield permits mounts

		// Suppress the auto-opening item-shop browser (itemshopbrowser_swf) on world entry. The client runs level/login
		// milestone Lua from its OWN RDB LuaArchive that calls player:OpenSiteInBrowser("http://aocshop.funcom.com/
		// showitem?...") - a Funcom-store upsell - which it renders in the itemshopbrowser SWF. Each open is gated on
		// `not player:HasTag(X)` (a "this bundle was already shown" flag) plus the PvPSagaBundles game tweak. No tweak
		// value skips them all (scripts 396 and 2240 open a bundle whether the tweak reads <1 OR >0), so the only
		// universal suppressor is the tag gate - and the client learns a character's tags from the FullChar snapshot we
		// send at entry. Pre-seeding the "already shown" flag for every shop-open branch makes HasTag(X) true, so the
		// browser never opens. The gating tag ids were recovered from the RDB shop scripts (393/395/396/2239/2240/2287-
		// 2298): 6617-6620 are the i<1 bundles, 7316 + 7416-7421 the i>0 bundles. Purely cosmetic "seen" flags - safe to
		// force on a private server that has no Funcom store.
		// TODO: Confirm
		private static readonly uint[] ItemShopSuppressTags = []; //6617, 6618, 6619, 6620, 7316, 7416, 7417, 7418, 7419, 7420, 7421];

		// The burst-less world-object packets: each WorldObjects row built via WorldObjectBuilder and framed. Built on
		// the row's captured handle + the live playfield instance (the rewrite the live splice path applies).
		private IEnumerable<Rpc> BuildBurstlessWorldObjects(INetworkClient client, IReadOnlyList<WorldObject> rows, uint playfieldInstance, IReadOnlySet<uint> activeQuestIds)
		{
			foreach (var row in rows)
			{
				// #262: quest-GATED objects (GateQuestId set) spawn only for a player holding that quest active - the
				// world-object analogue of an NPC's gating (but the object simply isn't sent; it doesn't spawn). Ungated
				// objects (the default, incl. the bricks for now) always spawn.
				if (!row.SpawnsFor(activeQuestIds))
					continue;

				yield return FrameWorldEntryRecord(client, WorldObjectBuilder.Build(row, row.CapturedHandle, playfieldInstance));
			}
		}

		// Loads the character's CharacterEquipment (creating it and seeding default starter gear if absent), with the
		// item navs included so they can be built into the snapshot. Beach characters only.
		private async Task<CharacterEquipment?> EnsureBeachStarterEquipment(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var equipment = await Database.GetCharacterEquipment(character.Id);
				if (equipment != null)
					return equipment;

				// Seed the default starter gear ONLY for a brand-new equipment row. Re-filling emptied slots on every
				// login would undo the player's own unequips (which persistence writes back), so an existing row is
				// left exactly as the player left it. Sex-specific slave outfit (#220): both sexes wear the manacles;
				// males add a loincloth (legs), females a gown (chest) + bottom (legs). See the Starter* constants.
				var female = character.Sex == Sex.Female;
				// Retail randomises the female slave-outfit colour per character (males always get the loincloth).
				var femaleOutfit = StartingData.StartingFemaleOutfits[Random.Shared.Next(StartingData.StartingFemaleOutfits.Length)];
				equipment = new CharacterEquipment
				{
					CharacterId = character.Id,
					Wrist = await Database.GetItem(StartingData.StartingWristItem),
					Chest = female
						? await Database.GetItem(femaleOutfit.Gown)
						: null,
					Legs = await Database.GetItem(female
						? femaleOutfit.Bottom
						: StartingData.StartingMaleLegsItem), // Class-specific improvised weapon (main-hand-left); only the classes the create dumps cover.
					MainHandLeft = StartingData.StartingWeapon.TryGetValue(character.Class, out var weaponId)
						? await Database.GetItem(weaponId)
						: null
				};
				await Database.SaveCharacterEquipment(equipment);

				return equipment;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed CharacterEquipment for {CharacterId}; sending inventory verbatim.", character.Id);
				return null;
			}
		}

		// Bind the character to the map's default respawn point if it has never bound (BindRespawnPointId == null).
		// Runs once per character - an established bind is left alone (it moves only via a bindstone/Wayfarer, not every
		// login). Without this, imported/older characters have no bind and every recall (Totem of Origin, Path of Asura,
		// .recall, death-resurrect) fails. No-op when the map defines no respawn point (nothing to bind to).
		private async Task EnsureCharacterBound(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null || character.HasBind)
				return;

			var spawn = await Database.GetDefaultSpawn(character.MapId);
			if (spawn == null)
				return;

			character.BindRespawnPointId = spawn.Id;
			await Database.SaveCharacterBind(character.Id, spawn.Id);
			Logger.LogInformation("Bind: {Char} auto-bound to respawn point {Point} on map {Map} (first entry).", character.Name, spawn.Id, character.MapId);
		}

		// Ensures the character's LOADOUT on EVERY world entry (#74/#71), not just at creation:
		//  (1) ALWAYS makes the class's spells + abilities KNOWN to the client (the client keeps no persistent kit), 		//      so the spellbook fills and every action-bar slot - default OR imported - resolves to an icon; also
		//      grants whatever spells/abilities the character's existing bar already references.
		//  (2) Seeds the DEFAULT bar layout ONLY when the character has no bar yet (a fresh character), mirroring the
		//      retail create-flow beach loadout - a customised or imported bar is left exactly as it is.
		// Runs at the start of world entry, before GrantCharacterSpells (which re-announces the spells this session);
		// the bar itself restores to the client via ShortcutBarReply.
		private async Task EnsureCharacterLoadout(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			const uint shortcutTypeAbility = 4;
			const uint shortcutTypeSpell = 2;
			const uint specialBarBase = 0x4E20;
			const uint secondBarBase = 100; // page 2 / "bar 1" (slot = bar position, page 2 = +100; see ShortcutBarBuilder)

			// Seed the class's spells ONLY for a character whose spellbook is EMPTY (a freshly-created one). An
			// imported character already carries its REAL spellbook from the capture's snapshot known-list, so
			// re-seeding it would only duplicate work and could reintroduce ids that character never had.
			//
			// NB the known-list is NOT a "highest rank only" set - it carries EVERY rank (NecroL80's imported book has
			// Pestilential Blast R1-R6, Ice Strike R1-R6, ...), which is correct: all ranks are learned and visible,
			// and showing only the top rank is a CLIENT-side UI option. Never filter a grant to a highest-rank set.
			// The abilities below are still granted every login (they're never in the snapshot known-list, and the
			// client keeps no persistent kit), and the bar's own spells are ensured resolvable further down.
			var spells = (await Database.GetCharacterSpells(character.Id)).Length == 0
				? await Database.GetStartingSpells(character.Level, character.Class)
				: [];
			foreach (var spellId in spells)
				await Database.AddCharacterSpell(character.Id, spellId);

			// The character's ABILITIES are its authoritative per-character set (CharacterAbility, imported from the
			// retail 0x2052 grants) - NOT the bar (the bar is only shortcuts). A freshly-created character with no set
			// gets the class-starting kit. We resolve them here ONLY to seed the default bar layout below; the actual
			// 0x2052 WIRE grants (+ the combo book) are NOT sent here. Retail sends them AFTER the FullChar snapshot and
			// after 0x2024 ReadyForPlayScreen (faolan necro login capture: abilities at frame 192, snapshot at 81), 			// driven by the 0x205f post-ready flush (FlushPendingPost205FFollowUp -> GrantCharacterAbilities/Combos).
			// Granting them pre-snapshot (the old behaviour) had the snapshot reconstruction discard them (abilities are
			// NOT in the snapshot known-list, unlike spells), so the abilities tab + ability-slot shortcuts came up empty.
			var abilities = await GetLoadoutAbilities(character);

			// The BAR layout is only SEEDED when the character has none. An imported or player-customised bar is left
			// exactly as it is. NB the bar is only SHORTCUTS - never the source of truth for what the character KNOWS
			// (that is the snapshot known-list -> CharacterSpells, plus CharacterAbility / CharacterCombo re-granted on
			// login); we never add a bar target to those sets (it "learned" a spell/ability twice when the bar
			// referenced a different id than the known set for the same thing - e.g. two "Quell the Ether (Rank 5)" ids).
			var existingShortcuts = await Database.GetCharacterShortcuts(character.Id);

			if (existingShortcuts.Length > 0)
			{
				Logger.LogInformation("EnsureCharacterLoadout: granted {S} spells + {A} abilities to '{Name}' (class={Class}); kept its existing {N}-slot bar.", spells.Length, abilities.Length,
					character.Name, character.Class, existingShortcuts.Length);
				return;
			}

			// Default bar layout: the player's primary visible action bar is bar 1 (the +100 page), so the full usable
			// kit lands there - the 3 Normal Attacks (left/middle/right) on bar 1 slots 100,101,102, then the class
			// SPELLS right after them on bar 1 (103+). The other starting abilities (toggle-combat / switch-weapon /
			// utility) go on the special bar (0x4E20+). Bar 0 is left empty so the attacks/spells are not split across
			// two bars. The 3 Normal Attacks come back ordered by Ability.Slot, but retail lays them out on bar 1 with
			// the 2nd and 3rd (bar slots 101/102) the other way round, so swap that pair before placing them (#285).
			var normalAttacks = abilities.Where(a => a.Id is >= 0xBBC40 and <= 0xBBC60).ToList();
			if (normalAttacks.Count >= 3)
				(normalAttacks[1], normalAttacks[2]) = (normalAttacks[2], normalAttacks[1]);

			var secondBarSlot = secondBarBase;
			foreach (var attack in normalAttacks)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeAbility, attack.Id);

			var specialSlot = specialBarBase;
			foreach (var ability in abilities.Where(a => a.Id is < 0xBBC40 or > 0xBBC60))
				await Database.SetCharacterShortcut(character.Id, specialSlot++, shortcutTypeAbility, ability.Id);

			// Spells go on bar 1 (the +100 page), after the page-2 normal attacks - matching retail (Herald 0x3006FF at
			// slot 103), so the player sees them on their primary visible bar rather than bar 0. ONE per spell family
			// (highest known rank) so the bar shows distinct icons, not every rank (Ice Strike R1/R2/R3 share an icon, 			// which made a leveled caster's bar look like it just repeated). All ranks are still learned above.
			var barSpells = await Database.GetStartingBarSpells(character.Level, character.Class);
			foreach (var spellId in barSpells)
				await Database.SetCharacterShortcut(character.Id, secondBarSlot++, shortcutTypeSpell, spellId);

			Logger.LogInformation("EnsureCharacterLoadout: seeded default bar for '{Name}' (class={Class}): {A} abilities + {S} spells (#74/#71).", character.Name, character.Class, abilities.Length,
				spells.Length);
		}

		// Loads the character's spawn-scaffold rows, seeding the fresh-character defaults once for characters that
		// have none (like EnsureBeachStarterEquipment seeds starter gear). Returns null on failure - the spawn
		// then keeps the captured scaffold frame.
		private async Task<Character?> EnsureSpawnScaffoldRows(Character character)
		{
			if (character == null)
				return null;

			try
			{
				var scaffoldCharacter = await Database.GetCharacterScaffold(character.Id) ?? character;

				// Resolve the character's chosen appearance resources (editor SetMorphValue refs) to the
				// weighted entry tags via the imported client registry; null (nothing stored / registry not
				// imported) falls back to the Dikketest defaults inside EnsureFreshRows.
				List<ScaffoldEntry>? weighted = null;
				if (scaffoldCharacter.BodyResources is { Count: > 0 })
				{
					var registry = await Database.GetMorphTargets(scaffoldCharacter.BodyResources.Select(r => r.Ref).ToArray());
					weighted = PlayerSpawnScaffoldRows.BuildWeightedEntries(scaffoldCharacter.BodyResources, registry);
					if (weighted == null)
					{
						Logger.LogWarning(
							"Character {CharacterId} has {Count} chosen appearance resources but none resolved via MorphTargets (registry imported?); using the default weighted entries.",
							character.Id, scaffoldCharacter.BodyResources.Count);
					}
				}

				// Seeds a fresh scaffold on first login; an existing one is left as-is. Propagating a later appearance
				// EDIT into the scaffold is done at save-time by Faolan.Web (PropagateToScaffold, #290), so there's no
				// login-time refresh here.
				if (!PlayerSpawnScaffoldRows.EnsureFreshRows(scaffoldCharacter, weighted))
					return scaffoldCharacter;

				await Database.SaveCharacterScaffold(scaffoldCharacter);
				Logger.LogInformation("Seeded fresh spawn-scaffold rows for character {CharacterId}.", character.Id);
				return scaffoldCharacter;
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Failed to load/seed spawn-scaffold rows for {CharacterId}; keeping the captured scaffold frame.", character.Id);
				return null;
			}
		}

		private async Task FlushPendingPost205FFollowUp(INetworkClient client)
		{
			if (!client.Session.HasPendingPost205FFollowUp)
				return;

			if (client.Session.IsUnnamedCreatePhase)
			{
				Logger.LogInformation("New-character post-0x205F follow-up remains pending on 0x2020; waiting for the retail create handoff instead of flushing early.");
				return;
			}

			// Past the early return the character is always named: the create-editor connection returned above (it
			// flushes via the retail create handoff, not here), so every connection that reaches this point is a
			// Login or a TeleportReentry - both of which want the full existing-character burst below. This is the
			// cleanup the old "Remove ...Name != null checks below" TODO asked for, now that the early return is in
			// place: the create-vs-named conditionals that used to guard this body were all unreachable dead code.
			Logger.LogInformation("Flushing pending post-0x205F follow-up on this 0x2020.");
			await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType),
				GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000Opcodes.StatIIR)
					.WriteUInt32(EntityClass.Character)
					.WriteUInt32(client.Session.Character.Id)
					.WriteByte(0x01)
					.WriteUInt32(0x00000013)
					.WriteUInt32(0x00000000))));
			Logger.LogInformation("Sent deferred (uint)GameServer0X2000Opcodes.StatIIR prompt.");

			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType),
				GameCharInterfaceOpcodes.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(client.Session.Character.Id).WriteByte(0)));

			// Retail sends 0x2024 ReadyForPlayScreen right after the spawn handshake (0x2076) - the "leave the
			// loading screen and enter the world" signal. We were sending the manifest but never this, so the client
			// finished the handshake (0x2020 -> 0x2008 -> 0x2076) and then sat on the loading screen forever.
			// Confirmed by diffing a 4010 login against the Strie dump: 0x2024 ours=0 / dump=1.
			Logger.LogInformation("Existing-character login: sending 0x2024 ReadyForPlayScreen after the spawn handshake.");
			await SendReadyForPlayScreen(client);
			// Retail grants the starting abilities (0x2052) + combo book right after 0x2024, not during world entry
			// (faolan necro login capture: abilities at frame 192, just after ReadyForPlayScreen). Both are granted
			// here, post-snapshot: neither rides the FullChar snapshot known-list (spells do), so a pre-snapshot grant
			// is discarded by the client's identity reconstruction.
			await GrantCharacterAbilities(client);
			await GrantCharacterCombos(client);
			// Re-seat the persisted mount if the player was riding one and this map allows mounts. Runs on login
			// AND on every teleport/changemap re-entry (this flush is re-armed on both), so riding into a no-mount
			// area dismounts and riding back re-mounts, and a relog restores the mount.
			await RestoreMount(client);
			// Re-apply the persisted self-buffs with their remaining time (#274) - the buff counterpart to RestoreMount.
			// Runs BEFORE SendShieldOfTheRisen (which owns the shield buffs, excluded from persistence) so ordering is
			// moot; the mount ride buff is likewise excluded and owned by RestoreMount above.
			await RestoreBuffs(client);
			// NB the learned SPELLS (DispatchLearnSpell 0x200A) are NOT re-granted here: they ride the FullChar
			// snapshot's known-spell list (#74/#71) so the client already knows them when it enters the world, and
			// the pre-snapshot GrantCharacterSpells re-send is absorbed silently. (The old PendingSpellLearns
			// post-ready re-announce - needed only while the snapshot carried an empty known-list - re-toasted "You
			// have learned a new spell" on every login; the known-list makes it redundant.)
			// Shield of the Risen (#221): re-applies the persistent protection self-buff every character carries.
			// This flush is also re-armed on teleport/changemap re-entry, so the buff is re-asserted on each
			// playfield change too (on every map change / login). RESOLVED 2026-07-11: the old id
			// 0x000B9000 was the wrong spell (rendered as the "Exhausted" debuff, no DB row -> never cleared).
			// The capture-proven ids are bases 4061768 (1h) + 3666828 (10h) - both in Spells with real durations,
			// so #274 arms a matching expiry and they clear on schedule. Rendered top-of-screen by the client.
			await SendShieldOfTheRisen(client);

			// #348: the area banner is now driven by the world tick's sub-area crossing check (WorldSimulation
			// .TickAreaBanners) - entering a playfield lands the player inside an area and that IS the first
			// crossing, so it banners without a special case here. Clearing the live area makes sure the crossing
			// fires again after a teleport/changemap back into an area the player was already standing in.
			if (_simulation.PlayerWorldEntity(client) is { } areaEntity)
				areaEntity.CurrentAreaNameId = 0;

			// The post-0x2024 login record stream (world state / quest / UI) retail sends right after ReadyForPlayScreen
			// + abilities - the records that let the client leave the loading screen. Rebuilt from the Strie 4010 login
			// capture as explicit PacketStream sends: the live player instance is written inline (the receiver is built via
			// Endpoints.EndpointBytes(type, instance), WriteUInt32(instance) where the body carries it), so no ids are
			// hard-coded. The remaining body
			// bytes are opaque captured payloads (quest/UI blobs). Per-map/DB-driven construction (like the rest of world
			// content) is #352 - blocked on decoding these blobs.
			// #304: the legacy verbatim beach NPC records (spawns/MoveTasks/despawns/BehaviorCC on captured handles) were
			// deleted - WorldBroadcaster interest streaming owns the beach population, confirmed in-client.

			var instance = client.Session.Character.Id;

			// "New claims available" glow (CoreClaimIIR sub 0x0009) - pushed ONLY when the account has unseen claims,
			// matching retail (present on a fresh login with new claims, absent once viewed). Previously this fired
			// unconditionally, so the claims button lit up for every account; see GameServerListener.Claims.cs.
			await _claims.SendNewClaimsGlowIfAny(client);

			// record 0: 0x00002000, 17-byte body
			await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance),
				GameInterfaceOpcodes.DispatchPassBlob,
				b => b.WriteArray(0x00, 0x00, 0x00, 0x0D, 0x86, 0x4C, 0xFE, 0xF8, 0x00, 0x00, 0xC3, 0x50).WriteUInt32(instance).WriteArray(0x00)));

			// record 1: 0x0000200C, 1-byte body
			await client.SendAsync(new Rpc(ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(Endpoints.GameCharInterfaceProjectType, instance),
				GameInterfaceOpcodes.DispatchReportServerID, (byte[])[0x01]));

			// record 2: 0x00002000 - the StartFixed3d destiny step (ScryScriptIIR 0xA36D3B74), constructed (was verbatim;
			// byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture). The body is [u32 len][step bytes].
			// #234: SKIP on the create-world connection - the create destiny cutscene overlay (SendCreateCutsceneStep)
			// already sends StartFixed3d at IntroDone (matching retail, which sends it ONCE there). Sending it here too
			// (before the cutscene) is a DUPLICATE retail doesn't have (loopback f649) and re-engages the cinematic.
			if (!client.Session.IsCreateWorldSession)
			{
				var startFixed3d = ScryScript.BuildStep(instance, "StartFixed3d");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance),
					GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(startFixed3d)));
			}

			// record 2b (BEACH ONLY, #267): the destiny SCRIPT GROUP that builds the chained-Casilda tableau - it
			// renders the `hangingwoman` chains and sets up the intro confinement ("invisible wall"). Retail sends
			// StartScriptGroupOnClient(385910) on EVERY Tortage-beach LOGIN, between StartFixed3d and WelcomeTutorial
			// (decode_scryscript.py on Ranochar/Stihetfv beach logins). We omitted it, which is why our beach chains
			// don't instantiate (the dynel is byte-exact + its mesh 347243 is in the RDB, but nothing runs the scene
			// script) and why the gate never resolves. Gated to the beach so a non-Tortage login isn't steered into
			// the intro destiny flow.

			// record 3: 0x00002000, 35-byte body - the GameCode script-message (script id 0x5F376, no name string, 			// unlike record 2 StartFixed3d / record 4 WelcomeTutorial) that triggers the on-screen TUTORIAL overlay.
			// DISABLED so the tutorial no longer pops on world entry; flip to true to restore it. (Note: this is NOT the
			// item-shop window - that one is opened client-side and persists even with the whole post-ready burst
			// removed, so it can't be suppressed from here.)
			var enableTutorialOverlay = false;
			// ReSharper disable once ConditionIsAlwaysTrueOrFalse
			if (enableTutorialOverlay)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance),
					GameInterfaceOpcodes.DispatchPassBlob,
					b => b.WriteArray(0x00, 0x00, 0x00, 0x1F, 0xA3, 0x6D, 0x3B, 0x74, 0x00, 0x00, 0xC3, 0x50)
						.WriteUInt32(instance)
						.WriteArray(0x00, 0x00, 0x00, 0x00, 0x0E, 0x08, 0x05, 0x10, 0x00, 0x18, 0xF6, 0xC6, 0x17, 0x32, 0x04, 0x08, 0x00, 0x10, 0x00)));

				// record 4: 0x00002000 - the WelcomeTutorial destiny step (ScryScriptIIR 0xA36D3B74), constructed (was
				// verbatim; byte-identical, proven by QuestTests.BuildScryStep_MatchesCapture).
				var welcomeTutorial = ScryScript.BuildStep(instance, "WelcomeTutorial");
				await client.SendAsync(new Rpc(WorldSenderEndpoint, Endpoints.Endpoint(Endpoints.GameInterfaceType, instance),
					GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(welcomeTutorial)));
			}

			// #262: apply any after-quest world-object effects up-front (e.g. a relog after freeing Casilda drops the
			// beach chain-wall collision, so the returning player isn't re-blocked by the world-entry burst's chain
			// dynel), and clear the escortee's captive pose if she's already freed. Both data-driven / no-op when N/A.
			await _broadcaster.ApplyQuestWorldEffects(client);
			await _questHandler.ClearCasildaCaptivePose(client);

			// Bind the character on entry if it has never bound. Every recall targets BindRespawnPointId - the Totem of
			// Origin, the Path of Asura spell, .recall, and the death-resurrect - so an unbound character (imported or
			// older chars, whose bind was never set) can't recall at all. Bind ONCE to the entry map's default respawn
			// point + persist; an established bind is left untouched (it only moves via a bindstone/Wayfarer, not login).
			await EnsureCharacterBound(client);

			Logger.LogInformation("Sent the post-0x2024 login records.");

			// #239/#304 RESOLVED 2026-07-05 - the class force-feed (SetStat 0x43 = Class) was REMOVED as proven
			// redundant, matching retail (which never sends stat 0x43 either). The in-world class is derived
			// CLIENT-SIDE from the char data we already deliver. Verified in-client
			// with the force-feed DISABLED on BOTH paths: an existing lvl80 login read stat 0x43 = Necromancer, // and a freshly-created char read stat 0x43 = HeraldOfXotli - both displayed correctly. The earlier
			// interim predated the client-derivation working; no server-side class stat is needed.

			// Re-deliver any persisted bag items the world-entry snapshot dropped for lacking an hash
			// (AH-bought items - harvested template, no hash) via the live add path, so they survive relog.
			await RedeliverNonSnapshotBagItems(client);

			// (The post-load second-snapshot delivery was removed 2026-06-12: the client ignores/rejects a
			// second 0x737A6DF9 for an already-constructed identity. Inventory rides the WORLD-ENTRY snapshot, // see BuildBurstlessSnapshotRoot.)

			// Wallet sync: retail does NOT push a 0x2005 at login (the initial balance presumably rides the
			// FullChar snapshot, location undecoded) - this is our interim so a moneyed character doesn't
			// show 0 until the first change. Reason 0 = a plain sync, not a gain celebration.
			if (client.Session.Character.Money > 0)
				await SendUpdateCash(client, client.Session.Character.Money, UpdateCashReason.Spend);

			// Guilded character: set the in-world guild tag (0x2550C99A). Retail sends this once on guild
			// JOIN; for logins the tag normally rides in the spawn data, which our DB spawn does not carry
			// yet, so re-assert it here - the client applies it the same way. (After the snapshot, like the
			// wallet sync - keep the burst -> snapshot gap clean.)
			if (client.Session.Character.GuildId is { } guildId)
			{
				var guild = await Database.GetGuild(guildId);
				if (guild != null)
					await client.SendAsync(BuildGuildTag(client, client.Session.Character.Id, guild.Name));
			}

			client.Session.HasPendingPost205FFollowUp = false;
			Logger.LogInformation("Cleared pending post-0x205F follow-up flag.");
		}

		// The abilities to grant a character on login: its authoritative per-character set (CharacterAbility, imported
		// from the retail 0x2052 grants) resolved to Ability rows, or the class-starting kit for a freshly-created
		// character with no persisted set. NOT derived from the bar (the bar is only shortcuts).
		private async Task<Ability[]> GetLoadoutAbilities(Character character)
		{
			var granted = await Database.GetCharacterAbilities(character.Id);
			if (granted.Length == 0)
				return await Database.GetClassAbilities(character.Level, character.Class);

			var abilities = new List<Ability>(granted.Length);
			foreach (var ga in granted)
			{
				var ability = await Database.GetAbility(ga.AbilityId);
				if (ability != null) // ids we do not have cataloged are skipped (that bar slot stays empty)
					abilities.Add(ability);
			}

			return [.. abilities];
		}

		// Grants the character's starting abilities so the player has castable attacks in-world (enables the combat
		// test, TODO #3). The set is the universal starting kit every captured character receives via 0x2052 on
		// login - the `Abilities` table, imported from the retail grants by CaptureAbilityImporter (3 Normal Attack
		// directions + toggle-combat / switch-weapon / utility). Sent on the spawn conversation (same family as the
		// 0x201c handshake), after the world has been built. (Class-specific SPELLS are a separate, not-yet-decoded
		// grant - no capture exercises it; see TODO #3.)
		private async Task GrantCharacterAbilities(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var abilities = await GetLoadoutAbilities(character);
			// Login is a SILENT re-grant: the abilities were already learned (in the create flow / on first learn), so
			// the announce flag is cleared - otherwise the client pops "You have learned a new ability" for the whole
			// kit on every login. Retail does exactly this: the create-flow grants carry the announce byte
			// 0x01, the per-login re-grants carry 0x00 (the ONLY differing byte; see BuildAddSpecialAbilityBody).
			foreach (var ability in abilities)
				await SendAddSpecialAbility(client, ability, false);

			Logger.LogInformation("GrantCharacterAbilities: re-granted {Count} abilities (silent) to '{Name}' (class={Class}, level={Level}).", abilities.Length, character.Name, character.Class,
				character.Level);
		}

		// Re-emits the character's COMBO book on login as ComboBookIIR grants - the combo counterpart to
		// GrantCharacterAbilities. The book = persisted CharacterCombo rows UNION the class's combos up to its level
		// from the class<->combo ACCESS map (ClassCombo); a MAX-LEVEL imported character never dings, so without this
		// its combo book would be empty. Not in the snapshot.
		private async Task GrantCharacterCombos(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var instance = character.Id;

			// The combo book = the persisted CharacterCombo rows UNION the class's combos up to its level, from the
			// class<->combo ACCESS map (ClassCombo). This replaces the old class-INDEPENDENT level-keyed catalog, which gave
			// every class the same level-keyed set - so a Necromancer (a caster with no weapon combos) re-"learned"
			// Sweep/Slam/Hell Strikes on every login. ClassCombo has NO rows for casters, and ships empty until
			// populated from dumps, so today this grants only the persisted CharacterCombo rows (the safe default).
			var comboIds = new HashSet<uint>();
			foreach (var combo in await Database.GetCharacterCombos(character.Id))
				comboIds.Add(combo.ComboId);
			foreach (var combo in await Database.GetClassCombos(character.Level, character.Class))
				comboIds.Add(combo.Id);

			foreach (var comboId in comboIds)
			{
				await client.SendAsync(new Rpc(WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType),
					GameInterfaceOpcodes.DispatchPassBlob, ComboBookIirCodec.Build(instance, comboId)));
			}

			if (comboIds.Count > 0)
				Logger.LogInformation("GrantCharacterCombos: emitted {Count} combos to '{Name}' (class={Class}, level {Level}).", comboIds.Count, character.Name, character.Class, character.Level);
		}

		// Re-grants the character's learned spells on login - the spell counterpart to GrantCharacterAbilities (#74).
		// Retail re-sends a caster's whole learned set via DispatchLearnSpell on every world entry (invariant across
		// every login capture: NecroL80 always relearns her 5, DemoL80 his 9); the client silently no-ops spells it
		// already knows, so this is safe to replay each login. The set is the character's CharacterSpells rows (seeded
		// for captured characters by CaptureSpellImporter, grown live by .learnspell). Sent after GrantCharacterAbilities.
		private async Task GrantCharacterSpells(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var spells = await Database.GetCharacterSpells(character.Id);
			foreach (var spell in spells)
				await SendLearnSpell(client, spell.SpellId);

			if (spells.Length > 0)
				Logger.LogInformation("GrantCharacterSpells: re-granted {Count} learned spells to '{Name}' (class={Class}).", spells.Length, character.Name, character.Class);
		}

		// The client's world-entry spawn-poll, arriving on wire 0x2020 (GameCharAgent method slot 0x20, RPC-catalog
		// label UpdateQuestMarkerNPC - the login/world flow reuses the opcode). Fires once per spawned entity the
		// client confirms it built (requestId = that entity's handle). Three jobs, all part of finishing world entry:
		//  (1) record the confirmed handle so interest only ever despawns handles the client actually built (#245 -
		//      a despawn for an unbuilt handle crashes a still-loading client: pcDynel == null);
		//  (2) answer with the 0x2008 SpawnCheck that completes/renders that entity (verified: the dikketest world
		//      conn polls Kalanthes 0x535A0 and gets a SpawnCheck for 0x535A0). Both existing-login and post-creation
		//      world entry need it - the create flow's old hand-built batch answered the wrong hardcoded capture
		//      handles, so its entities were never confirmed and the intro cinematic failed;
		//  (3) drive the ready-trigger: on a normal login retail arms the follow-up off the client's 0x205F and
		//      expects 0x2024 ReadyForPlayScreen right after this spawn handshake, so the flush lives HERE
		//      (capture-proven - moving it strands login on the loading screen). FlushPendingPost205FFollowUp no-ops
		//      unless armed, so a create-editor / post-ready poll just falls through.
		// Bodies shorter than the 16-byte poll are not spawn-polls (some other same-opcode traffic); just log them.
		private async Task HandleWorldEntrySpawnPoll(INetworkClient client, Rpc packet)
		{
			var body = packet.BodyReader();
			if (body.Length < sizeof(uint) * 4)
			{
				LogPacketBodyDebug("GameServer 0x2020 short body", body.ToArray());
				return;
			}

			var ack0 = body.ReadUInt32();
			var requestId = body.ReadUInt32();
			var ack1 = body.ReadUInt32();
			var characterId = body.ReadUInt32();

			_simulation.PlayerWorldEntity(client)?.Confirmed.Add(requestId);

			if (client.Session.WorldEntered || client.Session.IsCreateWorldSession)
				await SendSpawnCheckResponse(client, requestId);

			Logger.LogInformation("0x2020 summary: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X8}", ack0, requestId, ack1, characterId);
			Logger.LogInformation("0x2020 decoded: ack0={Ack0:X}, requestId={RequestId:X}, ack1={Ack1:X}, characterId={CharacterId:X}, pendingPost205F={PendingPost205F}", ack0, requestId, ack1,
				characterId, client.Session.HasPendingPost205FFollowUp);
			await FlushPendingPost205FFollowUp(client);
		}

		// Re-delivers, via the proven live DispatchAddItem (0x2020) path, the persisted BAG items the world-entry
		// snapshot DROPPED because they carry no hash (Hash == 0 - e.g. an AH-bought item whose template
		// we harvested from a listing, which has no hash). The snapshot only renders Hash != 0 rows
		// (FullPlayerCharacterBuilder), and embedding a half-identified item in the chat-critical 0x737A6DF9 root is
		// risky, so these ride the mid-session add-item mechanism (which renders by template / Hash 0) once at login.
		// Items WITH an hash already rode the snapshot, so this only touches the dropped ones (no duplication).
		private async Task RedeliverNonSnapshotBagItems(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var rows = await Database.GetCharacterInventory(character.Id);
			var pushed = 0;
			foreach (var row in rows.Where(r => r.Container == FullPlayerCharacterBuilder.BagKind && r.ItemId != null && r.Item is { Hash: 0 }))
			{
				var slot = (ulong)row.Slot;
				var entry = SnapshotItemBuilder.BuildInventoryEntry(FullPlayerCharacterBuilder.ItemId(character.Id, FullPlayerCharacterBuilder.BagKind, slot), row.Item!, 0,
				[
					new SnapshotItemBuilder.ItemStat(0, 1), new SnapshotItemBuilder.ItemStat(23, (uint)FullPlayerCharacterBuilder.RecordId(character.Id, FullPlayerCharacterBuilder.BagKind, slot)),
					new SnapshotItemBuilder.ItemStat(26, row.Count)
				], row.Item!.Level);
				await SendAddItem(client, ContainerKind.Bag, (uint)row.Slot, entry);
				pushed++;
			}

			if (pushed > 0)
				Logger.LogInformation("Re-delivered {Count} hash-less bag item(s) to '{Name}' via live add (not in the snapshot).", pushed, character.Name);
		}

		// Resolves the resurrection anchor advertised on the login 0x2024: the closest known / AlwaysKnown RespawnPoint on
		// the character's map (mirrors SendDeathScreen's resolution). Falls back to the universal Tortage Beach default
		// (Map.NewPlayerSpawnSpawnPoint) when the map defines no respawn point.
		private async Task<RespawnPoint> ResolveLoginResurrectionSpot(Character character)
		{
			var points = await Database.GetRespawnPoints(character.MapId);
			if (points.Length == 0)
				return Map.NewPlayerSpawnSpawnPoint;

			var discovered = new HashSet<uint>(await Database.GetDiscoveredRespawnPointIds(character.Id));
			return RespawnResolver.ClosestKnown(character.Position, points, discovered) ?? points.FirstOrDefault(p => p.AlwaysKnown) ?? points[0];
		}

		// Re-seat the character's persisted mount (Character.MountId) after world entry, when the map allows mounts.
		// Sends the ride buff + Mount/MountSpeed StatUpd and marks the live entity mounted - the same seat the
		// SummonMount channel ends with, minus the cast animation. No-op on foot, on a no-mount map (the client
		// auto-dismounted on entry), or if the stored id no longer resolves.
		private async Task RestoreMount(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character?.MountId is not { } mountId)
				return;

			var map = await Database.GetMap(character.MapId);
			if (((map?.PlayfieldFlags ?? 0) & MountAllowedFlag) == 0)
				return;

			var mount = MountCatalog.ForId(mountId);
			if (mount == null)
				return;

			var self = character.Id;
			await SendMagicBuffIir(client, self, self, MountCatalog.RideSpell, MountCatalog.RideBuffPayload());
			await SendStatUpdate(client, new CharacterStatBlock
			{
				[CharacterStatId.Mount] = mount.RideStat,
				[CharacterStatId.MountSpeed] = 100
			}, self);

			var entity = PlayerWorldEntity(client);
			entity?.IsMounted = true;

			Logger.LogInformation("Restored mount {Mount} for {Name} on map {Map}.", mount.CreatureName, character.Name, character.MapId);
		}

		// Builds + sends the entire world entry from generated/structured pieces - no captured burst (#210). Mirrors
		// SendWorldEntry's world-model setup + record order (MapChange, objects, player spawn, snapshot, vitals, 		// gametime), then runs interest for the NPCs. Beach-shaped (FullCharSnapshotSubClass + BeachPlayfieldId); a non-beach
		// map would get beach tails, so the flag is for a beach login until per-map sourcing lands.
		private async Task SendBurstlessWorldEntry(INetworkClient client, uint mapId)
		{
			var character = client.Session.Character;
			var equipment = await EnsureBeachStarterEquipment(character);

			// Seed the class starting loadout (#74/#71) here in the SHARED world-entry path so a freshly CREATED character
			// gets its class spells + default bar too - the create flow (CreateWorldCutsceneScripts) reaches world entry
			// straight through SendBurstlessWorldEntry, bypassing SendWorldEntry's login-only EnsureCharacterLoadout, so a
			// new char previously had only its attack abilities and no spells until the first relog. Idempotent (gated on
			// an empty bar), so this is a no-op on login (already seeded there). Runs BEFORE knownSpellIds is loaded below, 			// so the freshly-seeded spells ride this entry's snapshot known-list (and place on the bar) with no relog.
			await EnsureCharacterLoadout(client);

			// The beach starter quest (10238) is granted ONCE at character creation (CommitCharacterName), not re-seeded
			// here - so a completed/removed quest never reappears. The journal is now driven purely from CharacterQuests.
			var inventoryRows = character != null
				? await Database.GetCharacterInventory(character.Id)
				: [];
			// Union in the item-shop-suppression flags (see ItemShopSuppressTags) so the client's shop-open Lua sees
			// HasTag(X)==true and never auto-opens the itemshopbrowser on entry.
			var characterTags = character != null
				? (await Database.GetCharacterTags(character.Id)).Concat(ItemShopSuppressTags).Distinct().OrderBy(t => t).ToArray()
				: [];
			var scaffoldCharacter = await EnsureSpawnScaffoldRows(character!);
			var worldObjectRows = await Database.GetWorldObjects(mapId);

			// #262: the player's ACTIVE quest ids, for quest-gated world objects (WorldObject.GateQuestId / SpawnsFor).
			// Empty for a null character; ungated objects (the default) ignore it.
			var activeQuestIds = character == null
				? []
				: (await Database.GetCharacterQuests(character.Id)).Where(q => q.State == QuestState.Active).Select(q => q.QuestId).ToHashSet();

			QuestDefinition? beachDef = null;
			try
			{
				beachDef = await Database.GetQuestDefinition(BeachStarterQuestId);
			}
			catch (Exception ex)
			{
				Logger.LogWarning(ex, "Burst-less entry: beach quest definition load failed.");
			}

			if (beachDef is not { Goals.Count: > 0 })
			{
				// The beach starter quest is always seeded (importer SeedBeachQuestGoals), so this is a broken-DB case.
				// The legacy captured-burst replay fallback was removed with the rest of the snapshot-splice path;
				// burst-less entry needs the beach quest definition to author the snapshot, so abort the world entry.
				Logger.LogError("Burst-less world entry (map={Map}): no beach quest definition; cannot build the world entry.", mapId);
				return;
			}

			// Populate the shared world model BEFORE building so the player entity exists for CaptureOwnSpawnBody.
			// Resolve which COPY (instance) of this map the player joins from its instancing mode (#268): register the
			// player first (which picks/keeps their instance), then populate THAT instance's NPCs.
			var map = await Database.GetMap(mapId);
			// #3: aggregate the equipped-gear stat bonuses so the player's live vitals seed with gear-boosted maxes.
			var gearBonuses = character == null
				? null
				: MaxStatAggregator.AggregateGearBonuses(await Database.GetEquippedStatItems(character.Id, character.ActiveWeaponSet));
			_simulation.RegisterPlayerInWorld(client, map?.InstanceMode ?? MapInstanceMode.Shared, gearBonuses);
			var instanceId = _simulation.PlayerWorldEntity(client)?.InstanceId ?? WorldState.DefaultInstance;
			await _broadcaster.EnsureZoneNpcs(mapId, instanceId);

			// Warm this map's terrain heightmap + collision mesh off the tick (#284) so the first aggro/loot doesn't
			// stall the game loop with the ~12 MB decompress + grid build. Fire-and-forget; the caches are thread-safe.
			_ = Task.Run(() => _simulation.Terrain?.Warm(mapId));

			// Mark this connection as having entered the world. The conversation system (ConversationService), the
			// 0x2020 -> 0x2008 spawn-check (GameServerListener), and the dialogue-close fallback (Handle0x2000) are
			// all gated on this - without it NPC dialogue silently never opens.
			client.Session.WorldEntered = true;

			// Retail split (proven byte-exact in the *-create vs *-login captures): a CREATE-world entry carries NO quests
			// in the snapshot (empty Quest_c region) and grants the starter quest LIVE afterwards; a LOGIN entry carries
			// the character's held quests in the snapshot. So the snapshot region is data-driven from CharacterQuests on
			// login, and empty on create.
			var isCreateEntry = client.Session.IsCreateWorldSession;
			var (snapshotQuests, primaryQuest) = isCreateEntry
				? (new List<QuestDefinition>(), (QuestDefinition?)null)
				: await LoadSnapshotQuests(character!);
			// #74/#71: the character's learned spells ride the snapshot's known-list so they survive relog + place on the
			// action bar (instead of the per-login DispatchLearnSpell band-aid). Loaded after EnsureCharacterLoadout has
			// seeded a fresh character's class spells.
			var knownSpellIds = character != null
				? (await Database.GetCharacterSpells(character.Id)).Select(s => s.SpellId).ToArray()
				: [];
			// The character's captured stat sheet (level/vitals/attributes/PvP counters/ratings), authored into the
			// snapshot's stat block per-character; empty for a freshly-created character (skeleton + patches then).
			var statSheet = character != null
				? (await Database.GetCharacterStats(character.Id)).Select(s => (s.StatId, s.Value)).ToArray()
				: [];
			// The playfield-instance token is BeachPlayfieldId - the ONE value already threaded through the map-change, the
			// player-spawn transform, and the world objects. Thread it into the SNAPSHOT (stat slot 0xBF, via
			// BuildFromScratch) and the zone as well, so all of them AGREE BY CONSTRUCTION. The client scopes the character
			// to the playfield it loaded, so a snapshot token that disagrees with the map-change/spawn hides the abilities
			// tab (a server-minted id fails exactly this way). No more extract-the-token-back-out-of-the-snapshot. (Per-map
			// sourcing is a follow-up - the beach id is used for every map for now, exactly as the spawn/map-change do.)
			// #305: the token is now sourced PER-MAP (Map.PlayfieldToken), falling back to the beach default for maps with
			// no captured token. Threaded identically through the map-change, snapshot, world objects, spawn transform and
			// the zone so they still agree by construction - only the source value changes. A wrong (beach) token in a
			// mountable zone like Old Tarantia made the client read it as the no-mount Tortage beach.
			var mapToken = map?.PlayfieldToken ?? 0;
			var playfieldToken = mapToken != 0
				? mapToken
				: BeachPlayfieldId;
			_simulation.World.ZoneFor(mapId, instanceId).PlayfieldToken = playfieldToken;
			// The Completed tab rides the snapshot too (#390); a create-world entry has none.
			var completedJournal = isCreateEntry
				? []
				: await LoadCompletedJournal(character!);
			// The quest INDEX needs, per active quest, its AREA's 4CC and that area's full quest catalog ordered by
			// JournalSequence (#390) - neither is per-quest data.
			var (indexAreas, areaCatalog) = await LoadAreaCatalog(snapshotQuests);
			var snapshot = BuildBurstlessSnapshot(client, equipment!, inventoryRows, characterTags, snapshotQuests, primaryQuest!, knownSpellIds, statSheet, playfieldToken, completedJournal, indexAreas,
				areaCatalog);

			var toSend = new List<Rpc>(worldObjectRows.Length + 5)
			{
				BuildMapChange(client, map!, playfieldToken)
			};
			toSend.AddRange(BuildBurstlessWorldObjects(client, worldObjectRows, playfieldToken, activeQuestIds));

			var playerSpawn = BuildBurstlessPlayerSpawn(client, equipment!, scaffoldCharacter!, playfieldToken);
			if (playerSpawn is { } spawn)
			{
				toSend.Add(spawn);
				_broadcaster.CaptureOwnSpawnBody(client, spawn); // so other players can be sent this player's spawn
			}
			else
				Logger.LogWarning("Burst-less entry (map={Map}, '{Name}'): player spawn not built (no scaffold rows?); avatar may not render.", mapId, character?.Name);

			toSend.Add(snapshot);

			// #398: the in-range NPCs ride the ENTRY BURST (before the client's first move), matching retail's shape
			// (fr64-75 in the Hoxxling beach login capture) instead of a post-load interest volley. BuildEntryNpcBurst
			// seeds them into the player's Known set, so the interest layer (BroadcastPlayerMove) then streams only the
			// DELTA as the player walks - and the initial set is never culled mid-load (subsumes #245). The markers /
			// captive pose for these NPCs are separate IIRs and ride SendEntryNpcFollowups after the flush.
			var (entryNpcRecords, entryBurstNpcs) = await _broadcaster.BuildEntryNpcBurst(client);
			toSend.AddRange(entryNpcRecords);

			toSend.Add(BuildVitals(client));
			toSend.Add(BuildGameTime(client));

			foreach (var rpc in toSend)
				await client.SendAsync(rpc);

			Logger.LogInformation("Burst-less world entry (map={Map}, '{Name}'): sent {Count} constructed packets ({Npcs} in-range NPCs in the burst).", mapId, character?.Name, toSend.Count,
				entryBurstNpcs.Count);

			// The per-NPC quest markers / captive pose for the burst NPCs (separate IIRs, after the spawn records).
			await _broadcaster.SendEntryNpcFollowups(client, entryBurstNpcs);

			// Spawn this player to nearby OTHER players (its NPC set already rode the burst above). The quest journal is
			// NOT re-sent here: retail ships both tabs in the login snapshot and sends no quest packet at all until the
			// first in-session accept (#389/#390), so a live "restore" burst is a fabrication - and the one that used to
			// live here delivered every quest under a single area header, which filled the Completed tab with one bogus
			// group and left Active empty.
			await _broadcaster.BroadcastPlayerEntry(client);

			// On a CREATE-world entry the snapshot carried no quests (empty region above), so deliver the starter quest
			// LIVE exactly as retail does: QuestLogList (J-panel) then QuestFullUpdate (detail). It was persisted at
			// character creation, so it rides the snapshot on every later login - and nothing re-sends it live there
			// (#390), so there is no double-send.
			if (isCreateEntry && character != null)
			{
				await WorldBroadcaster.SendQuestLogList(client, beachDef, [BeachStarterQuestId]);
				await _broadcaster.SendQuestFullUpdate(client, beachDef);
				Logger.LogInformation("Create-world entry: delivered starter quest {QuestId} live to '{Name}'.", BeachStarterQuestId, character.Name);
			}

			// #262: if the player logs in mid-escort (an escort stage active), resume the escortee's follow now - the
			// live grant-trigger only fires on the talk itself, so without this a relog leaves her at the cage. Data-
			// driven off the Escort goal + its escortee template id (no hard-coded quest id / name).
			if (character != null)
			{
				foreach (var q in await Database.GetCharacterQuests(character.Id))
				{
					if (q.State != QuestState.Active)
						continue;

					var qdef = await Database.GetQuestDefinition(q.QuestId);
					if (qdef?.Goals.FirstOrDefault(g => g.GoalType == QuestGoalType.Escort) is { TrailingRefId: not 0 } escort)
					{
						_broadcaster.SetNpcFollowByTemplate(mapId, escort.TrailingRefId, client.Session.Character.Id, true);
						break;
					}
				}
			}
		}

		// The change-map prep IIR bodies (begin + destination announce) the retail teleport sends before the 0x2002
		// handoff, built by the named TeleportCodec (#219, World/TeleportCodec.cs). In-client verification pending.
		private async Task SendChangeMapPrep(INetworkClient client, uint destMapId, Vector3 position, uint destDescriptor = 0x00000014u)
		{
			var instance = client.Session.Character.Id;
			// Source the destination playfield token from the destination map's burst so the announce matches the token
			// the reconnect's world-entry will stamp (#219 / #211) - it is per-playfield-instance, not a constant.
			var destToken = await _broadcaster.ResolveMapPlayfieldToken(destMapId);
			foreach (var body in new[] { TeleportCodec.BuildBegin(instance, destMapId), TeleportCodec.BuildDestination(instance, destMapId, position, destToken, destDescriptor) })
			{
				await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client),
					GameInterfaceOpcodes.DispatchPassBlob, s => s.WriteArrayUInt32Length(body)));
			}
		}

		// Emits the retail level-up dings: ONE NewLevelIIR_t (0x6D314743) per level gained - the event that fires the
		// client's "Congratulations, you have reached level N!" banner + the level-up visual/audio effect (#209). The
		// banner text is client-side and gated on this packet; the raw Level stat + AttributeChanged lines alone show
		// nothing. Retail sends a SEPARATE event per level (Hoxxling dinged L2/L3/L4 as three, frames 9937->), each
		// carrying [newLevel, currentXP, xpThisLevel, xpNextLevel, 1, 8, 0] (fields 5/6/7 constant across every
		// capture). Sent before the AttributeChanged burst (combat-levelup capture ordering).
		private async Task SendLevelUpDings(INetworkClient client, uint fromLevel, uint toLevel, uint finalRemainingXp)
		{
			var instance = client.Session.Character.Id;
			var @class = client.Session.Character.Class;
			for (var lvl = fromLevel + 1; lvl <= toLevel; lvl++)
			{
				var thisXp = LevelingResolver.CumulativeXpForLevel(lvl);
				var nextXp = LevelingResolver.CumulativeXpForLevel(lvl + 1);
				var currentXp = lvl == toLevel
					? thisXp + finalRemainingXp
					: thisXp; // only the final level carries partial XP
				ReadOnlySpan<uint> deltas = [currentXp, thisXp, nextXp, 1u, 8u, 0u];

				await SendWorldIir(client, NewLevelIirCodec.Build(instance, lvl, deltas));

				// The combos this class unlocks AT this level, from the class<->combo ACCESS map (ClassCombo) - retail
				// sends them right after the NewLevelIIR (combat-levelup capture ordering). ClassCombo replaces the old
				// class-independent level-keyed catalog (casters have no rows); empty until populated from dumps.
				foreach (var combo in await Database.GetClassCombosUnlockedBetween(@class, lvl - 1, lvl))
					await SendWorldIir(client, ComboBookIirCodec.Build(instance, combo.Id));
			}
		}

		private async Task SendReadyForPlayScreen(INetworkClient client)
		{
			var sender = client.Session.IsUnnamedCreatePhase
				? Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x59)
				: Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58);

			var receiver = client.Session.IsUnnamedCreatePhase
				? Endpoints.EndpointFromBytes(0x0D, 0x91, 0xF7, 0x87, 0x8B, 0x10, 0xC2, 0xDD, 0xD8, 0x10)
				: client.Endpoint(Endpoints.GameCharInterfaceType);

			using var bodyStream = new FaolanStream();
			if (client.Session.IsUnnamedCreatePhase)
			{
				// New-character create flow: the create-capture's ValidResurrectionSpots body - a sibling of the login
				// WriteValidResurrectionSpotsBody with the create variant's own constants (see ResurrectionSpotsBody).
				ResurrectionSpotsBody.WriteCreateFlowValidResurrectionSpots(bodyStream, client.Session.Character.Position);
			}
			else
			{
				// Existing-character login: this 0x2024 is byte-structurally GameCharInterface.DispatchValidResurrectionSpots
				// (the death/release screen) - at login the client consumes it silently to set the resurrection anchor. Build
				// the body via the shared WriteValidResurrectionSpotsBody from the character's actual resurrection point (the
				// same closest-known/AlwaysKnown resolution SendDeathScreen does), so the anchor is per-character/per-map and
				// advertises our REAL RespawnPoint.Id (the id RespawnMe echoes back) instead of the Strie capture's hardcoded
				// beach point. Falls back to the universal Tortage Beach default when the map defines no point.
				var spot = await ResolveLoginResurrectionSpot(client.Session.Character);
				ResurrectionSpotsBody.WriteValidResurrectionSpotsBody(bodyStream, client.Session.Character.Position, [spot]);
			}

			await client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceOpcodes.DispatchValidResurrectionSpots, bodyStream.ToArray()));
		}

		// Answers a client 0x2020 spawn poll with a 0x2008 SpawnCheck on the 201C conversation: body is
		// 0x0000C350 + the polled entity handle + a status byte (0x02 for the player's own avatar, else 0x00).
		private async Task SendSpawnCheckResponse(INetworkClient client, uint handle)
		{
			// The 0x2020 poll reply (0x2008) doubles as the NPC quest marker. Compute it from the player's quest
			// state for the polled handle (#241) instead of the old hardcoded 0x02-for-Casilda, which forced '!' on
			// EVERY poll and overrode the state-driven marker (so it never cleared after accept). Non-giver /
			// not-in-view -> 0x00 (still confirms the spawn). Capture-matched: poll reply was 0x02 before accept,
			// 0x00 after (Ranochar frames 125 -> 698).
			var status = await _broadcaster.QuestMarkerStateForHandle(client, handle);

			await client.SendAsync(new Rpc(LoginWorld201CSenderEndpoint, client.Endpoint(Endpoints.GameCharInterfaceType),
				GameCharInterfaceOpcodes.DispatchSetQuestMarker,
				b => b.WriteUInt32(EntityClass.Character).WriteUInt32(handle).WriteByte(status)));
		}

		// The single existing-character WORLD ENTRY used by both login and the teleport GM command: send the 0x201c
		// channel handshake, then replay the character's current-map world burst from the DB (MapChange + world
		// objects + NPC spawns + the player spawn + the inventory snapshot, all re-addressed and DB-driven). Falls
		// back to the beach burst for any map without a usable burst. The caller has already set Character.MapId/Pos.
		private async Task SendWorldEntry(INetworkClient client)
		{
			await Send0X201C(client);

			// First-entry seeding (#74/#71): a freshly-created character with an empty action bar gets its class's
			// starting spells (persisted) + the default bar layout, so the spell grant below sends them this session.
			await EnsureCharacterLoadout(client);

			// #237: re-grant the character's learned SPELLS (DispatchLearnSpell 0x200A) BEFORE the world snapshot, so
			// the client absorbs them as part of the initial load and stays SILENT - retail sends them pre-entry
			// (NecroL80 login: grants at frame 59, the 0x737A6DF9 snapshot at frame 152). Granting them post-ready
			// (the old PostReadyFollowUp call) made the client treat each as a fresh learn and re-toast "You have
			// learned a new spell" on EVERY login. Done here, the shared world-entry path, so it's silent on login AND
			// on changemap/teleport re-entry. (Abilities 0x2052 stay post-0x2024 - they don't toast.)
			await GrantCharacterSpells(client);

			var mapId = client.Session.Character.MapId;

			// Burst-less entry (#210) is the ONLY world-entry path: the whole world entry is built from
			// generated/structured pieces - no captured burst. (The FAOLAN_BURSTLESS_ENTRY env toggle + its
			// captured-burst replay fallback were removed once burstless became the verified default; the
			// captured-burst replay now survives only as SendBurstlessWorldEntry's internal safety net for a map
			// with no quest definition.)
			await SendBurstlessWorldEntry(client, mapId);
		}

		private async Task SendWorldHandoff(INetworkClient client)
		{
			var realm = await Database.GetRealm(client.Session.Character.RealmId);
			var address = $"{realm!.IpAddress}:{realm.Port}";

			await client.SendAsync(new Rpc(CreateFlowService.MorphAckSender, CreateFlowService.Receiver(client),
				GameInterfaceOpcodes.DispatchRedirectToServer,
				b => b.WriteByte(0x00).WriteStringByteLength(address)));

			Logger.LogInformation("Create-editor: sent enter-world handoff (0x2002) advertising world server {Address}.", address);
		}

		// The complete burst-less player FA015807 spawn packet, or null when the character has no scaffold rows yet.
		private static Rpc? BuildBurstlessPlayerSpawn(INetworkClient client, CharacterEquipment equipment, Character scaffoldCharacter, uint playfield)
		{
			var character = client.Session.Character;
			// The CharacterIIR is built from code (PlayerCharacterIir, formerly PlayerCharacterIirTemplate.bin, #258):
			// the fixed render template with this character's name/identity/looks/worn gear applied.
			var message = PlayerCharacterIir.Build(character, equipment);

			// A near-zero stored quaternion (the degenerate W=0 default) can't be realised by the avatar; fall back to
			// identity rather than risk it (the live splice path guards the same way).
			var rotation = character.Rotation.LengthSquared() > 0.0001f
				? character.Rotation
				: Quaternion.Identity;

			var body = PlayerSpawnBuilder.BuildFromScratch(client.Session.Character.Id, playfield, character.Position, rotation, message.ToProtobuf(), scaffoldCharacter);
			return body == null
				? null
				: BuildWorldEntrySpawn(client, body);
		}

		private static Rpc BuildBurstlessSnapshot(INetworkClient client, CharacterEquipment equipment, CharacterInventory[] inventoryRows, uint[] characterTags,
			IReadOnlyList<QuestDefinition> snapshotQuests, QuestDefinition primaryQuest, IReadOnlyList<uint> knownSpellIds, IReadOnlyList<(CharacterStatId Id, uint Value)> statSheet,
			uint? playfieldToken, IReadOnlyList<(QuestArea Area, IReadOnlyList<QuestDefinition> Quests)> completedJournal, IReadOnlyDictionary<uint, QuestArea> indexAreas,
			IReadOnlyDictionary<uint, IReadOnlyList<QuestDefinition>> areaCatalog)
		{
			var instance = client.Session.Character.Id;
			var root = BuildBurstlessSnapshotRoot(instance, equipment, inventoryRows, characterTags);
			var tails = FullCharSnapshotSubClass.BuildFromScratch(snapshotQuests, primaryQuest, client.Session.Character, instance, playfieldToken, knownSpellIds, statSheet, completedJournal,
				indexAreas, areaCatalog);
			return BuildWorldEntrySnapshot(client, root, tails);
		}

		// The FullCharacterIIR root (containers) as protobuf bytes - typed authoring produced standalone (no captured
		// packet to splice into).
		private static byte[] BuildBurstlessSnapshotRoot(uint instance, CharacterEquipment equipment, CharacterInventory[] inventoryRows, uint[] characterTags)
		{
			var equipped = new Dictionary<ulong, (Item Item, uint Hash)>();
			foreach (var slot in InventoryEquippedSlots)
			{
				var item = equipment[slot];
				if (item is { Hash: not 0 })
					equipped[(ulong)slot] = (item, item.Hash);
			}

			(Item, uint)? bagFirst = equipment.FirstPosBag is { Hash: not 0 }
				? (equipment.FirstPosBag, equipment.FirstPosBag.Hash)
				: null;

			var typedRoot = FullPlayerCharacterBuilder.BuildRoot(characterTags, FullPlayerCharacterBuilder.FullExpansionFlags, FullPlayerCharacterBuilder.FullSubscriptionFlags, false, true,
				FullPlayerCharacterBuilder.BuildContainers(instance, equipped, bagFirst, inventoryRows ?? []));
			return typedRoot.ToProtobuf();
		}

		// The character-creator (editor) phase: a not-yet-named character is connected. The client instance is
		// the character's real id now, so the unnamed-character state alone identifies the editor flow.

		private static Task ReportDimensionId(INetworkClient client, uint realmId, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchReportDimesionID, s => s.WriteString(realmId.ToString())));
		}

		private static Task ReportServerId(INetworkClient client, uint value, ProtoEndpoint sender, ProtoEndpoint receiver)
		{
			// Body: [u32 0x54 (fixed header)][u32 value]. The create-flow auth handshake reports value 1.
			return client.SendAsync(new Rpc(sender, receiver, GameInterfaceOpcodes.DispatchReportServerID, s => s.WriteUInt32(0x54u).WriteUInt32(value)));
		}

		private static async Task Send0X201C(INetworkClient client)
		{
			var receiver = client.Endpoint(Endpoints.GameCharInterfaceType);
			var ticketBase = 0x00160000u + ((uint)Environment.TickCount & 0xFFFF);

			// Retail (Strie login) sends five channel registrations before the spawn burst: four with the
			// 0x65 prefix and state ids {5, 6, 0x10, 0x12}, then a final 0x68 prefix with state 1. The old
			// three-entry set (with a duplicated state 6 and missing 5/0x12/the 0x68 entry) left the client
			// stuck before it ever started polling spawns (0x2020), so nothing rendered.
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000005, ticketBase);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000006, ticketBase + 1);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000010, ticketBase + 2);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000065, 0x00000012, ticketBase + 3);
			await Send0X201CEntry(client, LoginWorld201CSenderEndpoint, receiver, 0x00000068, 0x00000001, ticketBase + 4);
		}

		private static Task Send0X201CEntry(INetworkClient client, ProtoEndpoint sender, ProtoEndpoint receiver, uint prefix, uint state, uint ticket)
		{
			return client.SendAsync(new Rpc(sender, receiver, GameCharInterfaceOpcodes.DispatchSetItemStat,
				b => b.WriteUInt32(prefix)
					.WriteUInt32(client.Session.Character.Id)
					.WriteUInt32(state)
					.WriteUInt32(0x00000017)
					.WriteUInt32(ticket)));
		}
	}
}
