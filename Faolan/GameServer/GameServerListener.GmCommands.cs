using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// .additem <itemId> [count]  - add an item (by faolan.db Items id) to the calling character's bag: persist
		// a CharacterInventory row in the first free bag slot, then re-send the rebuilt 0x737A6DF9 inventory
		// snapshot so it appears live (the snapshot is the ONLY item-delivery packet - there is no incremental
		// server add-item; see protocol_containeradditem). Without a stashed snapshot template (pre-world or a
		// non-replay session) the item still persists and renders on next login.
		private async Task AddItemCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var characterId = character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			uint count = 1;
			if (parts.Length is < 1 or > 2 || !uint.TryParse(parts[0], out var itemId) || (parts.Length == 2 && !uint.TryParse(parts[1], out count)))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .additem <itemId> [count]");
				return;
			}

			// A missing Items row is the only refusal left. This also demanded a harvested hash until #723, which
			// made the 232 never-captured templates un-`.additem`-able - and .additem is the standard bootstrap for
			// testing exactly those.
			var item = await Database.GetItem(itemId);
			if (item == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"No item with id {itemId}.");
				return;
			}

			// Persist + deliver live via DispatchAddItem (0x2020) - it appears without a relog now that the live
			// add path exists (the old "no live add packet exists" caveat is gone).
			var slot = await _economyOutbound.GrantItemToBackpack(client, itemId, count);
			if (slot == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Bag is full (24 cells).");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Added {count}x '{item.Name}' (id {itemId}) to bag slot {slot} (live DispatchAddItem).");
		}

		// .ban <account|character name>   - ban an account (sets State = Banned; login rejects non-Active accounts).
		// .unban <account|character name> - lift a ban (State = Active). Accepts either the account username OR one of
		// its character names. Banning also kicks the account's live world connections (the State change alone only
		// blocks the NEXT login). You can't ban your own account.
		private async Task BanCommand(INetworkClient client, string arguments, bool ban)
		{
			var gmId = client.Session.Character.Id;
			var name = arguments.Trim();
			if (string.IsNullOrEmpty(name))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"Usage: .{(ban ? "ban" : "unban")} <account or character name>");
				return;
			}

			// Resolve the target account: by username first, else by a character name -> its owning account.
			var account = await Database.GetAccount(name);
			if (account == null && await Database.GetCharacterByName(name, client.Session.Character.RealmId) is { } ch)
				account = await Database.GetAccount(ch.AccountId);

			if (account == null)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", $"No account or character named '{name}'."));
				return;
			}

			if (ban && account.Id == client.Session.Account.Id)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "You can't ban your own account."));
				return;
			}

			if (account.Type > client.Session.Account.Type)
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "You can't ban an account with a higher rank than yours."));
				return;
			}

			if (!await Database.SetAccountState(account.Id, ban
				    ? AccountState.Banned
				    : AccountState.Active))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "Error changing account state."));
				return;
			}

			var kicked = 0;
			if (ban)
			{
				// Snapshot before iterating: Stop() -> Disconnected mutates the client set.
				foreach (var c in NetworkClients.Where(nc => nc.Session.Account?.Id == account.Id).ToList())
				{
					if (c.Session.Character?.Id is { } cid)
						await _agentServerDispatcher.SendSystemMessage(cid, ChatMarkup.Color("red", "You have been banned by a GM."));
					c.Stop();
					kicked++;
				}
			}

			await _agentServerDispatcher.SendSystemMessage(gmId, ban
				? $"Banned account '{account.UserName}'{(kicked > 0 ? $" and kicked {kicked} live session(s)" : "")}."
				: $"Unbanned account '{account.UserName}'.");
		}

		// .delete [<handle>]  - PERMANENTLY remove the player's currently SELECTED target (or the given handle): despawn
		// it live for nearby players AND delete its NpcSpawn row(s) so it never renders again. The tool for scrubbing
		// captured import artifacts (phantom players that happened to be in the pcap when the world was captured). A
		// faction mob (hostile) deletes only the rows at the target's spot (don't wipe every same-name mob); a neutral
		// named entity deletes every same-name row on the map (its cross-session captures collapse to one render).
		// Unlike a plain live despawn (comes back on relog), .delete is durable.
		private async Task DeleteCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var resolved = arg.Length == 0
				? client.Session.SelectedTargetHandle
				: TryParseHandle(arg, out var parsed)
					? parsed
					: null;
			if (resolved is not { } handle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .delete [<handle>]  (no arg = your selected target). Removes it live AND deletes its spawn row.");
				return;
			}

			var entity = await _npcs.DespawnEntityForPlayer(client, handle);
			if (entity == null)
			{
				// Not a live entity - it may be a static WORLD OBJECT (chest/door/dynel), which lives only in the DB.
				var obj = await _worldObjects.DeleteWorldObjectForPlayer(client, handle);
				if (obj != null)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"Deleted world object {WorldObjectDisplay.ToWire(obj) ?? obj.Name ?? "(unnamed)"} (0x{handle:X8}) - removed live + DB row (gone on relog too).");
					return;
				}

				await _agentServerDispatcher.SendSystemMessage(characterId, $"No deletable entity or world object 0x{handle:X8} in this zone (players can't be deleted).");
				return;
			}

			var deleted = await Database.DeleteNpcSpawns(entity.MapId, entity.Name, entity.Hostile
				? entity.SpawnPosition.X
				: null, entity.Hostile
				? entity.SpawnPosition.Y
				: null, entity.Hostile
				? entity.SpawnPosition.Z
				: null, 10f);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Deleted {entity.Name} (0x{handle:X8}) - removed live + {deleted} spawn row(s) (gone on relog too).");
		}

		private async Task MatchStartCommand(INetworkClient client)
		{
			var outcome = await _csPlayerAgentDispatcher.ForcePvpMatchStart();
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"matchstart: {outcome}");
		}

		// .matchstop [reason]  - end the caller's started PvP match NOW, without waiting out its clock (#146). The
		// client's own abort path: every member gets DispatchForceStopMinigame with the reason line, then the match
		// dissolves and each member is put back where the countdown teleport found them. The test bootstrap for the
		// end half of the match loop, the way `.matchstart` is for the start half.
		private async Task MatchStopCommand(INetworkClient client, string arguments)
		{
			var outcome = await _csPlayerAgentDispatcher.ForcePvpMatchStop(client.Session.Character.Id, (arguments ?? "").Trim());
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"matchstop: {outcome}");
		}

		// .graduatenpcgroup <group name>  - GRADUATE a resolved spawn area (#363): delete the CAPTURED NpcSpawn rows
		// LINKED to it (materialized by the resolver's auto-link + `.setnpcgroup`) so the ambient POOL becomes the sole
		// source of that population - removing the captured/ambient density overlap. A clean FK delete on the membership
		// link, so a named/quest NPC in the area (never linked) is safe. Durable; takes effect on the next zone reload.
		// NB re-running the `npcs` importer resurrects the captured rows.
		private async Task GraduateNpcGroupCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var name = (arguments ?? "").Trim();
			if (name.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .graduatenpcgroup <group name>  (removes the captured spawns the area's ambient pool replaces).");
				return;
			}

			var mapId = _simulation.PlayerWorldEntity(client)?.MapId ?? client.Session.Character.MapId;
			var matches = (await Database.GetNpcSpawnAreas(mapId)).Where(a => string.Equals(a.GroupName, name, StringComparison.OrdinalIgnoreCase)).ToList();
			if (matches.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"No spawn area named '{name}' on map {mapId}.");
				return;
			}

			var resolved = matches.Where(a => a.NpcTemplateId != null).ToList();
			if (resolved.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Area '{name}' has no creature assigned yet - resolve it with .setnpcgroup first (nothing to graduate).");
				return;
			}

			var removed = 0;
			foreach (var area in resolved)
			{
				var n = await Database.GraduateSpawnArea(area.Id);
				if (n > 0)
					removed += n;
			}

			var creature = resolved[0].NpcTemplate?.Name ?? "?";
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Graduated '{name}' ({creature}) on map {mapId}: removed {removed} captured spawn row(s); the ambient pool now owns this population. Takes effect on zone reload (relog).");
		}

		private async Task HandleGmCommand(INetworkClient client, string commandText)
		{
			string command;
			string? arguments = null;

			if (!commandText.Contains(' '))
				command = commandText[1..];
			else
			{
				var spaceIndex = commandText.IndexOf(' ');
				command = commandText[1..spaceIndex];
				arguments = commandText[(spaceIndex + 1)..];
			}

			if (_gmCommandsDict.TryGetValue(command, out var entry))
			{
				// Per-command privilege gate (#904): retail gates each command against the gm_level BITMASK, not a
				// boolean, so the per-bit admin UI has to bite here too - un-checking NPCCommands must stop `.kill`.
				// ⚠️ RequiredMask == None means UNRESTRICTED (any GM), the client's own convention (FUN_006c86b0);
				// getting that backwards would lock every GM out of every unmasked command. The outer IsGm gates on
				// the callers still stand - this is the per-command refinement inside them.
				if (entry.RequiredMask != GmPrivileges.None && client.Session.Account?.HasGmPrivilege(entry.RequiredMask) != true)
				{
					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
						ChatMarkup.Color("red", $"'{command}' requires the {entry.RequiredMask} privilege."));
					return;
				}

				try
				{
					await entry.Handler.Invoke(client, arguments!);
				}
				catch (Exception e)
				{
					await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Color("red", $"{e.Message}"));
				}
			}
			else
				// Echo the prefix the caller actually used: the same table serves our `.cmd` chat commands and the
				// client's own `/cmd` GM commands (FanaticCommandIIR), and telling a `/teleport` user to "type .help"
				// with the wrong prefix reads as a different tool.
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
					$"Unknown command: '{command}' - type {commandText[0]}help for info");
		}

		// QueuePetCommand (0x205B, GameCharAgent): the pet command bar. Body = [u32 type=0xC350][u32 petHandle]
		// [u32 command]. The command set is the client's own twelve, recovered in #307 - see PetCommand for the
		// derivation and for the two conclusions it overturned (docs/subsystems/summon-pet-decode.md).
		//
		// DISMISSAL is the only family that ACTS today: retail's PetIIR mode-1 remove + the n3ToClientQuit despawn. The
		// stance commands need pet-side combat AI (a pet picking and engaging a target), which does not exist - a
		// summoned minion only follows, via SetNpcFollow. They are logged rather than silently dropped so the bar's
		// traffic is visible while that is built.
		//
		// All three dismissal codes are honoured, not just the captured one: `DismissFollower` (0x1F8) is what the
		// corpus shows, but `Dismiss` and `AllDismiss` are the same act from the per-pet and all-pets bars, and
		// answering only the captured spelling would leave the other two bars' buttons dead.
		private async Task HandlePetCommand(INetworkClient client, Rpc packet, FaolanStream body)
		{
			body.ReadUInt32(); // type 0xC350
			var petHandle = body.ReadUInt32();
			var command = body.ReadUInt32<PetCommand>();
			Logger.LogInformation("QueuePetCommand from {Owner:X8}: pet 0x{Pet:X8}, command {Command}.", client.Session.Character.Id, petHandle, command);

			if (command is PetCommand.DismissFollower or PetCommand.Dismiss or PetCommand.AllDismiss)
				await _pets.DismissPet(client, petHandle);
		}

		private void InitGmCommands()
		{
			_gmCommandsDict = BuildGmCommands(new Dictionary<string, GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["save"] = (client, _) => SaveChanges(client.Session.Character.Id),
				["help"] = (client, _) => SendGmHelpText(client),
				["maps"] = (client, arguments) => SendListAllMaps(client.Session.Character.Id, arguments),
				["teleport"] = TeleportCommand,
				// The retail client's own verbs for the same thing, so a GM's muscle memory works: the client
				// registers /teleport, /teleportself and /tp all under the TeleportSelf privilege, and its
				// FanaticCommandIIR routes through this same table (#448).
				["teleportself"] = TeleportCommand,
				["tp"] = TeleportCommand,
				["position"] = (client, _) => PositionCommand(client),
				["recall"] = (client, _) => RecallToBindCommand(client),
				["recallhome"] = (client, _) => RecallToRacialHomeCommand(client),
				["criminal"] = CriminalCommand,
				["heroicmode"] = HeroicModeCommand,
				["travel"] = TravelCommand,
				["knockback"] = KnockbackCommand,
				["level"] = SetLevelCommand,
				["feat"] = _feats.FeatCommand,
				["godmode"] = (client, _) => ToggleGodModeCommand(client),
				["invisible"] = (client, _) => ToggleInvisibleCommand(client),
				["delete"] = DeleteCommand,
				["kill"] = KillCommand,
				["hurt"] = HurtCommand,
				["additem"] = AddItemCommand,
				["removeitem"] = RemoveItemCommand,
				["tag"] = TagCommand,
				["claim"] = _claims.ClaimCommand,
				["syschat"] = (client, _) => SysChatCommand(client),
				["toast"] = ToastCommand,
				["ah"] = (client, _) => OpenAuctionHouseCommand(client),
				["ban"] = (client, arguments) => BanCommand(client, arguments, true),
				["unban"] = (client, arguments) => BanCommand(client, arguments, false),
				["setnpcgroup"] = SetNpcGroupCommand,
				["target"] = (client, _) => TargetInfoCommand(client),
				["togglenpcgroup"] = (client, _) => ToggleNpcGroupCommand(client),
				["graduatenpcgroup"] = GraduateNpcGroupCommand,
				["weaponset"] = WeaponSetCommand,
				// #914 Wave A - the retail client's own observability verbs, on the grammar recovered in #913.
				// ⚠️ `time` is NOT here on purpose. Retail's own `/time` manipulates FRAME times (the deltatimer system);
				// the time-of-day setter is `/settime`, which is what our `.time` actually was. Keeping ours under `time`
				// would be retail's name over someone else's meaning, so it moved (#914 rule 1) and `/gametime` took the
				// `reset` form, where retail puts it.
				["settime"] = SetTimeCommand,
				["gametime"] = GameTimeCommand,
				["distance"] = (client, _) => DistanceCommand(client),
				["whereis"] = WhereIsCommand,
				["playfieldinfo"] = (client, _) => PlayfieldInfoCommand(client),
				["playfieldinstancelock"] = (client, _) => PlayfieldInstanceLockCommand(client),
				["ghost"] = GhostCommand,
				["system"] = (client, _) => SystemInfoCommand(client),
				["hatedbylist"] = (client, _) => HatedByListCommand(client),
				// Wave B1 - character administration and the environment/economy levers.
				["summon"] = SummonCommand,
				["unsummon"] = UnsummonCommand,
				["assist"] = AssistCommand,
				["falldamage"] = FallDamageCommand,
				["cash"] = CashCommand,
				// Wave B2 - the combat levers.
				["inflict"] = InflictCommand,
				["areaknockback"] = AreaKnockbackCommand,
				["resurrect"] = ResurrectCommand,
				// Wave B3 - the stat family. ⚠️ `setstat` is GONE (#914 rule 1): it pushed a raw stat to the CLIENT
				// without touching server state, which is precisely retail's `/setlocal`, not its `/set`. The two verbs
				// are one class in the client, separated by the registry record's mode byte, so they are one method here.
				["get"] = GetStatCommand,
				["set"] = (client, arguments) => SetStatValueCommand(client, arguments, localOnly: false),
				["setlocal"] = (client, arguments) => SetStatValueCommand(client, arguments, localOnly: true),
				["getskill"] = GetSkillCommand,
				["teleporttarget"] = TeleportTargetCommand,
				["tradeskill"] = TradeskillCommand,
				["waypoint"] = WayPointCommand,
				["resist"] = ResistCommand,
				// #146: force-form a PvP match from the minigame queue as it stands, however few are queued (down
				// to one member - a solo walk of the teleport + match-instance flow). The queue lives on the CS
				// tier, so this crosses the same seam the team ops do.
				["matchstart"] = (client, _) => MatchStartCommand(client),
				// #146: the end half - stop the caller's started match now instead of waiting out its time limit.
				["matchstop"] = MatchStopCommand
			});
		}

		// The gm_level bit each verb requires (#904), joined with the handler table at construction. Two
		// provenances, kept visibly apart:
		//   * RETAIL - the verb exists in the client's own command registry (docs/re/gm-command-registry.tsv) and
		//     the mask is retail's, reused exactly;
		//   * OURS - retail has no such verb (.additem, .ah, ...), so the bit is a CHOICE by family, not a
		//     recovered fact. Do not cite these as decoded.
		// None = unrestricted (any GM), the client's own convention for an unmasked command (FUN_006c86b0), and a
		// multi-bit mask would pass on ANY set bit - the client's `(level & mask) != 0` test.
		internal static readonly Dictionary<string, GmPrivileges> GmCommandMasks = new Dictionary<string, GmPrivileges>(StringComparer.OrdinalIgnoreCase)
		{
			// RETAIL, from the registry: /save /time /tradeskill = Developer 0x1, the /teleport family =
			// TeleportSelf 0x4, /knockback = PlayerDamage 0x10, /feat = GetStats 0x20, /level = SetStats 0x40,
			// /kill = NpcCommands 0x80000.
			["save"] = GmPrivileges.Developer,
			// #914 Wave A - RETAIL's own masks, from the registry: /settime /gametime /ghost /hatedbylist /system are
			// Developer 0x1 (except /system, see below), /whereis /playfieldinfo /playfieldinstancelock = TeleportDynels
			// 0x8, /distance = GetStats 0x20.
			["settime"] = GmPrivileges.Developer,
			["gametime"] = GmPrivileges.Developer,
			["ghost"] = GmPrivileges.Developer,
			["hatedbylist"] = GmPrivileges.Developer,
			["whereis"] = GmPrivileges.TeleportDynels,
			["playfieldinfo"] = GmPrivileges.TeleportDynels,
			["playfieldinstancelock"] = GmPrivileges.TeleportDynels,
			["distance"] = GmPrivileges.GetStats,
			// ⚠️ Retail registers /system at mask 0x0 - UNRESTRICTED - and we match retail rather than inventing a bit.
			// It is the only verb besides our own `.help` that is unmasked.
			["system"] = GmPrivileges.None,
			// Wave B1, again retail's own masks: /summon /assist = AssistSummon 0x2000, /unsummon = TeleportDynels
			// 0x8, /falldamage = PlayerDamage 0x10, /cash = SetStats 0x40.
			["summon"] = GmPrivileges.AssistSummon,
			["assist"] = GmPrivileges.AssistSummon,
			["unsummon"] = GmPrivileges.TeleportDynels,
			["falldamage"] = GmPrivileges.PlayerDamage,
			["cash"] = GmPrivileges.SetStats,
			// Wave B2: /inflict /areaknockback = PlayerDamage 0x10, /resurrect = AcgSpawn 0x1000.
			["inflict"] = GmPrivileges.PlayerDamage,
			["areaknockback"] = GmPrivileges.PlayerDamage,
			["resurrect"] = GmPrivileges.AcgSpawn,
			// Wave B3: /get /getskill = GetStats 0x20, /set /setlocal = SetStats 0x40, /teleporttarget =
			// TeleportDynels 0x8 (NOT TeleportSelf - retail masks moving somebody else higher than moving yourself).
			["get"] = GmPrivileges.GetStats,
			["getskill"] = GmPrivileges.GetStats,
			["set"] = GmPrivileges.SetStats,
			["setlocal"] = GmPrivileges.SetStats,
			["teleporttarget"] = GmPrivileges.TeleportDynels,
			["tradeskill"] = GmPrivileges.Developer,
			["teleport"] = GmPrivileges.TeleportSelf,
			["teleportself"] = GmPrivileges.TeleportSelf,
			["tp"] = GmPrivileges.TeleportSelf,
			["knockback"] = GmPrivileges.PlayerDamage,
			["feat"] = GmPrivileges.GetStats,
			["level"] = GmPrivileges.SetStats,
			["kill"] = GmPrivileges.NpcCommands,

			// OURS - family choices. `help` is unrestricted and lists only what the caller may run.
			["help"] = GmPrivileges.None,
			["maps"] = GmPrivileges.Developer,
			["position"] = GmPrivileges.Developer,
			["recall"] = GmPrivileges.TeleportSelf,
			["recallhome"] = GmPrivileges.TeleportSelf,
			["travel"] = GmPrivileges.TeleportSelf,
			["criminal"] = GmPrivileges.SetStats,
			["heroicmode"] = GmPrivileges.Developer,
			["godmode"] = GmPrivileges.SetStats,
			["invisible"] = GmPrivileges.SetStats,
			["delete"] = GmPrivileges.NpcCommands,
			["hurt"] = GmPrivileges.PlayerDamage,
			["additem"] = GmPrivileges.AcgSpawn,
			["removeitem"] = GmPrivileges.AcgSpawn,
			["tag"] = GmPrivileges.Developer,
			["claim"] = GmPrivileges.Developer,
			["syschat"] = GmPrivileges.Broadcast,
			["toast"] = GmPrivileges.Broadcast,
			["ah"] = GmPrivileges.Developer,
			["ban"] = GmPrivileges.CanKick,
			["unban"] = GmPrivileges.CanKick,
			["setnpcgroup"] = GmPrivileges.NpcCommands,
			["target"] = GmPrivileges.GetStats,
			["togglenpcgroup"] = GmPrivileges.NpcCommands,
			["graduatenpcgroup"] = GmPrivileges.NpcCommands,
			["weaponset"] = GmPrivileges.Developer,
			["waypoint"] = GmPrivileges.Developer,
			["resist"] = GmPrivileges.SetStats,
			["matchstart"] = GmPrivileges.Developer,
			["matchstop"] = GmPrivileges.Developer
		};

		// Joins the handler table with GmCommandMasks, refusing to come up on ANY asymmetry - a verb with a handler
		// but no mask would otherwise dispatch ungated (or a mask with no handler would rot silently), and a loud
		// boot failure is the cheapest place to catch both. GmCommandTableTests pins the retail half of the masks.
		internal static Dictionary<string, GmCommandEntry> BuildGmCommands(Dictionary<string, GmCommandDelegate> handlers)
		{
			var noMask = handlers.Keys.Where(v => !GmCommandMasks.ContainsKey(v)).ToList();
			var noHandler = GmCommandMasks.Keys.Where(v => !handlers.ContainsKey(v)).ToList();
			if (noMask.Count > 0 || noHandler.Count > 0)
				throw new InvalidOperationException(
					$"GM command tables disagree - no mask: [{noMask.JoinAsString(", ")}], no handler: [{noHandler.JoinAsString(", ")}].");

			return handlers.ToDictionary(kv => kv.Key, kv => new GmCommandEntry(kv.Value, GmCommandMasks[kv.Key]), StringComparer.OrdinalIgnoreCase);
		}



		private static bool TryParseStatId(string text, out CharacterStatId statId)
		{
			var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
				: uint.TryParse(text, out raw);

			statId = (CharacterStatId)raw;
			return ok;
		}


		// .kill [<handle> | self]  - kill the player's currently SELECTED target (last LookAt/SelectDeselect), or the
		// given handle, or self. An NPC runs the full combat death lifecycle (vitals push, despawn/corpse-loot, XP, 		// kill-quest advance); self drops the player's vitals to 0 and the world tick respawns them at the nearest
		// resurrection point. No arg + nothing selected -> a system message rather than a no-op.
		private async Task KillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var selfHandle = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			uint? resolved;
			if (arg.Length == 0)
				resolved = client.Session.SelectedTargetHandle;
			else if (arg.Equals("self", StringComparison.OrdinalIgnoreCase) || arg.Equals("me", StringComparison.OrdinalIgnoreCase))
				resolved = selfHandle;
			else if (TryParseHandle(arg, out var parsedHandle))
				resolved = parsedHandle;
			else
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .kill [<handle> | self]  (no arg = your selected target)");
				return;
			}

			if (resolved is not { } targetHandle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No target selected.");
				return;
			}

			if (targetHandle == selfHandle)
			{
				var vitals = _simulation.GetOrAddPlayerVitals(client);
				vitals.Health = 0; // the world tick detects the death and respawns at the nearest resurrection point
				await SendStatUpdate(client, vitals.ToVitalsBlock(), selfHandle);
				await _agentServerDispatcher.SendSystemMessage(characterId, "You kill yourself.");
				return;
			}

			await _combat.KillNpc(client, targetHandle);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Killed target 0x{targetHandle:X8}.");
		}

		// .hurt [<percent>]  - drop the caller's three pools to `percent` of their max (default 50) and push the
		// authoritative vitals. The test bootstrap for the regen tick (Gitea #2): losing HP/mana to a mob or to casting
		// is unreliable to stage, and the pools have to end up BELOW max for the tick to emit anything at all. Never
		// kills - health floors at 1 - so this exercises regen rather than the death/release path (.kill self does that).
		private async Task HurtCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var percent = 50u; // default: half of every pool
			if (arg.Length > 0 && (!uint.TryParse(arg, out percent) || percent > 100))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .hurt [<percent 0-100>]  (default 50)");
				return;
			}

			// Only ever LOWER a pool. This used to SET each one to `percent` of max, which topped UP anything already
			// below that - .hurt 50 on a character sitting at 0/74 mana handed it 37 mana. Min() keeps it a damage
			// command whatever state the pools are in.
			var vitals = _simulation.GetOrAddPlayerVitals(client);
			vitals.Health = Math.Max(1, Math.Min(vitals.Health, vitals.MaxHealth * percent / 100));
			vitals.Mana = Math.Min(vitals.Mana, vitals.MaxMana * percent / 100);
			vitals.Stamina = Math.Min(vitals.Stamina, vitals.MaxStamina * percent / 100);
			// ⚠️ EXPERIMENT (2026-08-30, in-client): the player's own bar does not move for ANY vitals push we send.
			// `--report vitals-shape` says retail's own-handle health change is a SINGLE-stat block - {HealthCurrent}
			// alone, 211 captured - while the six-record current+max form is a SEED (93, at entry/stream-in) and the
			// three-current form is what it sends about OTHER entities (4,676) and never about you. We have only ever
			// sent the latter two. This sends retail's shape; if the bar moves, the shape is the whole bug.
			await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, vitals.Health), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Hurt to {percent}%: health {vitals.Health}/{vitals.MaxHealth}, mana {vitals.Mana}/{vitals.MaxMana}, stamina {vitals.Stamina}/{vitals.MaxStamina}.");
		}

		// .ah  - open the auction house at the player's position (PoC bootstrap for the Tradepost; the real trigger
		// is interacting with an auctioneer NPC). Sends DispatchOpenTradepost so the client opens the AH window and
		// then sends TradepostOpen + searches, which HandleTradepostPacket answers with listings.
		private async Task OpenAuctionHouseCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No world entity for the player yet.");
				return;
			}

			var bankCount = await SendAuctionHouseOpen(client, entity.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened the auction house (PoC) at your position; bank tab has {bankCount} item(s).");
		}

		// .position  - print the caller's current map + live position (the in-world entity transform when
		// connected, else the persisted Character position). Formatted so the x y z can be pasted into .teleport.
		// .resist <knockback|criminalteam|<metaTypeId>> [percentage]  - push a spell RESISTANCE, 0 to remove it (#869).
		//
		// The retail trigger for the only metatype we have seen in quantity is a `Criminal Protection` buff nothing
		// grants yet (#503), and the other is knockback immunity for a knockback that is not implemented (#496) - so
		// this is the standard GM bootstrap for a decoded message with no live trigger.
		//
		// A metatype is an id into LDB category 55200, so a raw id is accepted as well as the two named ones: the
		// catalog has hundreds of entries and this is the instrument for finding out what any of them does in-client.
		private async Task ResistCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					$"Usage: .resist <knockback|criminalteam|metaTypeId> [percentage]  (default {SpellMetaTypes.FullImmunity}, 0 removes)");
				return;
			}

			var metaType = parts[0].ToLowerInvariant() switch
			{
				"knockback" => SpellMetaTypes.Knockback,
				"criminalteam" => SpellMetaTypes.CriminalTeam,
				_ => uint.TryParse(parts[0], out var raw) ? raw : 0u
			};

			if (metaType == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Unknown metatype '{parts[0]}'. Use knockback, criminalteam or an LDB 55200 id.");
				return;
			}

			var percentage = SpellMetaTypes.FullImmunity;
			if (parts.Length > 1 && !uint.TryParse(parts[1], out percentage))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "The percentage must be a number (0 removes).");
				return;
			}

			// The remove carries the percentage too - retail's remove frames repeat the value the add carried, they
			// do not send 0. So a `.resist X 0` sends a REMOVE of the full-immunity value, not an add of zero.
			var add = percentage > 0;
			await SendSpellResistance(client, metaType, add ? percentage : SpellMetaTypes.FullImmunity, add);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				add ? $"Resistance to metatype {metaType} set to {percentage}%." : $"Resistance to metatype {metaType} removed.");
		}

		// .waypoint [off]  - plant a map marker at the player's current position, or take it down (#868).
		//
		// The retail trigger is a DEATH: every captured `DispatchAddWayPoint` is a TOMBSTONE pin, labelled
		// *"Tombstone"* / *"Locate your Tombstone to reduce death penalty!"* and addressed at an `EntityClass.TombStone`
		// id. We have no tombstone entity to point at yet - that rides with the death-penalty work (#497) - so this is
		// the standard GM bootstrap for a decoded message whose retail trigger is not wired.
		//
		// The marker id is the player's own handle under the tombstone class. That is a stand-in for the corpse's id,
		// and it is honest about being one: what matters for the in-client check is that the pin appears on the map at
		// the right place with the right label, and that the remove takes it down again.
		private async Task WayPointCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var pos = entity?.Position ?? character.Position;

			if ((arguments ?? "").Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
			{
				await SendRemoveWayPoint(client, new EntityId(EntityClass.TombStone, character.Id), mapId);
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Waypoint removed.");
				return;
			}

			await SendAddWayPoint(client, character.Id, mapId, pos.X, pos.Y, pos.Z);
			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Waypoint planted on map {mapId} at {pos.X.ToString("0.###", CultureInfo.InvariantCulture)} "
				+ $"{pos.Y.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Z.ToString("0.###", CultureInfo.InvariantCulture)}. Use .waypoint off to remove it.");
		}

		private Task PositionCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var pos = entity?.Position ?? character.Position;
			return _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Map {mapId} - position {pos.X.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Y.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
		}

		// .recall  - recall to the BOUND location (the persisted bind point). The retail triggers that land here are the
		// Path of Asura spell and a bindstone recall; this GM command is the test bootstrap.
		// ⚠️ NOT the Totem of Origins - that returns to the RACIAL home city and goes through RecallToRacialHomeCommand.
		// No on-screen line before the handoff: retail sends none, and the ones this used to send were invented.
		private async Task RecallToBindCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character.BindRespawnPointId is not { } pointId)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "You have no bind point. Use .bind to set one.");
				return;
			}

			var point = await Database.GetRespawnPoint(pointId);
			if (point == null)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", "Your bind point no longer exists. Re-bind with .bind."));
				return;
			}

			await _teleport.DoTeleport(character.Id, point.MapId, point.Position);
		}

		// .criminal [seconds]  - flag the calling character CRIMINAL for `seconds` (default the retail 300 s), or
		// .heroicmode <n>  - set stat 0x0A9, which the client's own descriptor registry names `HeroicMode` (#625).
		// Named for the STAT and not for a mechanic, because there is no mechanic here: nothing in retail is known to
		// set it, so a GM command is the only way the column is ever anything but 0.
		//
		// ⚠️ Was `.chosengod` until #625. The client names this stat TWICE and the two disagree - the exe's descriptor
		// registry says `HeroicMode`, LDB 615 says `ChosenGod` - and we take the exe's. See Character.HeroicMode.
		//
		// ⚠️ The argument is the RAW WIRE NUMBER. The client stores this stat and never reads it back, so nothing it
		// draws could show a wrong VALUE either way - only the name is now the client's.
		private async Task HeroicModeCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			if (!uint.TryParse((arguments ?? "").Trim(), out var value))
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id,
					$"Usage: .heroicmode <n>  (raw stat 0x0A9 value. Currently {character.HeroicMode}.)");
				return;
			}

			character.HeroicMode = value;
			await Database.SetCharacterHeroicMode(character.Id, value);
			await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.HeroicMode, value), character.Id);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"HeroicMode set to {value}.");
		}

		// `.criminal 0` to clear it. The standard test bootstrap: the retail trigger is killing another player, and PvP
		// player-vs-player combat is not modelled, so without this the criminal branches in ShopService are
		// unreachable. Also awards the measured murder points so the stat moves as it does in the capture.
		private async Task CriminalCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var seconds = CriminalStatus.CriminalSeconds;
			if (parts.Length > 0 && !int.TryParse(parts[0], out seconds))
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, $"Usage: .criminal [seconds]  (default {CriminalStatus.CriminalSeconds}, 0 clears)");
				return;
			}

			if (seconds <= 0)
			{
				character.CriminalUntil = null;
				character.MurderPoints = 0;
				await Database.SetCharacterCriminalState(character.Id, null, 0);
				await _buffs.SendBuffRemoval(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8);
				await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, 0), character.Id);
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Criminal status cleared.");
				return;
			}

			character.CriminalUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
			character.MurderPoints += CriminalStatus.PointsPerPlayerKill;
			await Database.SetCharacterCriminalState(character.Id, character.CriminalUntil, character.MurderPoints);

			// The wire side: the "Criminal" debuff plus the murder-point stat, the two things the capture shows moving.
			var spell = await Database.GetSpell(CriminalStatus.CriminalSpellId);
			await _buffs.SendMagicBuffIir(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8,
				MagicBuffDatas.ApplyNow(CriminalStatus.CriminalSpellId, (uint)(seconds * 100), BuffService.CasterLevel(client), casterIsPlayer: true));
			await SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, character.MurderPoints), character.Id);

			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Criminal for {seconds}s ({spell?.Name ?? "Criminal"} {CriminalStatus.CriminalSpellId}); murder points {character.MurderPoints}.");
		}

		// .recallhome  - use the Totem of Origins: return to the character's RACIAL home city, which is NOT the bind
		// point (see StartingData.RacialHomeMap for the mapping and the capture that pins the Stygian row). Bound
		// location vs racial origin are two separate destinations; the Totem ignores the bind entirely, so this shares
		// no state with RecallToBindCommand beyond the DoTeleport handoff.
		// No on-screen line before the handoff, same as the bind recall - retail sends none.
		private async Task RecallToRacialHomeCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			if (!StartingData.RacialHomeMap.TryGetValue(character.Race, out var homeMapId))
			{
				// Not silent: an unmapped race is a content gap in RacialHomeMap, not a corrupt character.
				Logger.LogWarning("Totem of Origins: race {Race} has no home city mapped, so {CharacterId:X8} cannot recall home.", character.Race, character.Id);
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"No home city is mapped for race {character.Race}."));
				return;
			}

			var point = await Database.GetDefaultSpawn(homeMapId);
			if (point == null)
			{
				Logger.LogWarning("Totem of Origins: home map {Map} ({Race}) has no respawn point.", homeMapId, character.Race);
				await _agentServerDispatcher.SendSystemMessage(character.Id, ChatMarkup.Color("red", $"Home map {homeMapId} has no respawn point."));
				return;
			}

			await _teleport.DoTeleport(character.Id, point.MapId, point.Position);
		}

		// .removeitem <slot>  - the inverse of .additem: clear the calling character's bag cell `slot` (persisted
		// CharacterInventory row) and push the live DispatchRemoveItem (0x201F) so it disappears without a relog.
		// `.tag <id>` / `.tag <name substring>` - award an achievement (#748). The retail TRIGGERS are per-achievement
		// content nobody has mapped yet (a vista, a kill streak, a first pet), so this is the standard GM bootstrap for
		// a feature whose real trigger is not wired: it exercises the whole path - persist, award, and the client's own
		// banner - against a catalog entry rather than a made-up id.
		//
		// No argument lists what the character already holds, which is the other half of checking this works: the award
		// is a one-shot event and the SNAPSHOT is what carries the set on a relog.
		private async Task TagCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var argument = (arguments ?? "").Trim();
			if (argument.Length == 0)
			{
				var held = await Database.GetCharacterTags(characterId);
				if (held.Length == 0)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "No tags. Usage: .tag <id> | .tag <name substring>");
					return;
				}

				var named = new List<string>();
				foreach (var tag in held.OrderBy(t => t))
					named.Add($"{tag} ({(await Database.GetAchievement(tag))?.Name ?? "unnamed"})");

				await _agentServerDispatcher.SendSystemMessage(characterId, $"{held.Length} tag(s): {string.Join(", ", named)}");
				return;
			}

			// An id, or a name to look up - a 4,095-entry catalog is not something to search by hand.
			if (!uint.TryParse(argument, out var tagId))
			{
				var matches = await Database.FindAchievements(argument, 6);
				if (matches.Length == 0)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, $"No achievement matches '{argument}'.");
					return;
				}

				if (matches.Length > 1)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"{matches.Length} matches: {string.Join(", ", matches.Select(a => $"{a.Id} {a.Name}"))}");
					return;
				}

				tagId = matches[0].Id;
			}

			var (granted, achievement) = await GrantTag(client, tagId);
			var name = achievement is null
				? "not in the achievement catalog - an item-shop flag, or an id the catalog is short of"
				: $"'{achievement.Name}'{(achievement.Description is { } d ? $" - {d}" : "")}";

			await _agentServerDispatcher.SendSystemMessage(characterId, granted
				? $"Awarded tag {tagId}: {name}"
				: $"Already held tag {tagId}: {name}");
		}

		private async Task RemoveItemCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length != 1 || !int.TryParse(parts[0], out var slot) || slot < 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .removeitem <bagSlot>");
				return;
			}

			var removed = await RemoveItemFromBackpack(client, slot);
			await _agentServerDispatcher.SendSystemMessage(characterId, removed
				? $"Removed the item in bag slot {slot} (live DispatchRemoveItem)."
				: $"No item in bag slot {slot}.");
		}

		// Bootstrap the AH window at accessPosition (must be within the 15-unit client proximity check). Shared by the
		// `.ah` GM command and the real trigger - talking to the "Trader" auctioneer NPC (GameServerListener.Inbound
		// TalkToNpc). The window's first/default tab is the BANK (personal storage), a separate channel: container kind
		// 105 delivered via a BankIIR (decoded, protocol_bank_storage), populated from the character's Container=105 rows.
		// ORDER MATTERS (fixes the old "first open shows an empty bank" quirk): retail sends the bank content (the 0x201c
		// slot registrations + the BankIIR) BEFORE DispatchOpenTradepost, so the window OPENS already populated -
		// account-faolan-...-Has-inventory-at-trader-bank F6208-6210 = [bank slot regs][BankIIR][DispOpenTradepost].
		// Sending the open FIRST landed the BankIIR before the window existed and the first open rendered empty. So:
		// bank first, open last. Returns the bank item count for the caller's feedback.
		private async Task<int> SendAuctionHouseOpen(INetworkClient client, Vector3 accessPosition)
		{
			var characterId = client.Session.Character.Id;
			var stored = await Database.GetCharacterItems(characterId, ContainerKind.Bank);
			var bank = FullCharacterRoots.Container(ContainerKind.Bank, 50, 30, characterId, stored);

			await _bank.SendBank(client, bank);

			// The GUILD vault rides the same open (#654): retail pushes DispatchGuildBankData in the SAME instant as
			// the personal bank's slot registrations and DispatchOpenTradepost, unasked - there is no OpenGuildBank
			// request on the wire at all. Before the open, for the same reason the bank is: a container that lands
			// after the window exists renders empty on the first open.
			await _guildBank.SendVault(client);
			await TradepostResponses.SendOpenTradepost(client, accessPosition);
			return bank.Items.Count;
		}

		// Lists only the commands the CALLER's privilege mask passes - the same per-bit filtering the client's own
		// /help applies to the retail command set (#904).
		private Task SendGmHelpText(INetworkClient client)
		{
			var account = client.Session.Account;
			var available = _gmCommandsDict
				.Where(e => e.Value.RequiredMask == GmPrivileges.None || account?.HasGmPrivilege(e.Value.RequiredMask) == true)
				.Select(e => e.Key)
				.Order(StringComparer.OrdinalIgnoreCase);

			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Raw($"Available Commands:<br />{available.JoinAsString("<br />")}"));
		}

		// .level <n>  - set the player's character level, regenerate its MaxStats from the new class/level, persist, 		// and push the Level stat + refreshed vitals so the change shows without a relog (the persisted level also
		// rides the next login snapshot). Test-bootstrap convenience for level-gated features (e.g. #206 Totem of
		// Origin's min-20). The stat's wire scale is applied at send.
		private async Task SetLevelCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var parts = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
			if (parts.Length == 0 || !ushort.TryParse(parts[0], out var level) || level == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .level <n>  (1..65535)");
				return;
			}

			// Everything a level change entails - persist, ding burst, attribute lines, vitals, rating quad, the
			// level-gated ability/spell unlocks and their castability grants - belongs to the progression service, so a
			// GM set and an XP-driven ding are LITERALLY the same code rather than two copies held in step by comment.
			// They were two copies, and the comments said so ("must not diverge").
			await _levelUp.SetLevel(client, level);

			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
		}

		// .setnpcgroup [<group name>] [clear]  - manual creature assignment for an RDB spawn AREA (#363). Target/select
		// an NPC standing in the area (its type IS what the area spawns), then run this:
		//   .setnpcgroup                 -> assign the SELECTED NPC's creature to the area the NPC stands in
		//   .setnpcgroup <group>         -> assign it to the named area on that map
		//   .setnpcgroup clear           -> CLEAR the area the NPC stands in (back to unresolved)
		//   .setnpcgroup <group> clear   -> clear the named area
		// A set marks the area Manual (never re-guessed); a clear reverts NpcTemplateId to null so the resolver may
		// re-guess it. The dev nameplate labels (.togglenpcgroup) show each NPC's area so you know what to type.
		private async Task SetNpcGroupCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null || client.Session.SelectedTargetHandle is not { } handle || !_simulation.World.ZoneForEntity(player).TryGet(handle, out var npc) || npc.NpcTemplateId == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Select an NPC first, then .setnpcgroup [<group name>] [clear]  (no arg = the area the NPC stands in).");
				return;
			}

			var areas = await Database.GetNpcSpawnAreas(npc.MapId);
			if (areas.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Map {npc.MapId} has no RDB spawn areas (scripted/instanced playfield).");
				return;
			}

			// A trailing "clear"/"none"/"-" makes this a CLEAR; the rest (if any) is the target group name.
			var tokens = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var clearing = tokens.Length > 0 && tokens[^1] is "clear" or "none" or "-";
			var groupName = string.Join(' ', clearing
				? tokens[..^1]
				: tokens);

			var area = groupName.Length > 0
				? areas.Where(a => string.Equals(a.GroupName, groupName, StringComparison.OrdinalIgnoreCase)).OrderBy(a => AreaDistanceSq(a, npc.Position)).FirstOrDefault()
				: NearestContainingArea(areas, npc.Position);
			if (area == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, groupName.Length > 0
					? $"No spawn area named '{groupName}' on map {npc.MapId}."
					: $"'{StripAreaLabel(npc.Name)}' isn't inside any spawn area on map {npc.MapId} - pass the group name explicitly.");
				return;
			}

			if (clearing)
			{
				await Database.SetNpcSpawnAreaCreature(area.Id, null);
				var unlinked = await Database.UnlinkNpcSpawnsFromArea(area.Id);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Spawn area '{area.GroupName}' (map {npc.MapId}) cleared - creature unassigned, {unlinked} captured spawn(s) un-linked.");
				return;
			}

			await Database.SetNpcSpawnAreaCreature(area.Id, npc.NpcTemplateId);
			var linked = await Database.LinkNpcSpawnsToArea(area.Id, npc.NpcTemplateId, npc.SpawnPosition.X, npc.SpawnPosition.Z);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Spawn area '{area.GroupName}' (map {npc.MapId}) now spawns '{StripAreaLabel(npc.Name)}' [manual]; linked {linked} captured spawn(s) (graduate to remove them).");
		}

		// .syschat - push a world/system chat line into the chat window via ChatTextIIR (#103). Sends the captured
		// localized CriteriaYou line (arbitrary literal text needs a literal-text remoteformat template, like the
		// FIFO banner). The test bootstrap for the ChatTextIIR path.
		private async Task SysChatCommand(INetworkClient client)
		{
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "Sent a world/system chat line (ChatTextIIR).");
		}

		// .toast <text>  - push ARBITRARY literal text to the top-of-screen FIFO banner ("toast"), NOT the chat window
		// (#664). The GenericDisplayAreaName FIFO template resolves to the format body `{DisplayedText}`, so a literal
		// type-7 parameter renders verbatim - the same envelope the area-name banner uses, minus the nested <localized>.
		// The test bootstrap for DynamicAreaBannerCodec; the mechanism the closed-as-impossible #664 said had no path.
		private async Task ToastCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var text = (arguments ?? "").Trim();
			if (text.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .toast <text>   (shows the text as a top-of-screen banner)");
				return;
			}

			await SendFeedback(client, new DynamicAreaBanner(text));
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Toasted: '{text}'");
		}

		// .target  - print info about the player's currently SELECTED target (last in-game LookAt/SelectDeselect):
		// name, wire handle, kind, template id, level, vitals, hostility, position, and the spawn area/group it belongs
		// to (the #363 labeling aid - read the group without the nameplate toggle). Reported in-game via system message.
		private async Task TargetInfoCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			if (client.Session.SelectedTargetHandle is not { } handle)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No target selected - click an NPC/object in-world first, then .target.");
				return;
			}

			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world.");
				return;
			}

			// A live ENTITY (player / NPC / loot bag) in the zone entity model.
			if (_simulation.World.ZoneForEntity(player).TryGet(handle, out var t))
			{
				var kind = t.IsPlayer
					? "player"
					: t.IsLoot
						? "loot"
						: "npc";
				var vitals = t.Vitals != null
					? $"{t.Vitals.Health}/{t.Vitals.MaxHealth} hp"
					: "no vitals";
				var group = t.OverheadNameOverride ?? (t.SpawnAreaId is { } id
					? $"area #{id} (unlabeled)"
					: "none");
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Lines(ChatMarkup.Raw($"{ChatMarkup.Bold(t.Name)}  (handle 0x{handle:X8}, {kind})"),
						$"template {t.NpcTemplateId} · level {t.Level} · {vitals} · {(t.Hostile ? "hostile" : "friendly")} (disp {t.Disposition})",
						$"pos ({t.Position.X:F1}, {t.Position.Y:F1}, {t.Position.Z:F1}) · map {t.MapId} / instance 0x{t.InstanceId:X8}", $"spawn group: {group}"));
				return;
			}

			// Not a live entity - a static WORLD OBJECT (chest/door/dynel) isn't in the entity model, it hangs off the
			// loaded zone. Resolve it there so a selected chest/door still reports.
			if (_simulation.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
			{
				var obj = live.Row;
				var oname = WorldObjectDisplay.ToWire(obj) ?? obj.Name ?? "(unnamed object)";
				// A name-resolved zone-line (#341) knows its destination map but no arrival - report the map either way,
				// and say which the row is, because "no arrival" is exactly what sends the player to the default spawn.
				var dest = obj.DestinationMapId is { } destMap
					? $" · teleports to map {destMap} {(obj.DestinationPosition is { } dp ? $"({dp.X:F1}, {dp.Y:F1}, {dp.Z:F1})" : "(no captured arrival - default spawn)")}"
					: "";
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Lines(ChatMarkup.Raw($"{ChatMarkup.Bold(oname)}  (handle 0x{handle:X8}, world-object)"),
						$"objectId {obj.ObjectId} · item template {obj.TemplateItemId?.ToString() ?? "none"} · subop 0x{obj.SubOpcode:X8} · {(live.IsClosed ? "closed" : "open")}",
						$"interact {obj.InteractAction} · carsGroup {obj.CarsGroup}{dest}", $"pos ({obj.Position.X:F1}, {obj.Position.Y:F1}, {obj.Position.Z:F1}) · map {obj.MapId}"));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Selected handle 0x{handle:X8} isn't a live entity or a known world object in your zone (it may have despawned).");
		}

		// .teleport <mapId>  - move the character to another playfield via the retail HANDOFF + RECONNECT.
		//
		// Retail never changes playfield in place: it sends a 0x2002 world-address advert on the current
		// connection, the client opens a FRESH connection and does a full world entry there (changemap capture
		// account-faolan-...-changemap: :7027 -> :7030, frame 4214 advertises gs013-nj4...:7030). This matters
		// because the client only loads a playfield's resources (camera paths, geometry, NPCs) during a fresh
		// world entry - confirmed via Ghidra: camera paths are an RDB resource (e_RDB_CameraPaths, type 1000047)
		// keyed by playfield id, loaded by CameraPathMap_c::LoadPlayfieldPaths only on the n3Camera init a fresh
		// entry runs (see docs/subsystems/create-cutscene-camera-paths.md). A soft in-place MapChange on the live connection
		// does NOT trigger that reload, so the old playfield's resources stay loaded - that was the teleport bug.
		//
		// The handoff is the SAME packet (same 0xb171ce13 -> 0x6c67c147 conversation, byte-identical) the create
		// enter-world flow already sends (SendWorldHandoff), proving our single-host server CAN do it: it
		// advertises our own GameServer, the client reconnects, and the existing-character Hello path runs
		// SendWorldEntry for the (now persisted) target map - identical to logging in on that map, which works.
		// .teleport <mapId | name>  - resolves the argument to a map (numeric id, or a case-insensitive name match
		// so `.teleport Khemi` works) then hands off to DoTeleport. Also accepts `.teleport <x> <y> <z>` (three numeric
		// tokens) to move to a position on the CURRENT map. Gives a usage/echo message instead of throwing on a
		// missing/garbled argument (the old `uint.Parse(arguments)` surfaced a raw FormatException to the player).
		// .knockback [distance] - shoves the player away from their current TARGET (or, with no target, away from the
		// direction they are facing) using the real wire path: the forced-move MoveTask + SetPos pair plus the
		// "Knockback immunity" buff retail applies alongside it (#496).
		//
		// This is the standard GM bootstrap for a feature whose retail trigger is not wired. Here the trigger is
		// genuinely unknown rather than merely unbuilt: the corpus has ONE knockback and the ApplySpell in that batch
		// names the immunity guard, not the ability that did the knocking, so nothing says which attacks knock back.
		private async Task KnockbackCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", "You are not in the world."));
				return;
			}

			var distance = Knockback.DefaultDistance;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length > 0 && !float.TryParse(arg, NumberStyles.Float, CultureInfo.InvariantCulture, out distance))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .knockback [distance]");
				return;
			}

			// The SOURCE is what the player is pushed away from. Their current target is the natural stand-in for the
			// attacker; with none, push away from a point just in front of them so the command still demonstrates the
			// mechanic (being shoved backwards) instead of doing nothing.
			var zone = _simulation.World.ZoneForEntity(entity);
			Vector3 source;
			if (client.Session.SelectedTargetHandle is { } target && zone != null && zone.TryGet(target, out var targetEntity))
			{
				source = targetEntity.Position;
			}
			else
			{
				var facing = Vector3.Transform(new Vector3(0f, 0f, 1f), entity.Rotation);
				source = entity.Position - facing;
			}

			if (await _knockback.Knock(entity, source, distance))
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Knocked back {distance:F2} units.");
			else
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", "Not displaced (no direction to push along, or fully resisted)."));
		}

		// .travel <npc name> [answer path] - drives local travel (#506/#514). With no path it LISTS what that NPC
		// offers; with one it runs that trip directly.
		//
		// Since #514 the retail trigger IS wired - a wagoneer's destination pick inside the ordinary Knubot
		// conversation runs the whole flow - so this is no longer the only way in. It stays as the diagnostic: it names
		// the answer path of every destination, which is the wiring key, and runs a trip without walking the tree.
		// ⚠️ It deliberately skips the FARE (that is charged one screen earlier, when the destination list is served),
		// so a GM trip is free.
		private async Task TravelCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .travel <npc name> [option id]");
				return;
			}

			// The destination's OPTION ID, when present, is the LAST whitespace-separated token - NPC names contain
			// spaces, so splitting from the front would break on every real traveller, and no NPC name ends in digits.
			var split = arg.LastIndexOf(' ');
			var npcName = arg;
			uint? optionId = null;
			if (split > 0 && arg[(split + 1)..] is { Length: > 0 } tail && uint.TryParse(tail, out var parsedOption))
			{
				npcName = arg[..split].Trim();
				optionId = parsedOption;
			}

			var destinations = await _travel.DestinationsOf(npcName);
			if (destinations.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"\"{npcName}\" offers no travel destinations."));
				return;
			}

			if (optionId == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"\"{npcName}\" travels to:");
				foreach (var destination in destinations)
				{
					var map = await Database.GetMap(destination.DestinationMapId);
					await _agentServerDispatcher.SendSystemMessage(characterId,
						$"  option {destination.OptionId} -> {destination.DestinationMapId} {(string.IsNullOrEmpty(map?.Name) ? "" : map.Name)} - {destination.Cost} tin");
				}

				return;
			}

			// The GM form names the destination alone; the screen it hangs off is whichever of this NPC's screens
			// offers it, which the row already states.
			var picked = Array.Find(destinations, d => d.OptionId == optionId);
			if (picked == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"\"{npcName}\" sells no trip on option {optionId}."));
				return;
			}

			await _travel.Travel(client, npcName, picked);
		}

		private async Task TeleportCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					"Usage: .teleport <mapId | name>  or  .teleport <x> <y> <z>  (current map)  or  .teleport <mapId | name> <x> <y> <z>  (see .maps)"
					+ "  -  retail's own form is /teleport <x> <z> [y <y>] [playfield], which is what the client's map sends");
				return;
			}

			// Invariant culture throughout so a comma-decimal locale doesn't reject the dotted coordinates.
			var tokens = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

			// RETAIL GRAMMAR (#913). The client's own hidden GM affordance - right-click the map -> "teleport to
			// location" - emits `/teleportself <posx> <posz>`: TWO coordinates and no Y, on the current playfield.
			// Recovered from FanaticTeleportCommand_c's parameter descriptor (docs/re/gm-command-args.tsv), which
			// declares the family as
			//     posx posz [[y posy][playfield]] [proxy model type group subgroup running]
			// so Y is optional and arrives behind a literal `y` token, with the playfield after it. We used to parse
			// the whole argument as a map name and answer "No map matching '534 657'", which is what made every one
			// of those clicks fail. The forms below are RETAIL's; ours (`<x> <y> <z>`, `<map>`) are unchanged and
			// handled after them - a 3-token form is unambiguously ours, since retail's third token is the literal `y`.
			if (tokens.Length is 2 or 4 or 5 &&
			    float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var retailX) &&
			    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var retailZ) &&
			    (tokens.Length == 2 || tokens[2].Equals("y", StringComparison.OrdinalIgnoreCase)))
			{
				var entity = _simulation.PlayerWorldEntity(client);
				var retailMapId = entity?.MapId ?? client.Session.Character.MapId;

				// `[playfield]` only ever follows the y-group, so it cannot appear on the 2-token form.
				if (tokens.Length == 5 && uint.TryParse(tokens[4], out var statedMapId))
					retailMapId = statedMapId;

				float retailY;
				if (tokens.Length >= 4 && float.TryParse(tokens[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var statedY))
					retailY = statedY;
				else if (_simulation.Terrain?.GroundHeight(retailMapId, retailX, retailZ) is { } ground)
					retailY = ground;
				else
				{
					// No baked terrain for this map, so there is no height to snap to. Keeping the caller's current Y
					// lands them at the right X/Z at the wrong altitude, which is recoverable and honest - say so
					// rather than silently teleporting them under the world.
					retailY = entity?.Position.Y ?? 0f;
					await _agentServerDispatcher.SendSystemMessage(characterId,
						ChatMarkup.Color("orange", $"No terrain loaded for map {retailMapId} - keeping your current height ({retailY:0.#})."));
				}

				await _teleport.DoTeleport(characterId, retailMapId, new Vector3(retailX, retailY, retailZ));
				return;
			}

			// .teleport <x> <y> <z>  - three numeric tokens = a position on the CURRENT map (no map change). Ours.
			if (tokens.Length == 3 && float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
			    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py) && float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
			{
				await _teleport.DoTeleport(characterId, client.Session.Character.MapId, new Vector3(px, py, pz));
				return;
			}

			// .teleport <mapId | name> <x> <y> <z>  - jump to a precise spot on a NAMED (or numeric) map in one command
			// (#247). The map is the first token, the trailing three are the coordinates; the map name itself may contain
			// spaces, so everything before the last three tokens is the map argument.
			if (tokens.Length >= 4 && float.TryParse(tokens[^3], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpx) &&
			    float.TryParse(tokens[^2], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpy) && float.TryParse(tokens[^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var mpz))
			{
				var mapArg = string.Join(' ', tokens[..^3]);
				var resolved = await ResolveMap(mapArg);
				if (resolved == null)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{mapArg}'. Try .maps."));
					return;
				}

				await _teleport.DoTeleport(characterId, resolved.Id, new Vector3(mpx, mpy, mpz));
				return;
			}

			if (uint.TryParse(arg, out var mapId))
			{
				await _teleport.DoTeleport(characterId, mapId);
				return;
			}

			var match = await ResolveMap(arg);
			if (match == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{arg}'. Try .maps."));
				return;
			}

			await _teleport.DoTeleport(characterId, match.Id);
		}

		// .godmode  - toggle invincibility on yourself: hostile NPC swings land their animation but deal no damage.
		// The toggle is persisted on the account (#279) and re-applied to the live WorldEntity on every world entry, 		// so it survives relog AND map changes (a playfield change is a fresh world connection / entity). Self-target only.
		private async Task ToggleGodModeCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			entity.GodMode = !entity.GodMode;
			client.Session.Account.SetGmPrivilege(GmPrivileges.GodMode, entity.GodMode);
			await Database.SaveAccount(client.Session.Account);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.GodMode
				? ChatMarkup.Raw($"God mode {ChatMarkup.Color("lime", "ON")} - you take no damage.")
				: ChatMarkup.Raw($"God mode {ChatMarkup.Color("red", "OFF")}."));
		}

		// .invisible  - toggle aggro invisibility on yourself: hostile NPCs won't target/aggro you (skipped in the
		// sim's NearestPlayerInRange). Persisted on the account (#279) and re-applied on each world entry, so it
		// survives relog and map changes. Self-target only.
		private async Task ToggleInvisibleCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			entity.Invisible = !entity.Invisible;
			client.Session.Account.SetGmPrivilege(GmPrivileges.InvisibleToMonsters, entity.Invisible);
			await Database.SaveAccount(client.Session.Account);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.Invisible
				? ChatMarkup.Raw($"Invisible {ChatMarkup.Color("lime", "ON")} - NPCs won't aggro you.")
				: ChatMarkup.Raw($"Invisible {ChatMarkup.Color("red", "OFF")}."));
		}

		// .togglenpcgroup  - toggle the dev spawn-area nameplate labels (#363) live for this session: each NPC that
		// resolves to an RDB spawn AREA shows that area's "group | tier xN" over its head (pushed via SetNameIIR, since
		// the client draws NPC names from the template LDB and ignores the spawn name). Pairs with .setnpcgroup: read
		// the area off the nameplate, assign with .setnpcgroup. Off by default; the toggle re-applies to nearby NPCs now.
		private async Task ToggleNpcGroupCommand(INetworkClient client)
		{
			var (on, affected) = await _broadcaster.ToggleAreaLabels(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"Spawn-area nameplate labels {(on ? "ON" : "OFF")} - {affected} nearby NPC(s) updated. Walk to re-stream the rest.");
		}

		// .weaponset [primary | secondary]  - the server-side test bootstrap for the SwitchWeaponsButton set switch (#400).
		// No arg (or "swap"/"toggle") flips the active loadout; "primary"/"secondary" (or 1/2) forces one. Persists the
		// set and mirrors it on the live entity so the login-time gear-stat aggregation (which counts only the active
		// set's weapons) uses it. NB this drives the MODEL only - the cross-player swap RELAY needs the real BCC payload,
		// so exercise that with the retail weapon-swap key against a live viewer.
		// .tradeskill [<profession> [<rank>]]  - train a tradeskill profession (#735). No argument reports what the
		// character has trained and what each profession's ladder looks like.
		//
		// This is the standard GM bootstrap for a feature whose RETAIL trigger is not wired: the learn path is a
		// trainer conversation and no capture shows one, so nothing here can be built from the corpus. What IS
		// retail is everything downstream - the ladder, the rank, and the recipe book the rank buys - so a GM grant
		// puts a character in a state the rest of the system reads exactly as it would a trained one.
		//
		// Trains the whole chain UP TO the named rank, because that is what a character who reached it holds: each
		// rung names the one below it as its prerequisite. `AddCharacterFeats` collapses the rows per base and keeps
		// the highest of each, so the stored set is the same 3 rows retail's would be.
		private async Task TradeskillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var words = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var ladders = await _tradeskill.Ladders();

			if (words.Length == 0)
			{
				var trained = await _tradeskill.ProfessionsOf(characterId);
				var lines = ladders.Select(l => trained.TryGetValue(l.Name, out var rank)
					? $"{l.Name}: {l.RankName(rank)} ({l.RankFeatIds.Length} rank(s) on its ladder)"
					: $"{l.Name}: -").ToList();

				lines.Add($"Usage: .tradeskill <profession> [{string.Join('|', TradeskillLadders.RankNames)}]");
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. lines]));
				return;
			}

			var ladder = ladders.FirstOrDefault(l => l.Name.Equals(words[0], StringComparison.OrdinalIgnoreCase));
			if (ladder == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId,
					ChatMarkup.Color("red", $"No tradeskill profession '{words[0]}'. Known: {string.Join(", ", ladders.Select(l => l.Name))}."));
				return;
			}

			// Default to the top of the ladder rather than to Novice: a GM asking for a profession by name almost
			// always wants to exercise the whole book, and the narrow case is the one worth typing out.
			var wanted = ladder.RankFeatIds.Length - 1;
			if (words.Length > 1)
			{
				var named = TradeskillLadders.RankNames.IndexOf(words[1]);
				if (named < 0 || named >= ladder.RankFeatIds.Length)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId,
						ChatMarkup.Color("red", $"No rank '{words[1]}' on {ladder.Name}. Known: {string.Join(", ", TradeskillLadders.RankNames.Take(ladder.RankFeatIds.Length))}."));
					return;
				}

				wanted = named;
			}

			var trainedCount = await Database.AddCharacterFeats(characterId, ladder.RankFeatIds.Take(wanted + 1));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Trained {ladder.Name} to {ladder.RankName(wanted)} ({trainedCount} feat row(s) written). Reopen the crafting window to see the book.");
			Logger.LogInformation("GM: {Character} trained {Profession} to {Rank}.", client.Session.Character.Name, ladder.Name, ladder.RankName(wanted));
		}

		private async Task WeaponSetCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			WeaponSet set;
			if (arg.Equals("secondary", StringComparison.OrdinalIgnoreCase) || arg == "2")
				set = WeaponSet.Secondary;
			else if (arg.Equals("primary", StringComparison.OrdinalIgnoreCase) || arg == "1")
				set = WeaponSet.Primary;
			else
			{
				var toggled = await _broadcaster.TogglePlayerWeaponSet(client);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Weapon set toggled -> {toggled} (persisted).");
				return;
			}

			await _broadcaster.SetPlayerWeaponSet(client, set);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Weapon set -> {set} (persisted).");
		}

		private static float AreaDistanceSq(NpcSpawnArea a, Vector3 pos)
		{
			var cx = (a.BoundsMinX + a.BoundsMaxX) * 0.5f;
			var cz = (a.BoundsMinZ + a.BoundsMaxZ) * 0.5f;
			return (pos.X - cx) * (pos.X - cx) + (pos.Z - cz) * (pos.Z - cz);
		}

		// The RDB spawn area a position sits in (nearest centroid; bounds padded), or null when outside every area.
		private static NpcSpawnArea? NearestContainingArea(IReadOnlyList<NpcSpawnArea> areas, Vector3 pos)
		{
			const float pad = 5f;
			NpcSpawnArea? best = null;
			var bestDistSq = float.MaxValue;
			foreach (var a in areas)
			{
				if (pos.X < a.BoundsMinX - pad || pos.X > a.BoundsMaxX + pad || pos.Z < a.BoundsMinZ - pad || pos.Z > a.BoundsMaxZ + pad)
					continue;

				var d = AreaDistanceSq(a, pos);
				if (d < bestDistSq)
				{
					bestDistSq = d;
					best = a;
				}
			}

			return best;
		}

		// Strips the " [Area]" dev-label suffix (DebugSpawnAreaLabels) from a nameplate so feedback shows the creature.
		private static string? StripAreaLabel(string? name)
		{
			var i = name?.LastIndexOf(" [", StringComparison.Ordinal) ?? -1;
			return i >= 0
				? name![..i]
				: name;
		}

		internal delegate Task GmCommandDelegate(INetworkClient client, string arguments);

		// A registered GM command: its handler and the gm_level bit(s) it requires. None = unrestricted (any GM),
		// the client's own convention for an unmasked command; a multi-bit mask passes on ANY set bit, the client's
		// `(level & mask) != 0` test (#904).
		internal sealed record GmCommandEntry(GmCommandDelegate Handler, GmPrivileges RequiredMask);
	}
}
