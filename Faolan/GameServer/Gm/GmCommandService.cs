using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
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
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Progression;
using Faolan.GameServer.Combat;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Feats;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Gm
{
	/// <summary>
	///     The `.command` GM commands: the table, the privilege gate and every handler. This file holds the commands
	///     written for this server; the Retail partial holds the client's own verbs. The listener routes a chat line to
	///     <see cref="Handle" /> and nothing else.
	/// </summary>
	public sealed partial class GmCommandService
	{
		private readonly ILogger _logger;
		private readonly IDatabaseRepository _database;
		private readonly ConnectionRegistry _connections;
		private readonly IChatServerDispatcher _agentServerDispatcher;
		private readonly ICsPlayerAgentDispatcher _csPlayerAgentDispatcher;
		private readonly WorldSimulation _simulation;
		private readonly WorldBroadcaster _broadcaster;
		private readonly NpcBroadcaster _npcs;
		private readonly RealmClock _clock;
		private readonly TeleportService _teleport;
		private readonly TravelService _travel;
		private readonly CombatService _combat;
		private readonly EconomyOutbound _economyOutbound;
		private readonly TradeskillService _tradeskill;
		private readonly KnockbackService _knockback;
		private readonly EquipmentService _equipment;
		private readonly BuffService _buffs;
		private readonly WorldObjectService _worldObjects;
		private readonly QuestProgressionService _questProgression;
		private readonly PlayerDeathService _playerDeaths;
		private readonly PetService _pets;
		private readonly LevelUpService _levelUp;
		private readonly GuildBankService _guildBank;
		private readonly FeatService _feats;
		private readonly FactionService _factions;
		private readonly ClaimService _claims;
		private readonly BankService _bank;
		private readonly PlayerPersistenceService _persistence;
		private readonly TagService _tags;
		private readonly TradepostService _tradepost;

		// The `.command` table, built once per service and therefore once per realm. It must not become a static: the
		// delegates are instance-bound, so a shared table would leak one realm's handlers into another.
		private readonly AlternateAdvancementService _alternateAdvancement;
		private readonly Dictionary<string, GmCommandEntry> _gmCommandsDict;

		public GmCommandService(ILogger logger, IDatabaseRepository database, ConnectionRegistry connections, IChatServerDispatcher agentServerDispatcher,
			ICsPlayerAgentDispatcher csPlayerAgentDispatcher, WorldSimulation simulation, WorldBroadcaster broadcaster, NpcBroadcaster npcs, RealmClock clock,
			TeleportService teleport, TravelService travel, CombatService combat, EconomyOutbound economyOutbound, TradeskillService tradeskill,
			KnockbackService knockback, EquipmentService equipment, BuffService buffs, WorldObjectService worldObjects, QuestProgressionService questProgression,
			PlayerDeathService playerDeaths, PetService pets, LevelUpService levelUp, GuildBankService guildBank, FeatService feats, FactionService factions,
			ClaimService claims, BankService bank, PlayerPersistenceService persistence, TagService tags, TradepostService tradepost,
			AlternateAdvancementService alternateAdvancement)
		{
			_logger = logger;
			_database = database;
			_connections = connections;
			_agentServerDispatcher = agentServerDispatcher;
			_csPlayerAgentDispatcher = csPlayerAgentDispatcher;
			_simulation = simulation;
			_broadcaster = broadcaster;
			_npcs = npcs;
			_clock = clock;
			_teleport = teleport;
			_travel = travel;
			_combat = combat;
			_economyOutbound = economyOutbound;
			_tradeskill = tradeskill;
			_knockback = knockback;
			_equipment = equipment;
			_buffs = buffs;
			_worldObjects = worldObjects;
			_questProgression = questProgression;
			_playerDeaths = playerDeaths;
			_pets = pets;
			_levelUp = levelUp;
			_guildBank = guildBank;
			_feats = feats;
			_factions = factions;
			_claims = claims;
			_bank = bank;
			_persistence = persistence;
			_tags = tags;
			_tradepost = tradepost;
			_alternateAdvancement = alternateAdvancement;
			_gmCommandsDict = BuildTable();
		}

		/// <summary>
		///     `.additem &lt;itemId&gt; [count]` - adds an item, by faolan.db Items id, to the calling character's bag:
		///     persists a CharacterInventory row in the first free bag slot, then delivers it live.
		/// </summary>
		/// <remarks>
		///     Without a stashed snapshot template - pre-world, or a non-replay session - the item still persists and
		///     renders on the next login.
		/// </remarks>
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

			// A missing Items row is the only refusal: a harvested hash is not required, which is what keeps the 232
			// never-captured templates reachable - and .additem is the standard bootstrap for testing exactly those.
			var item = await _database.GetItem(itemId);
			if (item == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"No item with id {itemId}.");
				return;
			}

			// Persist and deliver live through DispatchAddItem (0x2020), so it appears without a relog.
			var slot = await _economyOutbound.GrantItemToBackpack(client, itemId, count);
			if (slot == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Bag is full (24 cells).");
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, $"Added {count}x '{item.Name}' (id {itemId}) to bag slot {slot} (live DispatchAddItem).");
		}

		/// <summary>
		///     `.ban &lt;account|character name&gt;` bans an account (State = Banned; login rejects any non-Active
		///     account) and `.unban` lifts it (State = Active). Either the account username or one of its character names
		///     is accepted.
		/// </summary>
		/// <remarks>
		///     Banning also kicks the account's live world connections: the State change alone blocks only the next login.
		///     An account cannot ban itself.
		/// </remarks>
		private async Task BanCommand(INetworkClient client, string arguments, bool ban)
		{
			var gmId = client.Session.Character.Id;
			var name = arguments.Trim();
			if (string.IsNullOrEmpty(name))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, $"Usage: .{(ban ? "ban" : "unban")} <account or character name>");
				return;
			}

			// Resolve the target account: by username first, else by a character name and its owning account.
			var account = await _database.GetAccount(name);
			if (account == null && await _database.GetCharacterByName(name, client.Session.Character.RealmId) is { } ch)
				account = await _database.GetAccount(ch.AccountId);

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

			if (!await _database.SetAccountState(account.Id, ban
				    ? AccountState.Banned
				    : AccountState.Active))
			{
				await _agentServerDispatcher.SendSystemMessage(gmId, ChatMarkup.Color("red", "Error changing account state."));
				return;
			}

			var kicked = 0;
			if (ban)
			{
				// Snapshot before iterating: Stop() raises Disconnected, which mutates the client set.
				foreach (var c in _connections.Clients.Where(nc => nc.Session.Account?.Id == account.Id).ToList())
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

		/// <summary>
		///     `.delete [&lt;handle&gt;]` - permanently removes the player's currently selected target, or the given
		///     handle: despawns it live for nearby players and deletes its NpcSpawn rows so it never renders again. Unlike
		///     a plain live despawn, which comes back on relog, this is durable.
		/// </summary>
		/// <remarks>
		///     The tool for scrubbing captured import artifacts - phantom players that happened to be in the capture when
		///     the world was recorded. A hostile faction mob deletes only the rows at the target's spot, so a same-name mob
		///     elsewhere survives; a neutral named entity deletes every same-name row on the map, because its
		///     cross-session captures collapse to one render.
		/// </remarks>
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
				// Not a live entity - it may be a static world object (chest, door, dynel), which lives only in the database.
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

			var deleted = await _database.DeleteNpcSpawns(entity.MapId, entity.Name, entity.Hostile
				? entity.Route.SpawnPosition.X
				: null, entity.Hostile
				? entity.Route.SpawnPosition.Y
				: null, entity.Hostile
				? entity.Route.SpawnPosition.Z
				: null, 10f);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Deleted {entity.Name} (0x{handle:X8}) - removed live + {deleted} spawn row(s) (gone on relog too).");
		}

		private async Task MatchStartCommand(INetworkClient client)
		{
			var outcome = await _csPlayerAgentDispatcher.ForcePvpMatchStart();
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"matchstart: {outcome}");
		}

		/// <summary>
		///     `.matchstop [reason]` - ends the caller's started PvP match immediately, without waiting out its clock. The
		///     client's own abort path: every member gets DispatchForceStopMinigame with the reason line, then the match
		///     dissolves and each member is put back where the countdown teleport found them.
		/// </summary>
		/// <remarks>The test bootstrap for the end half of the match loop, as `.matchstart` is for the start half.</remarks>
		private async Task MatchStopCommand(INetworkClient client, string arguments)
		{
			var outcome = await _csPlayerAgentDispatcher.ForcePvpMatchStop(client.Session.Character.Id, (arguments ?? "").Trim());
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, $"matchstop: {outcome}");
		}

		/// <summary>
		///     `.graduatenpcgroup &lt;group name&gt;` - graduates a resolved spawn area: deletes the captured NpcSpawn rows
		///     linked to it, materialised by the resolver's auto-link and `.setnpcgroup`, so the ambient pool becomes the
		///     sole source of that population and the captured/ambient density overlap goes away.
		/// </summary>
		/// <remarks>
		///     A clean foreign-key delete on the membership link, so a named or quest NPC in the area - which is never
		///     linked - is safe. Durable, and takes effect on the next zone reload; re-running the `npcs` importer
		///     resurrects the captured rows.
		/// </remarks>
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
			var matches = (await _database.GetNpcSpawnAreas(mapId)).Where(a => string.Equals(a.GroupName, name, StringComparison.OrdinalIgnoreCase)).ToList();
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
				var n = await _database.GraduateSpawnArea(area.Id);
				if (n > 0)
					removed += n;
			}

			var creature = resolved[0].NpcTemplate?.Name ?? "?";
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Graduated '{name}' ({creature}) on map {mapId}: removed {removed} captured spawn row(s); the ambient pool now owns this population. Takes effect on zone reload (relog).");
		}

		public async Task Handle(INetworkClient client, string commandText)
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
				// Per-command privilege gate: retail gates each command against the gm_level bitmask rather than a
				// boolean, so the per-bit admin UI has to bite here too - un-checking NPCCommands must stop `.kill`.
				// RequiredMask == None means unrestricted, i.e. any GM, which is the client's own convention
				// (FUN_006c86b0); reading it the other way would lock every GM out of every unmasked command. The outer
				// IsGm gates on the callers still stand - this is the per-command refinement inside them.
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
				// Echo the prefix the caller actually used: the same table serves the `.cmd` chat commands and the
				// client's own `/cmd` GM commands (FanaticCommandIIR), and telling a `/teleport` user to type `.help`
				// reads as a different tool.
				await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
					$"Unknown command: '{command}' - type {commandText[0]}help for info");
		}

		/// <summary>
		///     The alternate-advancement bootstrap: `.aa` reports the AA pools and the perk-token wallets, `.aa xp
		///     &lt;n&gt;` pays n AA xp into the PvE pool (the give-away fires if it fills), and `.aa token &lt;pool&gt;
		///     &lt;n&gt;` credits a token pool outright.
		/// </summary>
		/// <remarks>
		///     It exists because the retail trigger is out of reach: retail pays AA xp for a kill only at the level cap, so
		///     nothing on a level 1-6 dev character can fill a 1,000,000-xp pool or buy a perk. Everything below this - the
		///     announcement, the pool push, the give-away, the wallet - is the ordinary path; the command only supplies the
		///     event.
		/// </remarks>
		private async Task AlternateAdvancementCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "";

			switch (verb)
			{
				case "xp" when parts.Length == 2 && uint.TryParse(parts[1], out var amount):
					await _alternateAdvancement.AwardXp(client, amount);
					return;

				case "token" when parts.Length == 3 && Enum.TryParse<TokenPool>(parts[1], true, out var pool) && uint.TryParse(parts[2], out var count):
					await _alternateAdvancement.GrantTokens(client, pool, count);
					return;

				case "":
				{
					var wallets = await _database.GetCharacterTokens(character.Id);
					var held = wallets.Length == 0
						? "no tokens"
						: string.Join(", ", wallets.Select(w => $"{w.Pool} {w.Gained - w.Spent} ({w.Gained} gained / {w.Spent} spent)"));
					await _agentServerDispatcher.SendSystemMessage(character.Id,
						$"AA: PvE {character.AaPvEXp}/{AlternateAdvancementRules.XpCap}, PvP {character.AaPvPXp}/{AlternateAdvancementRules.XpCap}; {held}");
					return;
				}

				default:
					await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .aa | .aa xp <amount> | .aa token <pool> <count>");
					return;
			}
		}

		private Dictionary<string, GmCommandEntry> BuildTable()
		{
			return BuildGmCommands(unbackedReply: u => (client, _) => SendUnbackedVerbReply(client, u), handlers: new Dictionary<string, GmCommandDelegate>(StringComparer.OrdinalIgnoreCase)
			{
				["save"] = (client, _) => SaveChanges(client.Session.Character.Id),
				["help"] = (client, _) => SendGmHelpText(client),
				["maps"] = (client, arguments) => SendListAllMaps(client.Session.Character.Id, arguments),
				["teleport"] = TeleportCommand,
				// The retail client's own verbs for the same thing, so a GM's muscle memory works: the client
				// registers /teleport, /teleportself and /tp all under the TeleportSelf privilege, and its
				// FanaticCommandIIR routes through this same table.
				["teleportself"] = TeleportCommand,
				["tp"] = TeleportCommand,
				["position"] = (client, _) => PositionCommand(client),
				["recall"] = (client, _) => _teleport.RecallToBindCommand(client),
				["recallhome"] = (client, _) => _teleport.RecallToRacialHomeCommand(client),
				["criminal"] = CriminalCommand,
				["heroicmode"] = HeroicModeCommand,
				["travel"] = TravelCommand,
				["knockback"] = KnockbackCommand,
				["level"] = SetLevelCommand,
				["aa"] = AlternateAdvancementCommand,
				// `feat` is retail's read-only lister (mask GetStats, on your target); the manipulator is `feats`
				// (mask SetStats). A verb must not carry retail's name over someone else's meaning.
				["feat"] = (client, _) => FeatListCommand(client),
				["feats"] = _feats.FeatCommand,
				["char"] = (client, _) => CharLogCommand(client),
				["kickban"] = KickBanCommand,
				// `/inventory delete <slot>` covers what a separate `removeitem` verb would, and retail's form also
				// carries deleteall and the equip-all verbs.
				["getfaction"] = GetFactionCommand,
				["setfaction"] = SetFactionCommand,
				["magic"] = MagicCommand,
				["inventory"] = InventoryCommand,
				["gethash"] = GetHashCommand,
				["quest"] = QuestDebugCommand,
				["settimescale"] = SetTimeScaleCommand,
				["showclassstatadjustments"] = (client, _) => ShowClassStatAdjustmentsCommand(client),
				["update"] = (client, _) => UpdateRatingCommand(client),
				["godmode"] = (client, _) => ToggleGodModeCommand(client),
				["delete"] = DeleteCommand,
				["kill"] = KillCommand,
				["hurt"] = HurtCommand,
				["additem"] = AddItemCommand,
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
				// The retail client's own observability verbs, on the client's own grammar.
				//
				// `time` is not here on purpose. Retail's `/time` manipulates frame times (the deltatimer system); the
				// time-of-day setter is `/settime`, and `/gametime` carries the `reset` form, which is where retail puts
				// it.
				["settime"] = SetTimeCommand,
				["gametime"] = GameTimeCommand,
				["distance"] = (client, _) => DistanceCommand(client),
				["whereis"] = WhereIsCommand,
				["playfieldinfo"] = (client, _) => PlayfieldInfoCommand(client),
				["playfieldinstancelock"] = (client, _) => PlayfieldInstanceLockCommand(client),
				["ghost"] = GhostCommand,
				["system"] = (client, _) => SystemInfoCommand(client),
				["hatedbylist"] = (client, _) => HatedByListCommand(client),
				// Character administration and the environment and economy levers.
				["summon"] = SummonCommand,
				["unsummon"] = UnsummonCommand,
				["assist"] = AssistCommand,
				["falldamage"] = FallDamageCommand,
				["cash"] = CashCommand,
				// The combat levers.
				["inflict"] = InflictCommand,
				["areaknockback"] = AreaKnockbackCommand,
				["resurrect"] = ResurrectCommand,
				// The stat family. Pushing a raw stat to the client without touching server state is retail's
				// `/setlocal`, not its `/set`; the two verbs are one class in the client, separated by the registry
				// record's mode word at [0xC], so they are one method here.
				["get"] = GetStatCommand,
				["set"] = (client, arguments) => SetStatValueCommand(client, arguments, localOnly: false),
				["setlocal"] = (client, arguments) => SetStatValueCommand(client, arguments, localOnly: true),
				["getskill"] = GetSkillCommand,
				["teleporttarget"] = TeleportTargetCommand,
				// The broadcast family and the monster-visibility flag. `/mobs` is the InvisibleToMonsters flag with
				// account persistence, in retail's richer form: explicit on/off rather than a toggle, and it can be
				// aimed at another character.
				["broadcast"] = (client, arguments) => BroadcastCommand(client, arguments, "broadcast"),
				["servcast"] = (client, arguments) => BroadcastCommand(client, arguments, "servcast"),
				["sitecast"] = (client, arguments) => BroadcastCommand(client, arguments, "sitecast"),
				["mobs"] = MobsCommand,
				["tradeskill"] = TradeskillCommand,
				["waypoint"] = WayPointCommand,
				["resist"] = ResistCommand,
				// Force-form a PvP match from the minigame queue as it stands, however few are queued - down to one
				// member, which walks the teleport and match-instance flow solo. The queue lives on the CS tier, so
				// this crosses the same seam the team ops do.
				["matchstart"] = (client, _) => MatchStartCommand(client),
				// The end half: stop the caller's started match now instead of waiting out its time limit.
				["matchstop"] = MatchStopCommand
			});
		}

		// The gm_level bit each verb requires, joined with the handler table at construction. Two provenances, kept
		// visibly apart:
		//   * retail - the verb exists in the client's own command registry (docs/re/gm-command-registry.tsv) and
		//     the mask is retail's, reused exactly;
		//   * local - retail has no such verb (.additem, .ah, ...), so the bit is a choice by family rather than a
		//     recovered fact, and must not be cited as decoded.
		// None means unrestricted, i.e. any GM, which is the client's own convention for an unmasked command
		// (FUN_006c86b0). A multi-bit mask passes on any set bit - the client's `(level & mask) != 0` test.
		internal static readonly Dictionary<string, GmPrivileges> GmCommandMasks = new Dictionary<string, GmPrivileges>(StringComparer.OrdinalIgnoreCase)
		{
			// Retail, from the registry: /save /time /tradeskill = Developer 0x1, the /teleport family =
			// TeleportSelf 0x4, /knockback = PlayerDamage 0x10, /feat = GetStats 0x20, /level = SetStats 0x40,
			// /kill = NpcCommands 0x80000.
			["save"] = GmPrivileges.Developer,
			// Retail's own masks, from the registry: /settime /gametime /ghost /hatedbylist are Developer 0x1,
			// /whereis /playfieldinfo /playfieldinstancelock = TeleportDynels 0x8, /distance = GetStats 0x20.
			["settime"] = GmPrivileges.Developer,
			["gametime"] = GmPrivileges.Developer,
			["ghost"] = GmPrivileges.Developer,
			["hatedbylist"] = GmPrivileges.Developer,
			["whereis"] = GmPrivileges.TeleportDynels,
			["playfieldinfo"] = GmPrivileges.TeleportDynels,
			["playfieldinstancelock"] = GmPrivileges.TeleportDynels,
			["distance"] = GmPrivileges.GetStats,
			// Retail registers /system at mask 0x0, unrestricted, and that is matched rather than a bit invented. It is
			// the only verb besides `.help` that is unmasked.
			["system"] = GmPrivileges.None,
			// Again retail's own masks: /summon /assist = AssistSummon 0x2000, /unsummon = TeleportDynels 0x8,
			// /falldamage = PlayerDamage 0x10, /cash = SetStats 0x40.
			["summon"] = GmPrivileges.AssistSummon,
			["assist"] = GmPrivileges.AssistSummon,
			["unsummon"] = GmPrivileges.TeleportDynels,
			["falldamage"] = GmPrivileges.PlayerDamage,
			["cash"] = GmPrivileges.SetStats,
			// /inflict /areaknockback = PlayerDamage 0x10, /resurrect = AcgSpawn 0x1000.
			["inflict"] = GmPrivileges.PlayerDamage,
			["areaknockback"] = GmPrivileges.PlayerDamage,
			["resurrect"] = GmPrivileges.AcgSpawn,
			// /get /getskill = GetStats 0x20, /set /setlocal = SetStats 0x40, /teleporttarget = TeleportDynels 0x8,
			// not TeleportSelf - retail masks moving somebody else higher than moving yourself.
			["get"] = GmPrivileges.GetStats,
			["getskill"] = GmPrivileges.GetStats,
			["set"] = GmPrivileges.SetStats,
			["setlocal"] = GmPrivileges.SetStats,
			["teleporttarget"] = GmPrivileges.TeleportDynels,
			// /broadcast /servcast = Broadcast 0x400, /sitecast = Sitecast 0x800 - its own bit, for the wider reach -
			// and /mobs = Developer 0x1.
			["broadcast"] = GmPrivileges.Broadcast,
			["servcast"] = GmPrivileges.Broadcast,
			["sitecast"] = GmPrivileges.Sitecast,
			["mobs"] = GmPrivileges.Developer,
			["tradeskill"] = GmPrivileges.Developer,
			["teleport"] = GmPrivileges.TeleportSelf,
			["teleportself"] = GmPrivileges.TeleportSelf,
			["tp"] = GmPrivileges.TeleportSelf,
			["knockback"] = GmPrivileges.PlayerDamage,
			["feat"] = GmPrivileges.GetStats,
			["feats"] = GmPrivileges.SetStats,
			["char"] = GmPrivileges.Developer,
			["kickban"] = GmPrivileges.CanKick,
			// /getfaction /setfaction = Developer 0x1, /magic = SetStats 0x40, /inventory = AcgSpawn 0x1000.
			["getfaction"] = GmPrivileges.Developer,
			["setfaction"] = GmPrivileges.Developer,
			["magic"] = GmPrivileges.SetStats,
			["inventory"] = GmPrivileges.AcgSpawn,
			["gethash"] = GmPrivileges.GetStats,
			["quest"] = GmPrivileges.Developer,
			["settimescale"] = GmPrivileges.Developer,
			["showclassstatadjustments"] = GmPrivileges.Developer,
			["update"] = GmPrivileges.Developer,
			["level"] = GmPrivileges.SetStats,
			["kill"] = GmPrivileges.NpcCommands,
			["aa"] = GmPrivileges.SetStats,

			// Local verbs - family choices. `help` is unrestricted and lists only what the caller may run.
			["help"] = GmPrivileges.None,
			["maps"] = GmPrivileges.Developer,
			["position"] = GmPrivileges.Developer,
			["recall"] = GmPrivileges.TeleportSelf,
			["recallhome"] = GmPrivileges.TeleportSelf,
			["travel"] = GmPrivileges.TeleportSelf,
			["criminal"] = GmPrivileges.SetStats,
			["heroicmode"] = GmPrivileges.Developer,
			["godmode"] = GmPrivileges.SetStats,
			["delete"] = GmPrivileges.NpcCommands,
			["hurt"] = GmPrivileges.PlayerDamage,
			["additem"] = GmPrivileges.AcgSpawn,
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

		/// <summary>
		///     Joins the handler table with GmCommandMasks, refusing to come up on any asymmetry.
		/// </summary>
		/// <remarks>
		///     A verb with a handler but no mask would dispatch ungated, and a mask with no handler would rot silently, so
		///     a loud boot failure is the cheapest place to catch both. GmCommandTableTests pins the retail half of the
		///     masks. <paramref name="unbackedReply" /> builds the handler for a catalog verb; it is a parameter rather
		///     than a direct call because this join is static and the reply needs the chat dispatcher. A null one still
		///     registers every catalog verb, so the table's shape is identical either way.
		/// </remarks>
		internal static Dictionary<string, GmCommandEntry> BuildGmCommands(Dictionary<string, GmCommandDelegate> handlers,
			Func<RetailCommandCatalog.Unbacked, GmCommandDelegate>? unbackedReply = null)
		{
			var noMask = handlers.Keys.Where(v => !GmCommandMasks.ContainsKey(v)).ToList();
			var noHandler = GmCommandMasks.Keys.Where(v => !handlers.ContainsKey(v)).ToList();
			if (noMask.Count > 0 || noHandler.Count > 0)
				throw new InvalidOperationException(
					$"GM command tables disagree - no mask: [{noMask.JoinAsString(", ")}], no handler: [{noHandler.JoinAsString(", ")}].");

			// A verb cannot be in both tables. The catalog is what is not backed yet, so a verb appearing there and
			// in the handler table means an implementation landed without its catalog row being removed - and the
			// unbacked reply would shadow it, or the other way round, depending on insertion order.
			var doubleBooked = RetailCommandCatalog.Verbs.Where(u => handlers.ContainsKey(u.Verb)).Select(u => u.Verb).ToList();
			if (doubleBooked.Count > 0)
				throw new InvalidOperationException(
					$"These verbs are implemented AND still listed as unbacked in RetailCommandCatalog: [{doubleBooked.JoinAsString(", ")}].");

			var table = handlers.ToDictionary(kv => kv.Key, kv => new GmCommandEntry(kv.Value, GmCommandMasks[kv.Key]), StringComparer.OrdinalIgnoreCase);

			// The rest of the client's registry, registered so a GM who presses one of the client's own affordances
			// gets the grammar and the reason rather than "no such command".
			foreach (var unbacked in RetailCommandCatalog.Verbs)
				table[unbacked.Verb] = new GmCommandEntry(unbackedReply?.Invoke(unbacked) ?? ((_, _) => Task.CompletedTask), unbacked.Mask);

			return table;
		}



		private static bool TryParseStatId(string text, out CharacterStatId statId)
		{
			var ok = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
				? uint.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var raw)
				: uint.TryParse(text, out raw);

			statId = (CharacterStatId)raw;
			return ok;
		}


		/// <summary>
		///     `.kill [&lt;handle&gt; | self]` - kills the player's currently selected target (the last LookAt or
		///     SelectDeselect), the given handle, or self.
		/// </summary>
		/// <remarks>
		///     An NPC runs the full combat death lifecycle: vitals push, despawn and corpse loot, XP, kill-quest advance.
		///     `self` drops the player's vitals to 0 and the world tick respawns them at the nearest resurrection point.
		///     With no argument and nothing selected it answers with a system message rather than doing nothing silently.
		/// </remarks>
		private async Task KillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var selfHandle = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();

			// Retail's grammar is `[all | radius r]`. Retail's own caveat - "the kill all and kill radius only works
			// on ..." - is truncated in the recovered description, so the one restriction applied is what the parameter
			// help states twice: both forms kill character dynels, i.e. NPCs, never players. A GM sweeping a playfield
			// must not take the other GMs with it.
			var tokens = arg.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			if (tokens.Length > 0 && tokens[0].Equals("all", StringComparison.OrdinalIgnoreCase))
			{
				await KillArea(client, radius: null);
				return;
			}

			if (tokens.Length == 2 && tokens[0].Equals("radius", StringComparison.OrdinalIgnoreCase) &&
			    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var killRadius) && killRadius > 0f)
			{
				await KillArea(client, killRadius);
				return;
			}

			uint? resolved;
			if (arg.Length == 0)
				resolved = client.Session.SelectedTargetHandle;
			else if (arg.Equals("self", StringComparison.OrdinalIgnoreCase) || arg.Equals("me", StringComparison.OrdinalIgnoreCase))
				resolved = selfHandle;
			else if (TryParseHandle(arg, out var parsedHandle))
				resolved = parsedHandle;
			else
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .kill [all | radius <r> | <handle> | self]  (no arg = your selected target)");
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
				await GameWire.SendStatUpdate(client, vitals.ToVitalsBlock(), selfHandle);
				await _agentServerDispatcher.SendSystemMessage(characterId, "You kill yourself.");
				return;
			}

			await _combat.KillNpc(client, targetHandle);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Killed target 0x{targetHandle:X8}.");
		}

		/// <summary>
		///     `.kill all` and `.kill radius &lt;r&gt;` - a null radius means the whole playfield.
		/// </summary>
		/// <remarks>
		///     Players are never swept: retail's parameter help says "character dynels" for both forms.
		/// </remarks>
		private async Task KillArea(INetworkClient client, float? radius)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You're not in the world yet.");
				return;
			}

			var zone = _simulation.World.ZoneForEntity(player);
			if (zone == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "You have no live zone.");
				return;
			}

			// Snapshot first: killing runs the whole lifecycle - corpse, loot, respawn arming - which mutates the zone.
			var doomed = (radius is { } r
					? zone.EntitiesInRange(player.Position, r, player.Handle)
					: zone.Entities.Where(e => e.Handle != player.Handle))
				.Where(e => !e.IsPlayer && e.Vitals is { IsDead: false })
				.ToArray();

			foreach (var victim in doomed)
				await _combat.KillNpc(client, victim.Handle);

			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Killed {doomed.Length} NPC(s){(radius is { } shown ? $" within {shown:0.#}u" : " on this playfield")}. Players were not touched.");
		}

		/// <summary>
		///     `.hurt [&lt;percent&gt;]` - drops the caller's three pools to <c>percent</c> of their max (default 50) and
		///     pushes the authoritative vitals.
		/// </summary>
		/// <remarks>
		///     The test bootstrap for the regen tick: losing health or mana to a mob or to casting is unreliable to stage,
		///     and the pools have to end up below max for the tick to emit anything at all. It never kills - health floors
		///     at 1 - so it exercises regen rather than the death and release path, which `.kill self` covers.
		/// </remarks>
		private async Task HurtCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? "").Trim();
			var percent = 50u; // half of every pool
			if (arg.Length > 0 && (!uint.TryParse(arg, out percent) || percent > 100))
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .hurt [<percent 0-100>]  (default 50)");
				return;
			}

			// Only ever lower a pool. Setting each one to `percent` of max would top up anything already below that -
			// `.hurt 50` on a character sitting at 0/74 mana would hand it 37 mana - so Min() is what keeps this a
			// damage command whatever state the pools are in.
			var vitals = _simulation.GetOrAddPlayerVitals(client);
			vitals.Health = Math.Max(1, Math.Min(vitals.Health, vitals.MaxHealth * percent / 100));
			vitals.Mana = Math.Min(vitals.Mana, vitals.MaxMana * percent / 100);
			vitals.Stamina = Math.Min(vitals.Stamina, vitals.MaxStamina * percent / 100);
			// Retail's own-handle health change is a single-stat block - {HealthCurrent} alone, 211 captured
			// (`--report vitals-shape`) - where the six-record current-plus-max form is a seed (93, at entry or
			// stream-in) and the three-current form is what it sends about other entities (4,676) and never about the
			// recipient. This command sends retail's own-handle shape.
			await GameWire.SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.HealthCurrent, vitals.Health), characterId);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Hurt to {percent}%: health {vitals.Health}/{vitals.MaxHealth}, mana {vitals.Mana}/{vitals.MaxMana}, stamina {vitals.Stamina}/{vitals.MaxStamina}.");
		}

		/// <summary>
		///     `.ah` - opens the auction house at the player's position. Sends DispatchOpenTradepost so the client opens the
		///     window and then sends TradepostOpen and its searches, which HandleTradepostPacket answers with listings.
		/// </summary>
		/// <remarks>A bootstrap for the tradepost; the real trigger is interacting with an auctioneer NPC.</remarks>
		private async Task OpenAuctionHouseCommand(INetworkClient client)
		{
			var characterId = client.Session.Character.Id;
			var entity = _simulation.PlayerWorldEntity(client);
			if (entity == null)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "No world entity for the player yet.");
				return;
			}

			var bankCount = await _tradepost.SendAuctionHouseOpen(client, entity.Position);
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Opened the auction house (PoC) at your position; bank tab has {bankCount} item(s).");
		}

		/// <summary>
		///     `.resist &lt;knockback|criminalteam|&lt;metaTypeId&gt;&gt; [percentage]` - pushes a spell resistance; 0
		///     removes it.
		/// </summary>
		/// <remarks>
		///     The standard GM bootstrap for a decoded message with no live trigger: the only metatype seen in quantity is
		///     a `Criminal Protection` buff nothing grants yet, and the other is knockback immunity for a knockback that is
		///     not implemented. A metatype is an id into LDB category 55200, so a raw id is accepted as well as the two
		///     named ones - the catalog has hundreds of entries, and this is the instrument for finding out what any of
		///     them does in-client.
		/// </remarks>
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

			// The remove carries the percentage too: retail's remove frames repeat the value the add carried rather
			// than sending 0, so `.resist X 0` sends a remove of the full-immunity value, not an add of zero.
			var add = percentage > 0;
			await GameWire.SendSpellResistance(client, metaType, add ? percentage : SpellMetaTypes.FullImmunity, add);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				add ? $"Resistance to metatype {metaType} set to {percentage}%." : $"Resistance to metatype {metaType} removed.");
		}

		/// <summary>
		///     `.waypoint [off] | [&lt;labelId&gt; [&lt;tooltipId&gt;]]` - plants a map marker at the player's current
		///     position, or takes it down.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The retail trigger is a death: every captured <c>DispatchAddWayPoint</c> is a tombstone pin, labelled
		///         "Tombstone" / "Locate your Tombstone to reduce death penalty!" and addressed at an
		///         <c>EntityClass.TombStone</c> id. There is no tombstone entity to point at yet, so this is the standard
		///         GM bootstrap for a decoded message whose retail trigger is not wired. The marker id is the player's own
		///         handle under the tombstone class, a stand-in for the corpse's id: what the in-client check needs is that
		///         the pin appears at the right place with the right label and that the remove takes it down again.
		///     </para>
		///     <para>
		///         The pin's icon is not selectable from this message - see <c>WayPointCodec</c>'s remark.
		///         <c>i_nLabel</c> and <c>i_nToolTip</c> are LDB category-100 text lookups (the title and the tooltip), and
		///         <c>i_cID</c> never reaches the client's waypoint-add at all: it is the marker key the remove addresses.
		///         The look comes from a waypoint type the client keeps in its own record, and the server-driven path
		///         zeroes it, so the two optional arguments change only what the pin says.
		///     </para>
		/// </remarks>
		private async Task WayPointCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var pos = entity?.Position ?? character.Position;

			if ((arguments ?? "").Trim().Equals("off", StringComparison.OrdinalIgnoreCase))
			{
				await GameWire.SendRemoveWayPoint(client, new EntityId(EntityClass.TombStone, character.Id), mapId);
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Waypoint removed.");
				return;
			}

			var tokens = (arguments ?? "").Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
			var label = tokens.Length > 0 && uint.TryParse(tokens[0], out var statedLabel)
				? statedLabel
				: WayPointCodec.TombstoneLabel;
			var toolTip = tokens.Length > 1 && uint.TryParse(tokens[1], out var statedToolTip)
				? statedToolTip
				: WayPointCodec.TombstoneToolTip;

			await GameWire.SendAddWayPoint(client, character.Id, mapId, pos.X, pos.Y, pos.Z, label, toolTip);
			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Waypoint planted on map {mapId} at {pos.X.ToString("0.###", CultureInfo.InvariantCulture)} "
				+ $"{pos.Y.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Z.ToString("0.###", CultureInfo.InvariantCulture)}"
				+ $" (label {label}, tooltip {toolTip} - LDB category 100). Use .waypoint off to remove it.");
		}

		/// <summary>
		///     `.position` - prints the caller's current map and live position: the in-world entity transform when
		///     connected, else the persisted Character position. Formatted so the x y z can be pasted into `.teleport`.
		/// </summary>
		private Task PositionCommand(INetworkClient client)
		{
			var character = client.Session.Character;
			var entity = _simulation.PlayerWorldEntity(client);
			var mapId = entity?.MapId ?? character.MapId;
			var pos = entity?.Position ?? character.Position;
			return _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Map {mapId} - position {pos.X.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Y.ToString("0.###", CultureInfo.InvariantCulture)} {pos.Z.ToString("0.###", CultureInfo.InvariantCulture)}");
		}

		/// <summary>
		///     `.heroicmode &lt;n&gt;` - sets stat 0x0A9, which the client's own descriptor registry names
		///     <c>HeroicMode</c>.
		/// </summary>
		/// <remarks>
		///     Named for the stat rather than for a mechanic, because there is no mechanic here: nothing in retail is known
		///     to set it, so a GM command is the only way the column is ever anything but 0. The client names this stat
		///     twice and the two disagree - the exe's descriptor registry says <c>HeroicMode</c>, LDB 615 says
		///     <c>ChosenGod</c> - and the exe's name is taken. See Character.HeroicMode. The argument is the raw wire
		///     number; the client stores this stat and never reads it back.
		/// </remarks>
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
			await _database.SetCharacterHeroicMode(character.Id, value);
			await GameWire.SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.HeroicMode, value), character.Id);
			await _agentServerDispatcher.SendSystemMessage(character.Id, $"HeroicMode set to {value}.");
		}

		/// <summary>
		///     `.criminal [seconds]` - flags the calling character criminal for <c>seconds</c>, defaulting to retail's
		///     300 s; `.criminal 0` clears it. Also awards the measured murder points so the stat moves as it does in the
		///     capture.
		/// </summary>
		/// <remarks>
		///     The standard test bootstrap: the retail trigger is killing another player, and player-versus-player combat
		///     is not modelled, so without this the criminal branches in ShopService are unreachable.
		/// </remarks>
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
				await _database.SetCharacterCriminalState(character.Id, null, 0);
				await _buffs.SendBuffRemoval(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8);
				await GameWire.SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, 0), character.Id);
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Criminal status cleared.");
				return;
			}

			character.CriminalUntil = DateTimeOffset.UtcNow.AddSeconds(seconds);
			character.MurderPoints += CriminalStatus.PointsPerPlayerKill;
			await _database.SetCharacterCriminalState(character.Id, character.CriminalUntil, character.MurderPoints);

			// The wire side: the "Criminal" debuff plus the murder-point stat, the two things the capture shows moving.
			var spell = await _database.GetSpell(CriminalStatus.CriminalSpellId);
			await _buffs.SendMagicBuffIir(client, character.Id, character.Id, CriminalStatus.CriminalSpellId << 8,
				MagicBuffDatas.ApplyNow(CriminalStatus.CriminalSpellId, (uint)(seconds * 100), BuffService.CasterLevel(client), casterIsPlayer: true));
			await GameWire.SendStatUpdate(client, new CharacterStatBlock().Set(CharacterStatId.MurderPoints, character.MurderPoints), character.Id);

			await _agentServerDispatcher.SendSystemMessage(character.Id,
				$"Criminal for {seconds}s ({spell?.Name ?? "Criminal"} {CriminalStatus.CriminalSpellId}); murder points {character.MurderPoints}.");
		}

		/// <summary>
		///     `.tag &lt;id&gt;` or `.tag &lt;name substring&gt;` - awards an achievement. With no argument it lists what
		///     the character already holds.
		/// </summary>
		/// <remarks>
		///     The retail triggers are per-achievement content nobody has mapped yet - a vista, a kill streak, a first pet
		///     - so this is the standard GM bootstrap for a feature whose real trigger is not wired: it exercises persist,
		///     award and the client's own banner against a catalog entry rather than a made-up id. The listing is the other
		///     half of checking it works, because the award is a one-shot event and the snapshot is what carries the set
		///     across a relog.
		/// </remarks>
		private async Task TagCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var argument = (arguments ?? "").Trim();
			if (argument.Length == 0)
			{
				var held = await _database.GetCharacterTags(characterId);
				if (held.Length == 0)
				{
					await _agentServerDispatcher.SendSystemMessage(characterId, "No tags. Usage: .tag <id> | .tag <name substring>");
					return;
				}

				var named = new List<string>();
				foreach (var tag in held.OrderBy(t => t))
					named.Add($"{tag} ({(await _database.GetAchievement(tag))?.Name ?? "unnamed"})");

				await _agentServerDispatcher.SendSystemMessage(characterId, $"{held.Length} tag(s): {string.Join(", ", named)}");
				return;
			}

			// An id, or a name to look up: a 4,095-entry catalog is not something to search by hand.
			if (!uint.TryParse(argument, out var tagId))
			{
				var matches = await _database.FindAchievements(argument, 6);
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

			var (granted, achievement) = await _tags.GrantTag(client, tagId);
			var name = achievement is null
				? "not in the achievement catalog - an item-shop flag, or an id the catalog is short of"
				: $"'{achievement.Name}'{(achievement.Description is { } d ? $" - {d}" : "")}";

			await _agentServerDispatcher.SendSystemMessage(characterId, granted
				? $"Awarded tag {tagId}: {name}"
				: $"Already held tag {tagId}: {name}");
		}


		/// <summary>
		///     What a verb the client registers but this server does not back yet answers with: retail's own usage line and
		///     what it would take. Deliberately neither a silent no-op nor an error - the command exists in the client, so
		///     "unknown command" would be wrong about the client.
		/// </summary>
		private Task SendUnbackedVerbReply(INetworkClient client, RetailCommandCatalog.Unbacked unbacked)
		{
			var usage = string.IsNullOrEmpty(unbacked.Usage) ? "(takes no arguments)" : $".{unbacked.Verb} {unbacked.Usage}";
			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Lines([
				ChatMarkup.Color("orange", $"'{unbacked.Verb}' is a retail client command this server does not implement yet."),
				string.IsNullOrEmpty(unbacked.Description) ? "" : $"  retail: {unbacked.Description}",
				$"  usage:  {usage}",
				$"  needs:  {unbacked.Needs}"
			]));
		}

		/// <summary>
		///     Lists every verb the caller's mask passes, backed or not, so the client's whole command surface is
		///     discoverable in-game rather than only by reading the registry TSV. Unbacked verbs are marked so the listing
		///     does not promise more than it delivers.
		/// </summary>
		private Task SendGmHelpText(INetworkClient client)
		{
			var account = client.Session.Account;
			var unbacked = RetailCommandCatalog.Verbs.Select(u => u.Verb).ToHashSet(StringComparer.OrdinalIgnoreCase);
			var available = _gmCommandsDict
				.Where(e => e.Value.RequiredMask == GmPrivileges.None || account?.HasGmPrivilege(e.Value.RequiredMask) == true)
				.Select(e => unbacked.Contains(e.Key) ? $"{e.Key} {ChatMarkup.Color("gray", "(not implemented)")}" : e.Key)
				.Order(StringComparer.OrdinalIgnoreCase);

			return _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, ChatMarkup.Raw($"Available Commands:<br />{available.JoinAsString("<br />")}"));
		}

		/// <summary>
		///     `.level &lt;n&gt;` - sets the player's character level, regenerates its MaxStats from the new class and
		///     level, persists it, and pushes the Level stat plus refreshed vitals so the change shows without a relog. The
		///     persisted level also rides the next login snapshot, and the stat's wire scale is applied at send.
		/// </summary>
		/// <remarks>Test-bootstrap convenience for level-gated features, such as the Totem of Origin's minimum of 20.</remarks>
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
			// level-gated ability and spell unlocks and their castability grants - belongs to the progression service, so
			// that a GM set and an XP-driven ding run the same code rather than two copies held in step by hand.
			await _levelUp.SetLevel(client, level);

			await _agentServerDispatcher.SendSystemMessage(character.Id, $"Level set to {level} (relog to refresh the full character sheet).");
		}

		/// <summary>
		///     `.setnpcgroup [&lt;group name&gt;] [clear]` - manual creature assignment for an RDB spawn area. Select an NPC
		///     standing in the area, whose type is what the area spawns, then:
		///     <code>
		///     .setnpcgroup                 assign the selected NPC's creature to the area the NPC stands in
		///     .setnpcgroup &lt;group&gt;         assign it to the named area on that map
		///     .setnpcgroup clear           clear the area the NPC stands in, back to unresolved
		///     .setnpcgroup &lt;group&gt; clear   clear the named area
		///     </code>
		/// </summary>
		/// <remarks>
		///     A set marks the area Manual, so it is never re-guessed; a clear reverts NpcTemplateId to null so the
		///     resolver may re-guess it. The dev nameplate labels (.togglenpcgroup) show each NPC's area.
		/// </remarks>
		private async Task SetNpcGroupCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null || client.Session.SelectedTargetHandle is not { } handle || !_simulation.World.ZoneForEntity(player).TryGet(handle, out var npc) || npc.NpcTemplateId == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Select an NPC first, then .setnpcgroup [<group name>] [clear]  (no arg = the area the NPC stands in).");
				return;
			}

			var areas = await _database.GetNpcSpawnAreas(npc.MapId);
			if (areas.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Map {npc.MapId} has no RDB spawn areas (scripted/instanced playfield).");
				return;
			}

			// A trailing "clear"/"none"/"-" makes this a clear; the rest (if any) is the target group name.
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
				await _database.SetNpcSpawnAreaCreature(area.Id, null);
				var unlinked = await _database.UnlinkNpcSpawnsFromArea(area.Id);
				await _agentServerDispatcher.SendSystemMessage(characterId, $"Spawn area '{area.GroupName}' (map {npc.MapId}) cleared - creature unassigned, {unlinked} captured spawn(s) un-linked.");
				return;
			}

			await _database.SetNpcSpawnAreaCreature(area.Id, npc.NpcTemplateId);
			var linked = await _database.LinkNpcSpawnsToArea(area.Id, npc.NpcTemplateId, npc.Route.SpawnPosition.X, npc.Route.SpawnPosition.Z);
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Spawn area '{area.GroupName}' (map {npc.MapId}) now spawns '{StripAreaLabel(npc.Name)}' [manual]; linked {linked} captured spawn(s) (graduate to remove them).");
		}

		/// <summary>
		///     `.syschat` - pushes a world or system chat line into the chat window through ChatTextIIR, using the captured
		///     localized CriteriaYou line. Arbitrary literal text needs a literal-text remoteformat template, as the FIFO
		///     banner uses. The test bootstrap for the ChatTextIIR path.
		/// </summary>
		private async Task SysChatCommand(INetworkClient client)
		{
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id, "Sent a world/system chat line (ChatTextIIR).");
		}

		/// <summary>
		///     `.toast &lt;text&gt;` - pushes arbitrary literal text to the top-of-screen FIFO banner, not to the chat
		///     window. The test bootstrap for DynamicAreaBannerCodec.
		/// </summary>
		/// <remarks>
		///     The GenericDisplayAreaName FIFO template resolves to the format body <c>{DisplayedText}</c>, so a literal
		///     type-7 parameter renders verbatim - the same envelope the area-name banner uses, minus the nested
		///     <c>&lt;localized&gt;</c>.
		/// </remarks>
		private async Task ToastCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var text = (arguments ?? "").Trim();
			if (text.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .toast <text>   (shows the text as a top-of-screen banner)");
				return;
			}

			await GameWire.SendFeedback(client, new DynamicAreaBanner(text));
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Toasted: '{text}'");
		}

		/// <summary>
		///     `.target` - prints information about the player's currently selected target, the last in-game LookAt or
		///     SelectDeselect: name, wire handle, kind, template id, level, vitals, hostility, position, and the spawn area
		///     it belongs to. Reported in-game as a system message.
		/// </summary>
		/// <remarks>The spawn area is the labelling aid - it reads the group without the nameplate toggle.</remarks>
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

			// A live entity (player / NPC / loot bag) in the zone entity model.
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

			// Not a live entity - a static world object (chest/door/dynel) isn't in the entity model, it hangs off the
			// loaded zone. Resolve it there so a selected chest/door still reports.
			if (_simulation.World.ZoneForEntity(player).TryGetWorldObject(handle, out var live))
			{
				var obj = live.Row;
				var oname = WorldObjectDisplay.ToWire(obj) ?? obj.Name ?? "(unnamed object)";
				// A name-resolved zone-line knows its destination map but no arrival - report the map either way,
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

		/// <summary>
		///     `.knockback [distance]` - shoves the player away from their current target, or with no target away from the
		///     direction they are facing, using the real wire path: the forced-move MoveTask and SetPos pair plus the
		///     "Knockback immunity" buff retail applies alongside it.
		/// </summary>
		/// <remarks>
		///     The standard GM bootstrap for a feature whose retail trigger is not wired. Here the trigger is genuinely
		///     unknown rather than merely unbuilt: the corpus has one knockback, and the ApplySpell in that batch names the
		///     immunity guard rather than the ability that did the knocking, so nothing says which attacks knock back.
		/// </remarks>
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

			// The source is what the player is pushed away from. Their current target is the natural stand-in for the
			// attacker; with none, push away from a point just in front of them so the command still demonstrates being
			// shoved backwards instead of doing nothing.
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

		/// <summary>
		///     `.travel &lt;npc name&gt; [answer path]` - drives local travel. With no path it lists what that NPC offers;
		///     with one it runs that trip directly.
		/// </summary>
		/// <remarks>
		///     The retail trigger is wired - a wagoneer's destination pick inside the ordinary Knubot conversation runs the
		///     whole flow - so this is the diagnostic rather than the only way in: it names the answer path of every
		///     destination, which is the wiring key, and runs a trip without walking the tree. It deliberately skips the
		///     fare, which is charged one screen earlier when the destination list is served, so a GM trip is free.
		/// </remarks>
		private async Task TravelCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var arg = (arguments ?? string.Empty).Trim();
			if (arg.Length == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, "Usage: .travel <npc name> [option id]");
				return;
			}

			// The destination's option id, when present, is the last whitespace-separated token: NPC names contain
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
					var map = await _database.GetMap(destination.DestinationMapId);
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

		/// <summary>
		///     `.teleport &lt;mapId | name&gt;` - resolves the argument to a map (a numeric id, or a case-insensitive name
		///     match so `.teleport Khemi` works) and then hands off to DoTeleport. Also accepts `.teleport &lt;x&gt;
		///     &lt;y&gt; &lt;z&gt;`, three numeric tokens, to move to a position on the current map. A missing or garbled
		///     argument answers with a usage line rather than throwing.
		/// </summary>
		/// <remarks>
		///     <para>
		///         A playfield change goes through retail's handoff and reconnect, never in place: the server sends a
		///         0x2002 world-address advert on the current connection, the client opens a fresh connection and does a
		///         full world entry there (changemap capture account-faolan-...-changemap: :7027 to :7030, frame 4214
		///         advertises gs013-nj4...:7030).
		///     </para>
		///     <para>
		///         This matters because the client only loads a playfield's resources - camera paths, geometry, NPCs -
		///         during a fresh world entry: camera paths are an RDB resource (e_RDB_CameraPaths, type 1000047) keyed by
		///         playfield id, loaded by CameraPathMap_c::LoadPlayfieldPaths only on the n3Camera init a fresh entry runs
		///         (see docs/subsystems/create-cutscene-camera-paths.md). A soft in-place MapChange on the live connection
		///         does not trigger that reload, so the old playfield's resources stay loaded.
		///     </para>
		///     <para>
		///         The handoff is the same packet - the same 0xb171ce13 to 0x6c67c147 conversation, byte-identical - the
		///         create enter-world flow sends: it advertises this server's own GameServer, the client reconnects, and
		///         the existing-character Hello path runs SendWorldEntry for the now-persisted target map, which is
		///         identical to logging in on that map.
		///     </para>
		/// </remarks>
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

			// Retail's grammar. The client's own hidden GM affordance - right-click the map, then "teleport to
			// location" - emits `/teleportself <posx> <posz>`: two coordinates and no Y, on the current playfield.
			// Recovered from FanaticTeleportCommand_c's parameter descriptor (docs/re/gm-command-args.tsv), which
			// declares the family as
			//     posx posz [[y posy][playfield]] [proxy model type group subgroup running]
			// so Y is optional and arrives behind a literal `y` token, with the playfield after it. These forms are
			// matched before the local ones (`<x> <y> <z>`, `<map>`), which are handled below; a 3-token form is
			// unambiguously local, because retail's third token is the literal `y`.
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

			// `.teleport <x> <y> <z>` - three numeric tokens are a position on the current map, with no map change.
			if (tokens.Length == 3 && float.TryParse(tokens[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var px) &&
			    float.TryParse(tokens[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var py) && float.TryParse(tokens[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var pz))
			{
				await _teleport.DoTeleport(characterId, client.Session.Character.MapId, new Vector3(px, py, pz));
				return;
			}

			// `.teleport <mapId | name> <x> <y> <z>` - a precise spot on a named or numeric map in one command. The map
			// is the leading token or tokens and the trailing three are the coordinates; a map name may contain spaces,
			// so everything before the last three tokens is the map argument.
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

		/// <summary>
		///     `.godmode` - toggles invincibility on the caller: hostile NPC swings land their animation but deal no
		///     damage. Self-target only.
		/// </summary>
		/// <remarks>
		///     The toggle is persisted on the account and re-applied to the live WorldEntity on every world entry, so it
		///     survives both a relog and a map change - a playfield change is a fresh world connection and entity.
		/// </remarks>
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
			await _database.SaveAccount(client.Session.Account);
			await _agentServerDispatcher.SendSystemMessage(characterId, entity.GodMode
				? ChatMarkup.Raw($"God mode {ChatMarkup.Color("lime", "ON")} - you take no damage.")
				: ChatMarkup.Raw($"God mode {ChatMarkup.Color("red", "OFF")}."));
		}


		// `.togglenpcgroup` - toggles the dev spawn-area nameplate labels live for this session: each NPC that
		// resolves to an RDB spawn area shows that area's "group | tier xN" over its head (pushed via SetNameIIR, since
		// the client draws NPC names from the template LDB and ignores the spawn name). Pairs with .setnpcgroup: read
		// the area off the nameplate, assign with .setnpcgroup. Off by default; the toggle re-applies to nearby NPCs now.
		private async Task ToggleNpcGroupCommand(INetworkClient client)
		{
			var (on, affected) = await _broadcaster.ToggleAreaLabels(client);
			await _agentServerDispatcher.SendSystemMessage(client.Session.Character.Id,
				$"Spawn-area nameplate labels {(on ? "ON" : "OFF")} - {affected} nearby NPC(s) updated. Walk to re-stream the rest.");
		}

		/// <summary>
		///     `.tradeskill [&lt;profession&gt; [&lt;rank&gt;]]` - trains a tradeskill profession. With no argument it
		///     reports what the character has trained and what each profession's ladder looks like.
		/// </summary>
		/// <remarks>
		///     <para>
		///         The standard GM bootstrap for a feature whose retail trigger is not wired: the learn path is a trainer
		///         conversation and no capture shows one, so it cannot be built from the corpus. Everything downstream - the
		///         ladder, the rank, and the recipe book the rank buys - is retail, so a GM grant puts a character in a
		///         state the rest of the system reads exactly as it would a trained one.
		///     </para>
		///     <para>
		///         It trains the whole chain up to the named rank, because that is what a character who reached it holds:
		///         each rung names the one below it as its prerequisite. <c>AddCharacterFeats</c> collapses the rows per
		///         base and keeps the highest of each, so the stored set is the same three rows retail's would be.
		///     </para>
		/// </remarks>
		private async Task TradeskillCommand(INetworkClient client, string arguments)
		{
			var characterId = client.Session.Character.Id;
			var words = (arguments ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
			var ladders = await _tradeskill.Ladders();

			if (words.Length == 0)
			{
				var trained = await _tradeskill.ProfessionsOf(characterId);
				var lines = ladders.Select(l => trained.TryGetValue(l.Name, out var rank)
					? $"{l.Name}: {TradeskillLadder.RankName(rank)} ({l.RankFeatIds.Length} rank(s) on its ladder)"
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

			var trainedCount = await _database.AddCharacterFeats(characterId, ladder.RankFeatIds.Take(wanted + 1));
			await _agentServerDispatcher.SendSystemMessage(characterId,
				$"Trained {ladder.Name} to {TradeskillLadder.RankName(wanted)} ({trainedCount} feat row(s) written). Reopen the crafting window to see the book.");
			_logger.LogInformation("GM: {Character} trained {Profession} to {Rank}.", client.Session.Character.Name, ladder.Name, TradeskillLadder.RankName(wanted));
		}

		/// <summary>
		///     `.weaponset [primary | secondary]` - the server-side test bootstrap for the SwitchWeaponsButton set switch.
		///     No argument, or "swap"/"toggle", flips the active loadout; "primary"/"secondary" (or 1/2) forces one.
		/// </summary>
		/// <remarks>
		///     Persists the set and mirrors it on the live entity so the login-time gear-stat aggregation, which counts
		///     only the active set's weapons, uses it. This drives the model only - the cross-player swap relay needs the
		///     real BCC payload, so exercise that with the retail weapon-swap key against a live viewer.
		/// </remarks>
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

		/// <summary>
		///     A registered GM command: its handler and the gm_level bits it requires. None means unrestricted, i.e. any
		///     GM, which is the client's own convention for an unmasked command; a multi-bit mask passes on any set bit,
		///     the client's <c>(level &amp; mask) != 0</c> test.
		/// </summary>
		internal sealed record GmCommandEntry(GmCommandDelegate Handler, GmPrivileges RequiredMask);

		/// <summary>
		///     Resolves a map argument - a numeric id, an exact case-insensitive name, or the first name-contains match -
		///     to its Map row, or null if nothing matches. Shared by the `.teleport &lt;name&gt;` and `.teleport
		///     &lt;name&gt; x y z` forms.
		/// </summary>
		private async Task<Map?> ResolveMap(string arg)
		{
			if (uint.TryParse(arg, out var id))
			{
				var maps = await _database.GetMaps();
				return maps.FirstOrDefault(m => m.Id == id);
			}

			var all = await _database.GetMaps();
			return all.FirstOrDefault(m => string.Equals(m.Name, arg, StringComparison.OrdinalIgnoreCase)) ?? all.FirstOrDefault(m => m.Name.Contains(arg, StringComparison.OrdinalIgnoreCase));
		}

		/// <summary>
		///     `.save` - flushes the live world state to the database. The WorldEntity is the source of truth for the live
		///     transform and movement is not otherwise persisted, so this snapshots every connected player's authoritative
		///     position and map back onto their persisted Character and saves it.
		/// </summary>
		private async Task SaveChanges(uint characterId)
		{
			var saved = await _persistence.FlushAllAsync();
			await _agentServerDispatcher.SendSystemMessage(characterId, $"Flushed {saved} player(s)' live world state to the database.");
		}

		/// <summary>
		///     Parses a wire handle from a GM-command argument: "0x"-prefixed hex, or plain decimal. Returns false on a
		///     malformed or overflowing value.
		/// </summary>
		private static bool TryParseHandle(string arg, out uint handle)
		{
			handle = 0;
			try
			{
				handle = arg.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
					? Convert.ToUInt32(arg[2..], 16)
					: uint.Parse(arg);
				return true;
			}
			catch (FormatException)
			{
				return false;
			}
			catch (OverflowException)
			{
				return false;
			}
		}

		// Lists maps for `.maps`, optionally filtered to those whose name contains `search` (case-insensitive) -
		// e.g. `.maps castle`. A blank search lists all maps.
		private async Task SendListAllMaps(uint characterId, string? search = null)
		{
			var maps = (await _database.GetMaps()).OrderBy(m => m.Name).AsEnumerable();
			if (!string.IsNullOrWhiteSpace(search))
				maps = maps.Where(m => m.Name != null && m.Name.Contains(search.Trim(), StringComparison.OrdinalIgnoreCase));

			var matches = maps.ToList();
			if (matches.Count == 0)
			{
				await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Color("red", $"No map matching '{search?.Trim()}'."));
				return;
			}

			await _agentServerDispatcher.SendSystemMessage(characterId, ChatMarkup.Lines([.. matches.Select(ChatMarkup (m) => $"{m.Id}: {m.Name}")]));
		}
	}
}
