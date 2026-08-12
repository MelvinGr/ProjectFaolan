using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Numerics;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Spawn;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Protobuf;
using Faolan.Core.Rdb;
using Faolan.Importer.Captures;
using ProtoBuf;

namespace Faolan.Importer.Cli
{
	// `--report timeline` - ONE flat chronological account of a single capture: what the session did, in order,
	// with every id resolved from our own data.
	//
	// It replaces a Python script that re-derived the same wire grammar by hand (tools/capture/timeline.py, retired).
	// That is the whole reason this lives here: the offsets a timeline needs ARE the codecs, and a second
	// implementation of them drifts - the Python one shipped four wrong offsets (playfield, QuestIIR, PetIIR, the
	// item template) before they were caught by eye. Here the decode comes from WorldQuest / StatDamageCodec /
	// HateListCodec / AbilityGrantBody / the generated protobuf types, all of which golden tests already gate.
	//
	// Two things it can do that the script could not:
	//   - names come from the Opcodes.cs ENUMS, so they cannot go stale the way a tshark dump's `Method` column does
	//     (tshark_dumps_carry_stale_opcode_names) and there is no source-parsing regex to get wrong;
	//   - LDB text is decrypted INLINE from the key the wire itself carries, so dialogue and quest lines read as
	//     English in the timeline - including screens our own ConversationScreens table has never captured.
	//
	// It is deliberately NOT sectioned and has no brief/verbose modes: one event per line, always detailed. Reading a
	// session as it happened is the job, and an event and its consequence must not land under different headings.
	public static partial class ReportCli
	{
		// The service tier each port belongs to. 7010 is the only fixed one (UniverseAgent); the rest are handed to
		// the client in the preceding tier's payload, so these are the ports the corpus actually used.
		private static readonly Dictionary<int, string> TimelineTiers = new Dictionary<int, string>
		{
			[7010] = "universe", [7011] = "playeragent", [7012] = "cs", [7001] = "chat", [7017] = "game"
		};

		// Per-frame traffic: movement, heartbeats, aggro polling, the stat/energy stream. Five of every six packets in
		// a gameplay capture, and none of them an event. Suppressed with a printed count - never silently.
		private static readonly HashSet<string> TimelineNoise =
		[
			"CharDCMoveIIR", "MoveTaskIIR", "RequestAggroStatus", "DispatchUpdateAggroStatus", "SetSprintInputState",
			"DispatchSetIsSprinting", "DispatchSyncCanSprint", "Ping", "DispatchPong", "UpdateGameServerStats",
			"DispatchGameServerStats", "StatIIR", "EnergyIIR", "RegenIIR", "DispatchSyncRegenDelayed",
			"UpdateQuestMarkerNPC", "DispatchSetQuestMarker"
		];

		private static readonly Regex RemoteFormat = new Regex("<(remoteformat|localized)([^>]*)>", RegexOptions.Compiled);
		private static readonly Regex XmlAttr = new Regex("(\\w+)=\"?([^\"\\s>]*)\"?", RegexOptions.Compiled);

		private static async Task Timeline(string dumpPath, string rdbDir)
		{
			if (!File.Exists(dumpPath))
			{
				Console.WriteLine($"timeline: no such dump: {dumpPath}");
				Console.WriteLine("  Pass --report-arg <capture>.tshark.json (dump a pcapng with tools/capture/tshark_dump.sh).");
				return;
			}

			var packets = TsharkCaptureReader.Read(dumpPath, true);
			if (packets.Count == 0)
			{
				Console.WriteLine($"timeline: {Path.GetFileName(dumpPath)} yielded no packets.");
				return;
			}

			await using var db = OpenDb();
			var look = await TimelineLookups.LoadAsync(db, rdbDir);
			var events = packets.Select(p => new TimelineEvent(p, look)).OrderBy(e => e.Ticks).ToList();
			var labels = ResolveEntities(events, look);

			var start = new DateTimeOffset(events[0].Ticks, TimeSpan.Zero);
			Console.WriteLine($"# {Path.GetFileName(dumpPath)}");
			Console.WriteLine($"# {start:u}   {(events[^1].Ticks - events[0].Ticks) / (double)TimeSpan.TicksPerSecond:F1}s   {packets.Count} packets");
			TimelineAnnotations(events, look);
			Console.WriteLine();

			var suppressed = new Dictionary<string, int>();
			var refused = new Dictionary<string, int>();
			foreach (var e in events)
			{
				if (TimelineNoise.Contains(e.Name))
				{
					suppressed[e.Name] = suppressed.GetValueOrDefault(e.Name) + 1;
					continue;
				}

				var seconds = (e.Ticks - events[0].Ticks) / (double)TimeSpan.TicksPerSecond;
				var detail = Describe(e, look, labels, refused);
				var hex = Convert.ToHexString(e.Raw).ToLowerInvariant();
				if (hex.Length > 72)
					hex = hex[..72] + "\u2026";

				// The player's own typed lines are marked, because on an ANNOTATED capture they are the only record of
				// intent the session has and they are otherwise one line in ten thousand.
				var marker = IsPlayerChat(e) ? ">>" : "  ";
				Console.WriteLine($"{marker}{seconds,8:F1} {e.Frame,7} {e.Tier,-11} {Dir(e.Direction)} {e.Name,-32} {detail}"
				                  + (hex.Length > 0 ? $"   [{hex}]" : ""));
			}

			TimelineFooter(events, look, suppressed, refused);
		}

		// A line the PLAYER typed, as opposed to one the server pushed at them. The c2s side is the honest test: an
		// NPC bark and the server's echo of the player's own line are both s2c `CharacterChat` on the chat tier and
		// look identical, while `ChatVicinity` / `ChannelChat` going UP the wire can only have been typed.
		private static bool IsPlayerChat(TimelineEvent e)
		{
			return e.Direction == "c2s" && e.Name is "ChatVicinity" or "ChannelChat" or "Whisper";
		}

		// The ANNOTATION INDEX, printed under the header.
		//
		// The maintainer narrates what he is doing in /say as he plays, which is the only record of INTENT a capture
		// carries - and it is scattered through ten thousand lines. Lifting it to the top turns the dump into a
		// labelled index: "17:44:47 just put 1 skillpoint in run speed" is a pointer straight at four packets that
		// would otherwise have to be found by hand.
		//
		// ⚠️ **A line describes something that already happened.** The lag is ~5 s and it runs BACKWARDS: by the time
		// a line is typed the event is over, and the player's NEXT action can land inside the window. So an annotation
		// names an EVENT, never "the packets nearest to it" - attribute traffic only when the message text, a named
		// token or a matching id corroborates it.
		private static void TimelineAnnotations(List<TimelineEvent> events, TimelineLookups look)
		{
			var typed = events.Where(IsPlayerChat).ToList();
			if (typed.Count == 0)
				return;

			Console.WriteLine();
			Console.WriteLine($"# {typed.Count} line(s) the player typed - the capture's own annotations. ⚠️ each one describes something ~5s EARLIER.");
			foreach (var e in typed)
			{
				var seconds = (e.Ticks - events[0].Ticks) / (double)TimeSpan.TicksPerSecond;
				Console.WriteLine($"#   {seconds,8:F1} {new DateTimeOffset(e.Ticks, TimeSpan.Zero):HH:mm:ss}  {look.Text(e.Content)}");
			}
		}

		private static string Dir(string direction)
		{
			return direction.Length > 0 ? direction[..1] : "?";
		}

		// The only trailing block. NOT a section of the narrative - it is the part of the capture our own data could
		// not name, which is the finding a timeline run exists to surface, and it is silent when there is nothing.
		private static void TimelineFooter(List<TimelineEvent> events, TimelineLookups look, Dictionary<string, int> suppressed,
			Dictionary<string, int> refused)
		{
			Console.WriteLine();
			if (suppressed.Count > 0)
			{
				var top = string.Join(", ", suppressed.OrderByDescending(k => k.Value).Take(6).Select(k => $"{k.Key} x{k.Value}"));
				Console.WriteLine($"# {suppressed.Values.Sum()} per-frame packets suppressed: {top}{(suppressed.Count > 6 ? ", \u2026" : "")}");
			}

			var unnamedIir = events.Where(e => e.Iir != 0 && !look.IirNames.ContainsKey(e.Iir))
				.Select(e => $"0x{e.Iir:x8}").Distinct().Order().ToList();
			if (unnamedIir.Count > 0)
				Console.WriteLine($"# IIR types not in Opcodes.cs: {string.Join(", ", unnamedIir)}");

			var unnamedMethod = events.Where(e => e.Iir == 0 && e.Name.StartsWith("0x", StringComparison.Ordinal))
				.Select(e => e.Name).Distinct().Order().ToList();
			if (unnamedMethod.Count > 0)
				Console.WriteLine($"# methods not in Opcodes.cs: {string.Join(", ", unnamedMethod)}");

			// ⭐ The decode-coverage line. A codec that THROWS on retail traffic models a shape the live service does
			// not send, and one such line is invisible in ten thousand - as a per-message count over a session it is a
			// finding. It is deliberately separate from the two lists above: those are messages we cannot NAME, this
			// one is messages we name and then fail to read.
			if (refused.Count > 0)
			{
				var counts = string.Join(", ", refused.OrderByDescending(k => k.Value).Select(k => $"{k.Key} x{k.Value}"));
				Console.WriteLine($"# {refused.Values.Sum()} record(s) a codec REFUSED (its Parse threw): {counts}");
			}
		}

		// handle -> label. Entity handles are SESSION-VOLATILE, so they cannot be looked up; they are resolved through
		// the capture itself, by matching a spawn body's POSITION against NpcSpawns/WorldObjects on this playfield.
		//
		// ⚠️ Five classes are excluded rather than matched, or they take the name of whatever they stood near:
		// the player, summoned PETS (they follow the player - one matched a "Brutal Pirate" 22u away), OTHER
		// PLAYERS (a character id is far above the entity-handle range; one standing inside 2u of a vendor came out
		// labelled "Trader"), and LOOT BAGS + TOMBSTONES. The last two are dynamic in the same way: they spawn where
		// something DIED or was dropped, so they can never have a WorldObjects row and the nearest static is simply
		// whatever the fight happened to be near. 8 of the 9 in the necromancer play2 capture were wrong - a corpse
		// bag labelled "Climbing Point (+19u)", a tombstone labelled "Genzio's House (+16u)" - and the only right one
		// was the one with nothing inside 25u to steal a name from (#560). A bare handle is honest; a confident wrong
		// name is not. (That also crossed entity CLASSES, LootBag 0xC87A taking a ClimbingDynel 0xC87D name; gating
		// the match on class would need a class column on WorldObject, and with the dynamic types gone there is no
		// observed case left that needs it.)
		//
		// A non-exact match prints its distance, because an AMBUSH spawns where it was triggered rather than at its
		// home point - the quarry Bat Demon appears 10u off its row, and that gap is evidence.
		private const uint CharacterIdFloor = 0x1000000;

		private static Dictionary<uint, string> ResolveEntities(List<TimelineEvent> events, TimelineLookups look)
		{
			var labels = new Dictionary<uint, string>();
			uint mapId = 0;
			foreach (var e in events)
			{
				switch (e.Name)
				{
					case "PlayfieldFIIR" when e.Raw.Length >= 38:
					{
						mapId = e.Raw.ReadU32Be(34);
						break;
					}

					case "FullPlayerCharacterIIR" when e.Raw.Length >= 16:
					{
						labels[e.Raw.ReadU32Be(12)] = "the player";
						break;
					}

					case "PetIIR" when e.Raw.Length >= 29:
					{
						labels[e.Raw.ReadU32Be(25)] = "pet";
						break;
					}
				}
			}

			var npcs = look.NpcSpawns.GetValueOrDefault(mapId) ?? [];
			var objects = look.WorldObjects.GetValueOrDefault(mapId) ?? [];
			foreach (var e in events)
			{
				var isChar = e.Name is "SimpleCharFullUpdateIIR";
				if (!isChar && e.Name is not ("SimpleDynelFullUpdateIIR" or "DoorFullUpdateIIR"
					    or "ClimbingDynelFullUpdateIIR"))
					continue;
				if (e.Raw.Length < 16)
					continue;

				var handle = e.Raw.ReadU32Be(12);
				if (labels.ContainsKey(handle))
					continue;
				if (handle >= CharacterIdFloor)
				{
					labels[handle] = "another player";
					continue;
				}

				var position = SpawnPosition(e.Raw);
				var table = isChar ? npcs : objects;
				if (position == null || table.Count == 0)
					continue;

				var (x, y, z) = position.Value;
				var best = table.MinBy(s => (s.X - x) * (s.X - x) + (s.Y - y) * (s.Y - y) + (s.Z - z) * (s.Z - z));
				var d = Math.Sqrt((best.X - x) * (best.X - x) + (best.Y - y) * (best.Y - y) + (best.Z - z) * (best.Z - z));
				if (!string.IsNullOrEmpty(best.Name) && d <= 25)
					labels[handle] = d < 2 ? best.Name : $"{best.Name} (+{d:F0}u)";
			}

			return labels;
		}

		// The spawn position, at the SAME named offsets the spawn builder writes and NpcSpawnExtractor reads
		// (SpawnPacketBuilder.Pos*Offset) - little-endian floats. Sharing the constants is the point: a timeline that
		// re-derived them would be one more place for them to drift.
		private static (float X, float Y, float Z)? SpawnPosition(byte[] raw)
		{
			return raw.Length < SpawnPacketBuilder.PosZOffset + 4
				? null
				: (raw.ReadF32Le(SpawnPacketBuilder.PosXOffset), raw.ReadF32Le(SpawnPacketBuilder.PosYOffset),
					raw.ReadF32Le(SpawnPacketBuilder.PosZOffset));
		}

		private static string Ent(Dictionary<uint, string> labels, uint handle)
		{
			return labels.TryGetValue(handle, out var name) ? $"{name} 0x{handle:x}" : $"0x{handle:x}";
		}

		// One line's worth of meaning. Every branch goes through a CODEC where one exists - that is the point of the
		// report living here rather than in a script - and states the id it could not resolve where one does not.
		//
		// A codec's Parse is written for the bodies its subsystem SENDS and may throw on a shape a capture happens to
		// carry (HateListCodec does, on a shorter body than it models). A diagnostic that reads arbitrary retail
		// traffic must not die on one message: the line degrades to its raw hex, which is still the evidence, and the
		// run continues. Swallowing it here is deliberate and is NOT a licence for a codec to be wrong - the raw hex
		// on the line is exactly what shows a decode is missing.
		private static string Describe(TimelineEvent e, TimelineLookups look, Dictionary<uint, string> labels,
			Dictionary<string, int> refused)
		{
			try
			{
				return DescribeCore(e, look, labels);
			}
			catch (Exception ex)
			{
				// ⭐ Counted, not just printed. A codec that throws on retail traffic is the most useful thing a
				// timeline run can find, and one line in ten thousand is invisible - the footer says which codecs
				// refused how many records, which is a decode-coverage measurement over a whole session.
				refused[e.Name] = refused.GetValueOrDefault(e.Name) + 1;
				return $"(undecoded: {ex.GetType().Name})";
			}
		}

		private static string DescribeCore(TimelineEvent e, TimelineLookups look, Dictionary<uint, string> labels)
		{
			// Every IIR codec now takes the CONTENT (from the sub-opcode on) - #530 rule 2 moved the carrier's u32
			// length out of the codecs and onto the sender, which retired the split where half of them read the length
			// first. `raw` is still the framed bytes, for the branches that have no codec yet.
			var raw = e.Raw;
			var content = e.Content;
			switch (e.Name)
			{
				case "InitiateAuthentication" when raw.Length > 2:
				{
					return $"account \"{Encoding.Latin1.GetString(raw, 2, Math.Min(raw.ReadU16Be(0), raw.Length - 2))}\"";
				}

				case "PlayfieldFIIR" when raw.Length >= 38:
				{
					var pf = raw.ReadU32Be(34);
					return $"playfield {pf}" + (look.Maps.TryGetValue(pf, out var m) ? $" \"{m}\"" : "");
				}
				case "FullPlayerCharacterIIR":
				{
					return $"login snapshot, {raw.Length} B";
				}

				case "DispatchUpdateBindLocation" when raw.Length >= 4:
				{
					return $"bind playfield {raw.ReadU32Be(0)}";
				}

				case "QuestGoalIIR":
				{
					var goal = QuestGoalCodec.Parse(content);
					return $"quest {goal.QuestId} goal {goal.GoalId} -> {goal.Update}";
				}
				case "QuestJournalIIR":
				{
					return Journal(QuestJournalCodec.Parse(content), look);
				}

				case "KnubotAnswerIIR" or "KnubotAnswerListIIR" or "KnubotAppendTextIIR"
					or "KnubotCloseChatWindowIIR" or "KnubotOpenChatWindowIIR":
				{
					return Dialogue(KnubotCodec.Parse(content), look, labels);
				}

				case "DispatchSetDialogueCamera":
				{
					var camera = SetDialogueCameraCodec.Parse(raw);
					return $"{Ent(labels, camera.NpcHandle)} shot size {camera.Camera.ShotSize} type {camera.Camera.ShotType}";
				}
				case "QuestIIR":
				{
					var quest = QuestCodec.Parse(content);
					return $"quest {quest.QuestId} state {quest.State}"
					       + quest.State switch { QuestCodec.StateActive => " (active)", QuestCodec.StateCompleted => " (completed)", _ => "" };
				}
				case "DispatchAddSpecialAbility":
				{
					var ability = AddSpecialAbilityCodec.Parse(content);
					return $"ability {ability.Id} slot {ability.Slot}"
					       + (look.Abilities.TryGetValue(ability.Id, out var an) ? $" = {an}" : "");
				}
				case "PerformSpecialAction" when raw.Length >= 4:
				{
					var slot = raw.ReadU32Be(0);
					return $"use ability slot {slot}" + (look.AbilityBySlot.TryGetValue(slot, out var n) ? $" = {n}" : "");
				}
				case "MagicBuffIIR":
				{
					// applyFrom is the CASTER and applyTo is who the buff lands on - the carrier handle is the TARGET.
					// This line had them the other way round until #530 wave 4 merged the three MagicBuffIIR emitters
					// and settled which endpoint is which (the NPC-cast golden pins it: applyFrom = the caster's spawn).
					var buff = MagicBuffCodec.Parse(content);
					var wireId = MagicBuffIds.WireIdOf(buff.SpellBaseId, buff.Form);
					var name = look.Spells.TryGetValue(buff.SpellBaseId, out var sn) ? sn : $"spell {buff.SpellBaseId}";
					var at = buff.ApplyTo != 0 && buff.ApplyTo != buff.ApplyFrom ? $" -> {Ent(labels, buff.ApplyTo)}" : "";
					var end = buff.Form == MagicBuffIds.FormEnd ? " (end)" : "";

					// A buff can carry NO caster (#614) - retail zeroes the class word. Say so rather than attributing
					// it to entity 0, which is a real handle space.
					var from = buff.ApplyFrom is { } caster ? Ent(labels, caster) : "(no caster)";
					return $"{from} buff {name}{at}{end} [0x{wireId:x}]";
				}
				case "ScryScriptIIR":
				{
					// The Tortage destiny/tutorial STEP driver. Without this the whole intro sequence
					// (StartFixed3d -> WelcomeTutorial -> ApproachCasilda -> CombatTutorial -> QuestRewards)
					// reads as undifferentiated hex, which is exactly the part of a create/beach capture one
					// opens a timeline to follow. int_parameter is the ZIGZAG-DECODED script-group id (#234).
					var scry = ScryScriptCodec.Parse(content).Script;
					var step = scry.type is ProtoScryScriptIIR.Type.ScriptMessage or ProtoScryScriptIIR.Type.PlayOrSkipQuestion
					           && scry.StringParameterOne.Length > 0
						? $" \"{scry.StringParameterOne}\""
						: "";
					// A step token carries no group id; a StartScriptGroupOnClient carries nothing else.
					var group = scry.IntParameter != 0 ? $" group {scry.IntParameter}" : "";
					var bound = scry.IdParameter is { Instance: not 0 } id ? $" -> {Ent(labels, id.Instance)}" : "";
					var res = scry.StringParameterTwo.Length > 0 ? $" (+{scry.StringParameterTwo.Length} B resources)" : "";
					return $"{scry.type}{step}{group}{bound}{res}";
				}
				case "LookAtIIR":
				{
					var look2 = LookAtCodec.Parse(content);
					return $"{Ent(labels, look2.Subject)} " + (look2.Target != 0 ? $"selects {Ent(labels, look2.Target)}" : "deselects");
				}
				case "StatDamageIIR":
				{
					return StatDelta(content, look, labels);
				}

				case "DamageResultIIR":
				{
					var hit = DamageResultCodec.Parse(content);
					return $"{Ent(labels, hit.Target)} <- {Ent(labels, hit.Attacker)}, "
					       + $"{hit.Damage} ({hit.Damage / 100.0:F2} shown), school {(uint)hit.School}";
				}
				case "ChainKillIIR":
				{
					return $"chain {ChainKillCodec.Parse(content).Count}";
				}

				case "HateListUpdateIIR":
				{
					var hate = HateListUpdateCodec.Parse(content);
					return $"{Ent(labels, hate.Handle)} -> {Ent(labels, hate.Target)}";
				}
				case "CombatShieldIIR":
				{
					return $"shield {CombatShieldCodec.Parse(content).Value}";
				}

				case "PetIIR":
				{
					var pet = PetCodec.Parse(content);
					return $"{Ent(labels, pet.Pet)} " + (pet.Mode == PetCodec.ModeRemove ? "dismissed" : "summoned");
				}
				case "N3ToClientFadeIIR" when raw.Length >= 16:
				{
					return $"despawn {Ent(labels, raw.ReadU32Be(12))}";
				}

				case "N3ToClientQuitIIR" when raw.Length >= 16:
				{
					return $"quit {Ent(labels, raw.ReadU32Be(12))}";
				}

				case "SimpleDynelVisualChangeIIR":
				{
					// Through the codec rather than off byte 20: `Kind` is the client's own visual-change selector and
					// several of them carry a resource NAME (an animation, a mesh) that byte 20 could never show.
					var visual = SimpleDynelVisualChangeCodec.Parse(content);
					var named = string.IsNullOrEmpty(visual.Name) ? "" : $" \"{visual.Name}\"";
					var words = visual.Words.Count == 0 ? "" : " " + string.Join(" ", visual.Words.Select(w => $"0x{w:x}"));
					return $"{Ent(labels, visual.Handle)} visual kind 0x{visual.Kind:x2}{named}{words}";
				}

				// ⭐ The single busiest non-noise record in a gameplay capture - 720 of them in the herald play2 dump,
				// every one of which used to print as raw hex. `BehaviorId` is the CLIENT's own command catalog and
				// `Args` is a `CommandMessage`, so a line can say what the entity actually DID.
				case "BehaviorControlCenterIIR":
				{
					return Behavior(BehaviorControlCenterCodec.Parse(content), look, labels);
				}

				case "StatUpdateIIR":
				{
					var update = StatUpdateCodec.Parse(content);
					var stats = string.Join(", ", update.Stats.Records.Select(r =>
						$"{look.Stats.GetValueOrDefault((uint)r.StatId, $"stat {(uint)r.StatId}")} {r.RawValue}"));
					return $"{Ent(labels, update.EntityHandle)} {stats}";
				}

				// The three POSE records. Position is what a reader is after in all of them, and `StopIIR` doubly so -
				// it is where an NPC came to rest, which is the evidence a patrol/leash reading is checked against.
				case "StopIIR":
				{
					var stop = StopCodec.Parse(content);
					return $"{Ent(labels, stop.Handle)} stops at {Pos(stop.Position)}";
				}

				case "SetPosIIR":
				{
					var pos = SetPosCodec.Parse(content);
					return $"{Ent(labels, pos.Handle)} placed at {Pos(pos.Position)}" + (pos.Flag == 0 ? "" : $" flag {pos.Flag}");
				}

				case "N3TeleportIIR" or "IntendedTeleportIIR":
				{
					var teleport = TeleportCodec.Parse(content);
					var destination = teleport.Proxy.MapId;
					var where = teleport is N3TeleportIIR n3 ? $" to {Pos(n3.ArrivalPosition)}" : "";
					return $"teleport{where} on playfield {destination}"
					       + (look.Maps.TryGetValue(destination, out var mapName) ? $" \"{mapName}\"" : "");
				}

				case "ThreatLevelIIR":
				{
					var threat = ThreatLevelCodec.Parse(content);
					return $"{Ent(labels, threat.Handle)} threat {threat.Threat} flag {threat.Flag}";
				}

				case "LockedToChangedIIR":
				{
					var locked = LockedToChangedCodec.Parse(content);
					return $"{Ent(labels, locked.Handle)} locked to "
					       + (locked.TargetInstance == 0 ? "nothing" : Ent(labels, locked.TargetInstance));
				}

				case "NewCurrentLooterIIR" or "NewAllowedLooterIIR":
				{
					var looter = NewLooterCodec.Parse(content);
					return $"bag {Ent(labels, looter.BagHandle)} -> "
					       + (looter.LooterHandle == 0 ? "nobody" : Ent(labels, looter.LooterHandle));
				}

				case "CharInPlayIIR":
				{
					return $"{Ent(labels, CharInPlayCodec.Parse(content).Handle)} in play";
				}

				case "QuestFullUpdateIIR":
				{
					var full = QuestFullUpdateCodec.Parse(content);
					var record = full.Record;
					var area = look.Line(record.Area);
					return $"quest {record.QuestId} (line {record.QuestLineId})"
					       + (area.Length > 0 ? $" in \"{area}\"" : "")
					       + (record.Summary.Length > 0 ? $": \"{look.Line(record.Summary)}\"" : "");
				}

				case "LootRemoveItemsIIR":
				{
					var removed = LootRemoveItemsCodec.Parse(content);
					return $"bag {Ent(labels, removed.BagHandle)} loses slot(s) {string.Join(", ", removed.RemovedPositions)}";
				}

				case "AreaFormulaIIR":
				{
					var area = AreaFormulaCodec.Parse(content);
					var at = area.Positions.Length > 0 ? $" at {Pos(area.Positions[0])}" : "";
					return $"{Ent(labels, area.Handle)} area effect{at}, {area.Refs.Length} ref(s)";
				}

				case "UpdateSimpleDynelIIR":
				{
					var dynel = UpdateSimpleDynelCodec.Parse(content);
					return $"{Ent(labels, dynel.Handle)} 0x{dynel.Word0:x} 0x{dynel.Word1:x} [{dynel.B0:x2} {dynel.B1:x2} {dynel.B2:x2} {dynel.B3:x2}]";
				}

				case "TemplateActionIIR":
				{
					var action = TemplateActionCodec.Parse(content);
					return $"{Ent(labels, action.Handle)} template action, {action.Template.Length} B template";
				}

				case "UpdateInventoryIIR":
				{
					var inventory = UpdateInventoryCodec.Parse(content);
					return $"{Ent(labels, inventory.Handle)} inventory, {inventory.InventoryProtobuf.Length} B";
				}

				case "GameTimeIIR":
				{
					var clock = GameTimeCodec.Parse(content);
					return $"day {clock.GameDay}, {TimeSpan.FromSeconds(clock.DayTimeSeconds):hh\\:mm\\:ss} in-game, rate x{clock.Rate:0.##}";
				}

				case "DispatchLockTemplate":
				{
					var locked = LockTemplateCodec.Parse(content);
					var what = look.Spells.TryGetValue(locked.TemplateId, out var sn) ? $"\"{sn}\"" : $"template {locked.TemplateId}";
					return $"{what} locked for {locked.SecondsRemaining:0.##}s";
				}

				case "DispatchRemoveItem":
				{
					var removed = RemoveItemCodec.Parse(content);
					return $"{removed.Container} slot {removed.ItemPos}"
					       + (removed.AmountToRemove == 0 ? "" : $" x{removed.AmountToRemove}");
				}

				case "DispatchValidResurrectionSpots":
				{
					var spots = ValidResurrectionSpotsCodec.Parse(content);
					return $"{spots.Spots.Count} resurrection spot(s) from {Pos(spots.ReferencePosition)}";
				}

				case "CoreClaimIIR":
				{
					return CoreClaimCodec.Parse(content) switch
					{
						CoreClaimListIIR list => $"{list.Claims.Count} claim(s) available",
						CoreClaimRedeemAckIIR ack => $"claim {ack.Claim.Index} redeemed"
						                             + (look.Items.TryGetValue(ack.Claim.ItemTemplateId, out var claimed) ? $" = \"{claimed}\"" : ""),
						_ => "new claims"
					};
				}

				// ⚠️ `DynelHandle` is the element's TYPE ID, not a handle - `Build(frameHandle, action)` passes
				// `action.TypeId` into that slot. Printing it as an entity would name it after whatever spawn happened
				// to share the number. The generic text scrape still runs, because several action types carry a
				// RESOURCE NAME in the tail ("Ignore.particleeffect") and that is the readable half of the line.
				case "GameActionListIIR":
				{
					var list = GameActionListCodec.Parse(content);
					var kind = GameActionTypes.Get(list.DynelHandle)?.Name ?? $"type {list.DynelHandle}";
					var text = look.Text(raw);
					return $"{Ent(labels, list.FrameHandle)} {kind}, {list.ActionTail.Length} B"
					       + (text.Length > 0 ? $" {text}" : "");
				}

				case "DispatchSetItemStat":
				{
					var itemStat = SetItemStatCodec.Parse(content);
					var statName = look.Stats.GetValueOrDefault(itemStat.StatId, $"stat {itemStat.StatId}");
					return $"{itemStat.Container} slot {itemStat.Slot} {statName} = {itemStat.Value}"
					       + (itemStat.Value == uint.MaxValue ? " (cleared)" : "");
				}

				case "DispatchAddItem":
				{
					return AddedItem(raw, look);
				}

				case "DispatchUpdateCash" when raw.Length >= 12:
				{
					return $"cash {raw.ReadU32Be(4)} (+{raw.ReadU32Be(8)})";
				}

				case "DispatchOpenLootBag" or "DispatchCloseLootBag" or "TryLootAll" or "TryLootCash" when raw.Length >= 8:
				{
					return $"bag {Ent(labels, raw.ReadU32Be(4))}";
				}

				case "SimpleCharFullUpdateIIR" or "SimpleDynelFullUpdateIIR" or "DoorFullUpdateIIR"
					or "LootBagFullUpdateIIR" or "TombStoneFullUpdateIIR" or "ClimbingDynelFullUpdateIIR" when raw.Length >= 16:
				{
					var at = SpawnPosition(raw) is { } p ? $" at ({p.X:F1}, {p.Y:F1}, {p.Z:F1})" : "";
					return $"spawn {Ent(labels, raw.ReadU32Be(12))}{at}";
				}
				default:
				{
					return look.Text(raw);
				}
			}
		}

		private static string Pos(Vector3 p)
		{
			return $"({p.X:F1}, {p.Y:F1}, {p.Z:F1})";
		}

		// One behaviour record: WHO, which command, which phase of it, and what the CommandMessage says about it.
		//
		// This is the busiest record in a gameplay capture by a wide margin and it used to print as raw hex - which
		// meant every swing, cast, combo step, equip and object interaction in a session was invisible in the one
		// report meant to say what the session did.
		//
		// Three sources make the line readable, and all three are already gated elsewhere:
		//   * `BehaviorId` is the CLIENT's own command classmap (#536), so `0x0F` reads `CombatMelee` and not a number;
		//   * `Seq` is the phase - `BehaviorAnimations.SeqBegin` and its siblings name the four that matter;
		//   * `Args` is a `CommandMessage` protobuf, which carries the combo step, the execution status and the
		//     animation id the record is otherwise silent about.
		//
		// ⚠️ The args are parsed defensively. `Describe`'s catch would degrade the WHOLE line to hex on a body whose
		// args a future field breaks, and the actor/command/phase are worth having without them.
		private static string Behavior(BehaviorControlCenterIIR bcc, TimelineLookups look, Dictionary<uint, string> labels)
		{
			var command = Enum.IsDefined(typeof(BehaviorId), bcc.BehaviorId)
				? Enum.GetName(typeof(BehaviorId), bcc.BehaviorId)!
				: $"command 0x{bcc.BehaviorId:x2}";
			var phase = BehaviorPhases.GetValueOrDefault(bcc.Seq, $"phase 0x{bcc.Seq:x}");
			var delay = bcc.DelayCentiseconds is { } cs ? $" +{cs / 100.0:0.##}s" : "";

			var detail = new List<string>();
			try
			{
				var args = Serializer.Deserialize<CommandMessage>(new ReadOnlyMemory<byte>(bcc.Args));
				if (args.ShouldSerializeBccActionId())
					detail.Add($"action {args.BccActionId}");

				// ⭐ The combo id IS the `Combos` primary key, so a swing names its own combo with no correlation -
				// and combos are not in `Spells`, which is why the obvious lookup would miss every one.
				if (args.ShouldSerializeComboId())
					detail.Add("combo " + (look.Combos.TryGetValue(args.ComboId, out var comboName)
						           ? $"\"{comboName}\" ({args.ComboId})"
						           : args.ComboId.ToString())
					           + (args.ShouldSerializeComboAttackNum() ? $" step {args.ComboAttackNum}" : ""));

				if (args.ShouldSerializeExecutionStatus())
					detail.Add($"status {args.ExecutionStatus}");

				if (args.ShouldSerializeFlags())
					detail.Add($"flags 0x{args.Flags:x}");
			}
			catch (Exception ex)
			{
				detail.Add($"(args undecoded: {ex.GetType().Name})");
			}

			// The tail belongs to the COMMAND rather than to this record (see BehaviorControlCenterCodec), so its
			// LENGTH is what a timeline can honestly say - and it is a real signal: a no-tail command and a 39-byte
			// PlayAnimation are the same record shape until you look at it.
			var tail = bcc.Tail.Length == 0 ? "" : $" +{bcc.Tail.Length} B tail";
			return $"{Ent(labels, bcc.Actor)} {command} {phase}{delay}"
			       + (detail.Count > 0 ? " " + string.Join(" ", detail) : "") + tail;
		}

		// `Seq`'s named values - the phases of one behaviour, from BehaviorAnimations / BehaviorAcks. Everything else
		// prints as its number rather than being guessed at.
		private static readonly Dictionary<ushort, string> BehaviorPhases = new Dictionary<ushort, string>
		{
			[0x0004] = "begin", [0x0007] = "abort", [0x000A] = "complete", [0x000E] = "delay", [0x000F] = "commit"
		};

		// One conversation message, through KnubotCodec - which NPC, and what was said or picked.
		//
		// ⚠️ These are the lines a reader of a quest capture actually wants, and they were the WORST served by the
		// generic text scrape (#556): a Knubot body is [player][env][npc] followed by the payload, so the two bytes
		// PlainString found were the low half of the NPC handle - every one of them printed as "y)" (0x27929 = Turach)
		// rather than as dialogue. The answer list was worse still: it carries every option the player was offered and
		// the scrape showed only the first, the same first-record-wins truncation the journal had (#557).
		private static string Dialogue(KnubotIIR message, TimelineLookups look, Dictionary<uint, string> labels)
		{
			var who = Ent(labels, message.NpcHandle);
			switch (message)
			{
				case KnubotAppendTextIIR text:
				{
					return $"{who}: {look.Text(text.Xml)}";
				}

				case KnubotAnswerListIIR answers:
				{
					return $"{who} offers {answers.Strings.Count}: "
					       + string.Join(" | ", answers.Strings.Select(s => look.Text(s)));
				}

				case KnubotAnswerIIR answer:
				{
					return $"picks answer {answer.Index} to {who}";
				}

				case KnubotOpenChatWindowIIR:
				{
					return $"opens dialogue with {who}";
				}

				case KnubotCloseChatWindowIIR close:
				{
					// The close carries one screen that is the EMPTY sentinel in every capture - LDB[100][0], which
					// resolves to "" now that the plaintext categories are readable (#561). Printing it would leave a
					// dangling colon, so an empty screen renders as nothing rather than as an empty quote.
					var closing = look.Text(close.Text);
					return $"closes dialogue with {who}" + (closing.Length > 0 ? ": " + closing : "");
				}

				default:
				{
					return who;
				}
			}
		}

		// The whole J-panel journal, through QuestJournalCodec.
		//
		// ⚠️ This line used to fall through to the generic `look.Text(raw)`, which matches the FIRST <remoteformat> tag
		// in a body and stops - so all four journals in the necromancer play2 capture printed the same thing, the
		// container title "[Destiny 01] Shipwrecked", while 10 to 16 records per message were dropped in silence
		// (#557). A journal carries three header strings plus TWO per quest (title cat 50301, sub-line cat 50303) plus
		// two more for the highlighted entry, and it is re-sent whole on every change, so it grows as the chain
		// advances: 11 records at frame 875, 17 by frame 6115.
		//
		// The sub-line is deliberately NOT printed. It is the multi-sentence objective blurb, and six of them would
		// bury the line the reader came for; the title is what identifies a quest. `>` marks the entry the journal
		// highlights - the quest just accepted or advanced.
		private static string Journal(QuestJournalIIR journal, TimelineLookups look)
		{
			var areas = new List<QuestJournalArea>(1 + journal.MoreAreas.Count) { journal.Area };
			areas.AddRange(journal.MoreAreas);

			var parts = new List<string>();
			foreach (var area in areas)
			{
				var where = string.Join(", ", new[] { look.Line(area.MapName), look.Line(area.RegionName) }
					.Where(s => s.Length > 0).Distinct());
				var entries = area.Entries.Select(e =>
					(e.QuestId == journal.Current.QuestId ? ">" : "") + $"{e.QuestId} \"{look.Line(e.Title)}\"");
				parts.Add($"\"{look.Line(area.QuestLineTitle)}\"" + (where.Length > 0 ? $" ({where})" : "")
				          + $" {area.Entries.Count} quest(s): " + string.Join(", ", entries));
			}

			return string.Join(" | ", parts);
		}

		// The stat delta and its cause, through StatDamageCodec - now by NAME rather than by index into a bag of
		// seventeen words (#530 wave 4). Like combat damage the delta is display-scaled x100, which makes a
		// damage-over-time tick read -7.07.
		private static string StatDelta(byte[] content, TimelineLookups look, Dictionary<uint, string> labels)
		{
			var msg = StatDamageCodec.Parse(content);
			var stat = look.Stats.TryGetValue(msg.StatId, out var sn) ? sn : $"stat {msg.StatId}";
			var cause = msg.SpellBaseId == 0
				? ""
				: look.Spells.TryGetValue(msg.SpellBaseId, out var cn) ? $" via {cn}" : $" via spell {msg.SpellBaseId}";
			return $"{Ent(labels, msg.Handle)} {stat} {msg.Delta / 100.0:+0.00;-0.00} from {Ent(labels, msg.Caster)}{cause}";
		}

		// The granted item, through the GENERATED protobuf rather than a hand walk: item_data.template1 is the
		// template. The stat list is deliberately not consulted - its stat 23 is a Static Instance id, the tempting
		// wrong answer, and fields 1-3 of item_data are the LDB name-key quads, which is what a raw tag scan finds.
		//
		// The body is AddItemBody.WriteAddItemBody's: [container][charId][itemPos][u32 len][entry][feedback]. The
		// entry must be sliced to its DECLARED length - a trailing feedback word rides after it, and handing those
		// extra bytes to the deserializer is a ProtoException, not a tolerated overrun.
		private static string AddedItem(byte[] raw, TimelineLookups look)
		{
			if (raw.Length < 16)
				return "";
			try
			{
				var length = (int)raw.ReadU32Be(12);
				if (length <= 0 || 16 + length > raw.Length)
					return "";

				var entry = Serializer.Deserialize<ProtoInventoryGameEntry>(new ReadOnlyMemory<byte>(raw, 16, length));
				var template = entry?.ItemData?.Template1 ?? 0;
				var slot = raw.ReadU32Be(8);
				return $"slot {slot}, template {template}"
				       + (look.Items.TryGetValue(template, out var n) ? $" = \"{n}\"" : "");
			}
			catch (Exception ex)
			{
				return $"(item undecoded: {ex.GetType().Name})";
			}
		}
	}
}
