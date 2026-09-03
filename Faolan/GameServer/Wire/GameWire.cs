using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Crypt;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Codecs.Apprentice;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.Faction;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Logging;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Network.Server.CsPlayerAgent;
using Faolan.Core.Network.Server.GameServer;
using Faolan.Core.Network;
using Faolan.Core.Progression;
using Faolan.Core.Protobuf;
using Faolan.Core;
using Faolan.GameServer.Apprentice;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Feats;
using Faolan.GameServer.Inventory;
using Faolan.GameServer.Progression;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Scripting;
using Faolan.GameServer.Session;
using Faolan.GameServer.Wire;
using Faolan.GameServer.World;
using Faolan.GameServer;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using ProtoBuf;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using System.Threading;
using System;

namespace Faolan.GameServer.Wire
{
	// The GameServer tier's shared WIRE senders and packet builders: the record framings several services emit
	// (stat updates, vitals, map change, the spell/ability grants, the banners). They were `internal static` members
	// of the listener, and 14 service files called `GameServerListener.X` for them - the last edge on which a
	// composed service reached BACK into the listener (#928 item 1). Static because they are pure functions of the
	// client and their arguments; the listener calls them like any other caller now.
	//
	// ⚠️ Wire ENCODING still lives in Codecs/ (the codec conventions); what lives here is the framing of an already
	// encoded body into an Rpc against this tier's endpoints, and the "send it" convenience around that.
	internal static class GameWire
	{
		// 0x5D85BFC7 MapChange = the PlayfieldFIIR: tells the client which playfield/instance the player is entering.
		// This is now a thin wrapper over the shared CreateEditorRecordBuilder.BuildPlayfieldFiir codec (the create-flow
		// builds the SAME packet) - it just prepends the MapChange subop and sources the PER-MAP rule fields from the Map
		// row (#308). The client keys per-area rules on this packet (mount permission = PlayfieldFlags); those fields are
		// NOT universal (beach vs mainland differ), so they come from the DB (Map.PlayfieldCount/BlobLastWord/Flags/Instanced), 		// defaulting to the beach values for un-sourced maps. Position is the character's entry transform (per-session);
		// nearClip 15.0 + the farClip float are camera params (left constant). `map` may be null (fresh/unmapped -> beach
		// defaults).
		// The word at body [55:59] (client obj+0x54). We send 1 for every map, which is what 97 of the 151 captured
		// records carry; the other 54 carry 0, and the split is not per-map (4000 and 4010 carry both). Was written
		// as 0x01000000 while the builder's framing was a byte out - see BuildPlayfieldFiir (#451). [V0]
		internal const uint SceneWord = 1u;

		// Points first, then rank: a rank is a threshold ON the points, so the client has the number the title is
		// about before it is told the title. Nothing captured says the order matters; this is the one that cannot
		// read as a title arriving for a standing the client has not seen.
		internal static async Task SendFactionStanding(INetworkClient client, uint factionId, int points, uint rank)
		{
			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchApplyFactionPoints, ApplyFactionPointsCodec.Build(factionId, points)));

			await client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchApplyFactionRank, ApplyFactionRankCodec.Build(factionId, rank)));
		}

		// #792: the zero-length GameCharInterface `0x1B` retail sends immediately before every `IntendedTeleportIIR` -
		// **102 of 102 across the corpus, no exceptions**, on cross-map handoffs and same-map revives alike.
		//
		// ⚠️ The client does NOTHING with it: `GamecodeClient_t`'s override of the vtable slot it dispatches to
		// (+0x70) is an empty function. So this is sent for fidelity with retail's byte stream and for nothing else -
		// do not hang behaviour off it, and do not go looking for what it "should" do.
		internal static Task SendTeleportPending(INetworkClient client)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchTeleportStarted, _ => { }));
		}

		// `now` and the offset are parameters so a golden test can feed the capturing instant and the captured dayTime
		// and get the captured bytes back.
		internal static Rpc BuildGameTime(INetworkClient client, DateTimeOffset now, long offsetGameSeconds, float rate = 1.0f)
		{
			return BuildGameTime(client, GameClock.TimeOfDay(now, offsetGameSeconds), now, rate);
		}

		internal static Rpc BuildGameTime(INetworkClient client, float dayTimeSeconds, DateTimeOffset now, float rate = 1.0f)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(GameTimeCodec.Build(dayTimeSeconds, now, rate)));
		}

		// Frames the GuildUpdateIIR_c (GuildUpdateCodec) on the world conversation: [RpcHeader][DispatchPassBlob]
		// [u32 len][GuildUpdateIIR]. Sent on login for guilded characters and pushed live by the CsPlayerAgent
		// guild-accept flow through the dispatcher seam. The body grammar + round-trip live on GuildUpdateCodec.
		internal static Rpc BuildGuildTag(INetworkClient client, uint characterId, string guildName)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(GuildUpdateCodec.Build(characterId, guildName)));
		}

		// ⭐ #512: `teamId` names the TEAM that owns this copy of the playfield, when one does. The proxy's owner pair
		// is `(type, id)`, and retail's own group entry carries `(0xDEA9, <teamId>)` there where a personal instance
		// carries `(PlayfieldFieldA, <characterId>)` - the Amphitheatre of Karutonia entry names the exact id
		// `DispatchYouJoinedTeam` had announced three seconds earlier. So the pair is switched WHOLE rather than
		// having the id swapped under the map's own type word: a team id under type 2 would be a shape retail never
		// sends. Null (the common case) leaves the personal form exactly as it was.
		internal static Rpc BuildMapChange(INetworkClient client, Map map, uint playfieldToken, uint? teamId = null, bool epic = false)
		{
			var character = client.Session.Character;
			var charInstance = map?.PlayfieldInstanced ?? true
				? character.Id
				: 0u;
			var count = map?.PlayfieldCount ?? 2u;
			var blobLastWord = map?.PlayfieldBlobLastWord ?? 3u;
			var flags = map?.PlayfieldFlags ?? 0u;
			// #451: the two per-map words that used to be builder constants. The `??` defaults are those constants, so
			// a map with no imported MapChange behaves exactly as before rather than silently getting a zero.
			var fieldA = map?.PlayfieldFieldA ?? 0u;
			var fieldC = map?.PlayfieldFieldC ?? 7u;

			// The owner pair, switched as a unit when a team owns the copy (see the remark above).
			var ownerType = teamId is null ? fieldA : PlayfieldProxy.TeamOwnerType;
			var ownerId = teamId ?? charInstance;

			// #309: the proxy's class word is the copy's DIFFICULTY - 0xCABC on the captured epic Spider Caverns
			// entry against 0xC79C everywhere else. Everything else in the record is mode-independent.
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				s => s.WriteArrayUInt32Length(PlayfieldFCodec.Build(new Vector3(character.PositionX, character.PositionY, character.PositionZ), character.MapId,
					ownerId, count, playfieldToken, 15.0f, BitConverter.UInt32BitsToSingle(0x40E3944Fu), flags, SceneWord, blobLastWord, ownerType, fieldC,
					epic ? PlayfieldProxy.EpicPlayfieldClass : PlayfieldProxy.PlayfieldClass)));
		}

		// 0x96B8DC59 - entity vitals / stat-update packet. The wire builder now lives on World.VitalsPacket (shared
		// with the CombatService); this delegates so the existing SendStatUpdate/SendVitals/BuildVitals callers
		// (login replay, GM, the regen tick) are unchanged.
		internal static Rpc BuildStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return VitalsPacket.BuildStatUpdate(client, stats, entityHandle);
		}

		// Builds the player's own vitals (0x96B8DC59) from the player's LIVE world-model vitals - the world-entry
		// replay emits this instead of replaying the captured player's vitals.
		//
		// `vitals` must be the live EntityVitals, not `character.MaxVitals()`. The raw class/level curve does not
		// include worn gear, so sending it told the client a current the server did not agree with: an L23 Assassin
		// with +156 stamina of gear was announced at 742 while the world model held 898, and the bar sat short of
		// where regen would immediately put it.
		internal static Rpc BuildVitals(INetworkClient client, EntityVitals vitals)
		{
			var character = client.Session.Character;
			var stats = new CharacterMaxStats { Health = vitals.MaxHealth, Mana = vitals.MaxMana, Stamina = vitals.MaxStamina };
			// Emit the full value under the CURRENT ids (0x001B/0x01F8/0x01FA) - the retail full-spawn announce sends
			// current (== max at full health); the max ids ride the snapshot/tails. (in-client proof: CapturedCharacterStats.)
			// Also carry Level + Exp/MaxExp so the XP bar reflects the character's REAL progress: the snapshot tails
			// carry the captured template's stale to-next-level max, so overriding it here (the same Exp/MaxExp the
			// combat XP path sends) scales the bar correctly for this character's level.
			// #214: a dead character must NOT get its current health refilled here - the world-entry snapshot just set
			// HealthCurrent = 0 to latch the client's dead state, and this post-snapshot vitals update would otherwise
			// refill the bar (and un-latch the release window). Send 0 current health while dead.
			var currentHealth = character.IsDead
				? 0u
				: stats.Health;
			var block = new CharacterStatBlock()
				.Set(CharacterStatId.HealthCurrent, currentHealth)
				.Set(CharacterStatId.StaminaCurrent, stats.Stamina)
				.Set(CharacterStatId.ManaCurrent, stats.Mana)
				// Level AND ApprenticeLevel - the client divides by the latter for armour mitigation, and the
				// nameplate renders max() of the two. See CharacterStatBlock.SetLevel.
				.SetLevel(character.Level)
				.SetExperience(character.Level, character.Experience);

			return BuildStatUpdate(client, block, client.Session.Character.Id);
		}

		// Assembles the complete world-entry snapshot packet for `client` from a pre-built root + tails. The
		// RECORD ([IirType=0x737A6DF9][TypeTag][Handle=instance][blob frame][root][tails]) is built by
		// FullPlayerCharacterCodec, which owns the blob frame next to the reader that parses it - it used to be
		// stated here as well, and the two drifted (see that type's header). The carrier's u32 length is written
		// HERE, because it belongs to the sender (#530 rule 2), together with the RpcHeader + outer length/CRC. No
		// captured packet is consulted.
		internal static Rpc BuildWorldEntrySnapshot(INetworkClient client, ProtoFullCharacterIIR root, FullCharSnapshotSubClass tails)
		{
			var record = FullPlayerCharacterCodec.Build(new FullPlayerCharacterIIR(client.Session.Character.Id, root, tails));

			using var body = new FaolanStream();
			body.WriteArrayUInt32Length(record);
			return FrameWorldEntryRecord(client, body.ToArray());
		}

		// Assembles the complete world-entry player/NPC FA015807 spawn packet for `client` from a pre-built spawn
		// RECORD ([IirType=0xFA015807][TypeTag][Handle][FieldA][transform + pre-IIR + CharacterIIR + post-IIR]) - the
		// form SimpleCharFullUpdateCodec builds. The carrier's u32 length is written HERE, because it belongs to the
		// sender (#530 rule 2), which is what lets a from-scratch record reach the wire without a captured header.
		internal static Rpc BuildWorldEntrySpawn(INetworkClient client, byte[] spawnRecord)
		{
			ArgumentNullException.ThrowIfNull(spawnRecord);
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, s => s.WriteArrayUInt32Length(spawnRecord));
		}

		// Prepends the shared world-entry RpcHeader + outer length/CRC (the WriteHeader/WriteLengthHash path) to a
		// record body that ALREADY carries its own [u32 bodyLen] prefix - i.e. the `packet[bodyStart..]` form that
		// SpawnPacketBuilder and WriteArrayUInt32Length produce. This is the keystone that lets a
		// from-scratch record body reach the wire without a captured packet header.
		internal static Rpc FrameWorldEntryRecord(INetworkClient client, params ReadOnlySpan<byte> bodyWithLenPrefix)
		{
			return new Rpc(GameEndpoints.WorldSenderEndpoint, client.Endpoint(RpcInterfaces.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob, bodyWithLenPrefix.ToArray());
		}

		// #348: the top-of-screen FIFO banner naming the SUB-AREA the player just walked into. Retail fires one on
		// every sub-area crossing, not once per playfield - Old Tarantia alone has six (Bridge of Vilerus I, The East
		// Gate, The West Gate, ...) - so the world tick drives this off MapArea, and the entry banner is simply the
		// first crossing. `areaNameId` is the client's localized-string id; the client renders the text itself.
		//
		// This used to take a mapId and read the single `Map.AreaNameId`, which could only ever name one area per
		// playfield (it had picked "The West Gate" for Old Tarantia, an arbitrary one of the six).
		internal static Task SendAreaNameBanner(INetworkClient client, uint areaNameId)
		{
			return SendFeedback(client, new AreaNameBanner(areaNameId));
		}

		// Pushes a FormatFeedback message to the client's on-screen feedback, addressed to their GameInterface
		// instance. The PAYLOAD carries its own typeFlags/footer (#539) - this used to take the child's text plus those
		// two words, which is how the same message came to be framed differently in two services.
		//
		// The carrier's [u32 len] is the SENDER's (#530 rule 2), which is what WorldIir.Send writes - the codec emits
		// the record alone.
		internal static Task SendFeedback(INetworkClient client, FeedbackPayload payload)
		{
			return WorldIir.Send(client, FormatFeedbackCodec.Build(client.Session.Character.Id, payload));
		}

		// #372: the "resurrection point discovered" FIFO banner. Retail sends it in the SAME frame as the
		// resurrection-spots re-send that adds the point to the player's list, and it is what the player actually
		// sees; only the point id goes on the wire (the client resolves the name from LDB category 270).
		internal static Task SendRespawnPointDiscoveredBanner(INetworkClient client, uint pointId, string? nameKey)
		{
			return SendFeedback(client, new RespawnPointDiscovered(pointId, nameKey));
		}

		internal static Task SendStatUpdate(INetworkClient client, CharacterStatBlock stats, uint entityHandle)
		{
			return client.SendAsync(BuildStatUpdate(client, stats, entityHandle));
		}

		internal static Task SendValidResurrectionSpots(INetworkClient client, Vector3 deathPosition, IReadOnlyList<RespawnPoint> spots)
		{
			return client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x59), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchValidResurrectionSpots, ValidResurrectionSpotsCodec.Build(ValidResurrectionSpotLists.Known(deathPosition, spots))));
		}

		// Sends a live spell grant (DispatchLearnSpell, s2c 0x200A) - the retail "learn a spell" packet (#73). Grants a
		// spell mid-session (level-up / quest-reward / GM) without rebuilding the FullChar snapshot. Rides the same
		// GameCharInterface conversation as the 0x2052 ability grant; the body is a single u32 spell id (decoded in
		// tools/capture/decode_learn_spell.py, byte-exact in SpellBookChangeTests). The client pops the "learned a new
		// spell" toast itself. (The siblings RemoveSpecialAbility 0x2053 / UntrainSingleFeat 0x2011 have no capture
		// oracle yet, so they are not built - see #73.)
		internal static Task SendLearnSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchLearnSpell,
				SpellBookChangeCodec.Build(spellId)));
		}

		// The revoke half (DispatchForgetSpell, s2c 0x200B) - takes one spell back out of the client's spellbook
		// (#915). Same conversation and same one-u32 body as the grant; the client's own dispatch reader for the two
		// is the same function twice (see SpellBookChangeCodec). No capture carries a forget - retail only sends one
		// when something un-teaches a spell, which the corpus's sessions never do - but the client's reader settles
		// the body, so this is recovered, not invented.
		internal static Task SendForgetSpell(INetworkClient client, uint spellId)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchForgetSpell,
				SpellBookChangeCodec.Build(spellId)));
		}

		// Builds a single 0x2052 ability-grant packet from an Ability row. `announce` controls whether the client pops
		// the "You have learned a new ability" toast: true on a genuine first learn (create flow / .grantability / future
		// level-up), false for the silent per-login re-grant.
		// `sender` defaults to the login/world grant sender (GameCharAgent 0x58); the create-editor world-prep grant
		// rides its own conversation (GameCharAgent 0x0C), so it passes that in - the only thing that differs between
		// the two grant sites (the 81-byte body incl. the TEA key comes wholly from the Ability row either way).
		internal static Task SendAddSpecialAbility(INetworkClient client, Ability ability, bool announce = true, ProtoEndpoint? sender = null)
		{
			return client.SendAsync(new Rpc(sender ?? GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchAddSpecialAbility,
				AddSpecialAbilityCodec.Build(ability, announce)));
		}

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		internal static Task SendWorldChat(INetworkClient client, string xml)
		{
			// As SendFeedback: the [u32 len] is the sender's (#530 rule 2), written by WorldIir.Send.
			return WorldIir.Send(client, ChatTextCodec.Build(client.Session.Character.Id, Encoding.Latin1.GetBytes(xml)));
		}

		// The level-scaled rating QUAD (#685) - AttackRating / DefenseRating / MagicRating / MagicalResist, four
		// player-addressed StatIIRs carrying one number, in the wire order the corpus sends them.
		//
		// It rides the login snapshot (`SnapshotStatSheet`), and until now that was the ONLY place it ever went - so a
		// character who dinged read the level it logged in at until the next relog. Retail restates it, and the corpus
		// names two of the occasions outright:
		//
		//   - a LEVEL-UP. 15 of the 18 captured `NewLevelIIR` events are followed by a quad burst, 11 of them within
		//     0.1 s, i.e. inside the ding burst itself. That one is a correctness fix, not fidelity: the value is
		//     `400 x level - 300` and the level just changed.
		//   - the COMBAT TOGGLE (#505). 13 of 13 captured 0x4E2 presses draw a burst 0.31-1.61 s later, against a
		//     median burst interval of 10.4 s across the corpus - so it is causal, not adjacency. Mechanically it is
		//     what a sheathe/draw IS: the active weapon changes, so the ratings are restated.
		//
		// ⚠️ Those are two occasions, not THE rule: bursts arrive every ~10 s in a live capture and most of them
		// follow neither. [V0: what makes retail re-send the quad] stands - what is settled is the VALUE.
		internal static async Task SendCombatRatings(INetworkClient client)
		{
			var character = client.Session.Character;
			var rating = CombatRatingCurve.For(character.Level);
			foreach (var stat in CombatRatingCurve.Stats)
				await WorldIir.Send(client, StatCodec.Build(StatIirs.ForCharacter(character.Id, (uint)stat, rating)));
		}

		// ---- SPELL RESISTANCE (#869) ----
		//
		// `DispatchAddSpellResistance` / `DispatchRemoveSpellResistance` - a per-character resistance to one spell
		// METATYPE (LDB category 55200), on its own registry rather than on the stat sheet or the buff list.
		//
		// ⚠️ Retail never sends this ALONE: every captured frame accompanies a `Criminal Protection` apply or end in
		// the same frame (`CriminalStatus.CriminalProtectionSpellId`). When #503 grants that buff, the matching
		// resistance belongs beside it - which is why this takes the add/remove verb rather than being two methods.
		internal static Task SendSpellResistance(INetworkClient client, uint metaType, uint percentage, bool add)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				add ? GameCharInterfaceMethods.DispatchAddSpellResistance : GameCharInterfaceMethods.DispatchRemoveSpellResistance,
				SpellResistanceCodec.Build(metaType, percentage)));
		}

		// ---- MAP MARKERS (#868) ----
		//
		// `DispatchAddWayPoint` / `DispatchRemoveWayPoint` on GameCharInterface. The endpoint pair is the ordinary
		// GameCharAgent -> GameCharInterface one every other per-character dispatch uses; the captured sender
		// instances (0x57 / 0x59 / 0x5A) are the per-session conversation ids, not a distinct conversation.
		internal static Task SendAddWayPoint(INetworkClient client, uint tombstoneInstance, uint playfieldId, float x, float y, float z,
			uint label = WayPointCodec.TombstoneLabel, uint toolTip = WayPointCodec.TombstoneToolTip)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchAddWayPoint, WayPointCodec.Build(tombstoneInstance, playfieldId, x, y, z, label, toolTip)));
		}

		internal static Task SendRemoveWayPoint(INetworkClient client, EntityId id, uint playfieldId)
		{
			return client.SendAsync(new Rpc(GameEndpoints.LoginWorld201CSenderEndpoint, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchRemoveWayPoint, RemoveWayPointCodec.Build(id, playfieldId)));
		}
	}
}
