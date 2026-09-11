using System;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Cli;
using Faolan.Core.Constants;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Combat;
using Faolan.Core.Network.Codecs.Dialogue;
using Faolan.Core.Network.Codecs.Economy;
using Faolan.Core.Network.Codecs.FullCharSnapshot;
using Faolan.Core.Network.Codecs.Guild;
using Faolan.Core.Network.Codecs.Inventory;
using Faolan.Core.Network.Codecs.Quest;
using Faolan.Core.Network.Codecs.Team;
using Faolan.Core.Network.Codecs.Text;
using Faolan.Core.Network.Codecs.World;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Faolan.Core.Protobuf;
using Faolan.GameServer.Behavior;
using Faolan.GameServer.Combat;
using Faolan.GameServer.CreateFlow;
using Faolan.GameServer.Dialogue;
using Faolan.GameServer.Economy;
using Faolan.GameServer.Quest;
using Faolan.GameServer.Session;
using Faolan.GameServer.World;
using Faolan.GameServer.Wire;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		private async Task HandleIirCarrier(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// Snapshot the whole 0x2000 sub-payload before consuming it, so every case below can read the raw bytes.
			// This starts at the 4-byte iirLength header; ToArray() returns the whole body window regardless of the
			// cursor, so it is safe to take after the shared reader arrives positioned at 0.
			var rawPayload = body.ToArray();

			var iirLength = body.ReadUInt32();
			var iirType = body.ReadUInt32<IirTypes>();

			// The .char per-character packet trace. One flagged character logs at information level while everyone
			// else stays at debug, which is what keeps it usable on a populated server.
			if (client.Session.LogAllIirs)
				Logger.LogInformation("GameServer [.char] {Character:X8} <- {Iir} ({Length} bytes) raw={Raw}",
					client.Session.Character?.Id ?? 0, iirType, rawPayload.Length, Convert.ToHexString(rawPayload));

			switch (iirType)
			{
				case IirTypes.CombatReactiveIIR:
				{
					// Spellweave add-component (docs/subsystems/combos.md). Each add is two packets: a 0x03-tagged carrier
					// carrying the componentIndex in the element list, and a 0x02-tagged ack twin whose list is empty.
					// CombatReactiveCodec reads both - the c2s add and the s2c element list are one grammar.
					var reactive = CombatReactiveCodec.Parse(rawPayload.AsSpan(sizeof(uint)));
					if (reactive is { Tag: CombatReactiveCodec.AddComponentTag, ElementBaseIds: [var componentIndex, ..] })
						await _spellweave.AddComponentAsync(client, componentIndex);

					break;
				}

				case IirTypes.CombatShieldIIR:
				{
					await HandlePlayerShieldRose(client, rawPayload);
					break;
				}

				case IirTypes.StatIIR:
				{
					// Create-editor (unnamed) avatar stat stream: retail sends no response, we just observe it.
					if (client.Session.IsUnnamedCreatePhase)
					{
						Logger.LogInformation("Observed retail create-editor (uint)IirTypes.StatIIR while pendingPost205F={PendingPost205F}.",
							client.Session.HasPendingPost205FFollowUp);
					}

					break;
				}

				case IirTypes.ScryScriptIIR:
				{
					await _createFlow.HandleCommandPacket(client, packet, body);
					break;
				}

				case IirTypes.CharDCMoveIIR:
				{
					await _movement.HandleMove(client, body, rawPayload);
					break;
				}

				case IirTypes.CharInPlayIIR: //peer0_160
				{
					// CharInPlayIIR ("ReadyForPlayScreen") means something different per connection role, so it routes on
					// EntryMode - the role decided once at Authenticate2 - and every mode is handled explicitly. A mode
					// with no branch must be reported, never silently treated as a login.
					switch (client.Session.EntryMode)
					{
						case WorldEntryMode.CreateEditor:
						{
							// The character-creator connection launches the editor with this. Retail answers directly
							// with the post-ready burst - 0x2024 + CharInPlayIIR + 0xF508F4C1 + the creation script
							// (Blablatest necro create: client CharInPlayIIR at f90 -> server burst at f95).
							Logger.LogInformation("Create-editor: CharInPlayIIR received; sending the retail post-ready burst to launch the editor.");
							await CreateEditorBursts.SendRetailCreateEditorPostReadyBurst(client);
							break;
						}

						case WorldEntryMode.CreateWorld:
						{
							// The post-creation world connection. Retail answers this CharInPlayIIR with the ready
							// burst and then the intro (c2s fr317 -> s2c 0x2024 fr324 -> s2c cutscene fr325); earlier
							// than this - e.g. off a 0x2020 poll while the intro runs - and the player gets a blue
							// screen instead of a cutscene.
							Logger.LogInformation("Create-world CharInPlayIIR: sending the create ready burst, then starting the intro cutscene.");
							client.Session.HasPendingPost205FFollowUp = false;
							await _worldEntry.SendCreateWorldReadyBurst(client);
							await CreateFlowService.SendCreateIntroCutscene(client);
							break;
						}

						case WorldEntryMode.TeleportReentry:
						case WorldEntryMode.Login:
						{
							// A changemap/teleport reconnect carries no 0x205F, so the client uses CharInPlayIIR as its
							// ready signal instead of the 0x2020 poll a normal login flushes on. Flushing the armed
							// follow-up here is what drops the loading screen into the world; Login shares the branch
							// because the flush no-ops unless something armed it.
							Logger.LogInformation("Named-character CharInPlayIIR ({Mode}, no 0x205F): flushing the world post-0x205F follow-up.", client.Session.EntryMode);
							await _worldEntry.FlushPendingPost205FFollowUp(client);
							break;
						}

						default:
						{
							// A mode added without deciding what this signal means for it. Report it rather than
							// guessing a ready sequence.
							Logger.LogError("CharInPlayIIR on an unhandled entry mode {Mode}: no ready sequence was sent, so this client will sit on its loading screen.", client.Session.EntryMode);
							break;
						}
					}

					break;
				}

				case IirTypes.KnubotAnswerIIR:
				{
					// The player picked a dialogue option. The pick is an ordinary Knubot record - KnubotBase header then
					// the chosen option index - and the index selects the branch, so route to the child screen at the
					// extended answer path (AdvanceConversation). No active conversation means no-op.
					if (KnubotCodec.Parse(rawPayload.AsSpan(sizeof(uint))) is KnubotAnswerIIR pick)
						await _conversation.AdvanceConversation(client, pick.Index);

					break;
				}

				case IirTypes.KnubotCloseChatWindowIIR:
				{
					// The player closed the NPC chat window (ESC/X). The client signals this with its own
					// KnubotCloseChatWindow, which it double-sends; retail just accepts it. The conversation session state
					// must be torn down here so the dialogue stays closed - left set, the next interaction re-enters a
					// stale conversation. No reply: the window is already closed on the client.
					ConversationService.EndConversation(client);
					break;
				}

				case IirTypes.BehaviorControlCenterIIR:
				{
					await _behavior.HandleBehaviorControlCenter(client, packet, body, rawPayload);
					break;
				}

				case IirTypes.CharacterActionIIR:
				{
					// Standard IIR carrier head: [u32 entity class][u32 handle]. Corpus (21 c2s CharacterActionIIRs,
					// every one on quit) continues [u32 0][u8 0x21][24 zero bytes] - the same [u32 0][u8 code] slot
					// QuestIIR below uses, with the action code 0x21 = the client's "CloseGame".
					body.ReadUInt32(); // EntityClass.Character (0xC350)
					var clientInst = body.ReadUInt32();

					Logger.LogInformation("GameServer close request for character {CharacterId}", clientInst.ToHex());

					// The character is persisted in ClientDisconnected, not here.
					break;
				}

				case IirTypes.LookAtIIR:
				{
					// LookAtIIR (0x0D155695): the client selects/deselects an entity. Body (after the 0x2000 framing):
					//   [u32 0x0000C350 type][u32 selectorInstance=self] [u8 00 00 00 pad]
					//   [u16 0xC350 targetTag][u32 targetHandle] [trailing counters]
					// On deselect the target tag+handle are zeroed. Decoded from
					// Hoxxling_mage-heraldofxotli_login_gameplay (frames 259 select / 362 deselect).
					// LookAtCodec reads the whole 29-byte record, so the deselect (both target words zeroed) and a live
					// select come off the same reader.
					var lookAt = LookAtCodec.Parse(rawPayload.AsSpan(sizeof(uint)));

					var targetHandle = lookAt.Target;
					client.Session.SelectedTargetHandle = targetHandle;
					// A (re-)select is genuine intent to interact - lift the post-close greeting suppression so a
					// deliberate re-talk opens (the reflexive post-close greeting carries no SelectDeselect).
					client.Session.ConversationEndedNpcHandle = null;

					Logger.LogInformation("SelectDeselect: subject 0x{Subject:X8} -> target 0x{Target:X8} (trailers {A}/{B})", lookAt.Subject, targetHandle, lookAt.TrailerA, lookAt.TrailerB);

					break;
				}

				case IirTypes.DeleteItemIIR:
				{
					// ProtoDeleteItemIIR, not the sibling move message: { container_id, item_pos, template_id }. The two
					// alias on the wire for fields 1-3, so reading this record as a move silently takes the item template
					// id for a destination container. See DeleteItemCodec.
					var drop = DeleteItemCodec.Parse(body.ReadRemainingArray()).Delete;

					Logger.LogInformation($"DropItem: {drop}");

					// Remove the dropped item from the persisted bag so it doesn't reappear on relog.
					await _equipment.PersistDropItem(client, drop);

					// Push the live remove so the open bag updates without a relog: the DropItem opcode has no implicit
					// client-side removal, so without this the dropped item stays visible until the next login. Same
					// DispatchRemoveItem (0x201F) the .removeitem GM path and RemoveItemFromBackpack use.
					if (drop?.ContainerId?.Type == (uint)ContainerKind.Bag)
						await EconomyOutbound.SendRemoveItem(client, (ContainerKind)drop.ContainerId.Type, (uint)drop.ItemPos);

					break;
				}

				case IirTypes.MoveInventoryItemIIR:
				{
					// The recovered client schema names this message ProtoMoveInventoryItemIIR:
					//   { source_container_id, source_item_pos, destination_container_id, destination_item_pos }
					// where each container id is a ProtoID32 { type, instance }; the instance is the container's owner
					// handle, not a per-item id. No server->client packet follows these moves.

					var move = MoveInventoryItemCodec.Parse(body.ReadRemainingArray()).Move;

					Logger.LogInformation($"MoveItemInInventory: {move}");

					// A move with the guild bank vault (kind 109) on either end is the same drag, but it changes the
					// item's owner rather than its cell - so it cannot go through PersistInventoryMove, whose every query
					// is scoped to this character. It also has a reply, which no ordinary move does.
					if (GuildBankService.IsVaultMove(move))
					{
						await _guildBank.Move(client, move);

						break;
					}

					// Persist the new slot positions so the player's bag arrangement survives logout (the world-entry
					// snapshot rebuilds the bag from these CharacterInventory rows).
					await _equipment.PersistInventoryMove(client, move);

					// A move that touches the personal bank (kind 105) gets no client-side render otherwise (the bank
					// tab isn't refreshed), so a deposit looks like the item vanished - push the live bank refresh.
					await _bank.UpdateBankAfterMove(client, move);

					break;
				}

				case IirTypes.QuestIIR:
				{
					// The client's own copy of the record the server sends as QuestIIR (QuestCodec.Build) - same grammar,
					// other direction: [u32 entity class][u32 handle][u32 0][u8 0x01][u32 state][u32 questId][u32 0]. The
					// client names this method "DeleteQuest" (QuestIIR_c, docs/protocol/gen/iir-types.md) and sends it with
					// state = QuestCodec.StateCompleted when the player drops a quest from the journal. Three corpus
					// instances: two in the Hakotep "delete-quests" capture (quest 0x388F and 0x3919) and one in
					// chat-annotated-4, where the player deletes quest 15051 to make journal room for a group-finder signup
					// that had just been refused for holding too many quests.
					//
					// State 4 c2s is not a completion: c2s carries state 4 and nothing else, while s2c carries 2 (x47) and
					// 4 (x50). The code is the slot; the direction is the meaning.
					body.ReadUInt32(); // EntityClass.Character (0xC350)
					var clientInst = body.ReadUInt32();
					body.ReadUInt32(); // [12..16] = 0, as in BuildQuestIir
					body.ReadByte(); // 0x01 record marker
					var state = body.ReadUInt32();
					var questId = body.ReadUInt32();
					body.ReadUInt32(); // trailing 0

					// Retail's answer is the same record echoed back, byte for byte, one frame later - both captured
					// deletes show c2s -> s2c with an identical body (frames 3937/3938 and 3975/3976). So the drop is
					// confirmed by the echo, not by a bespoke reply. Send it before the delete so the client is answered
					// even if persistence throws.
					await QuestBroadcaster.SendQuestIir(client, questId, state);
					await Database.RemoveCharacterQuest(client.Session.Character!.Id, questId); // a QuestIIR only arrives from a character in the world
					_questBroadcaster.ForgetMarkerContext(client.Session.Character.Id);

					Logger.LogInformation("QuestIIR (DeleteQuest) from character {CharacterId}: dropped quest {QuestId} (state {State}).", clientInst.ToHex(), questId, state);

					// Retail's third packet in the same frame: a MainQuestUpdateIIR (0x504B7924) refreshing the J-panel.
					// It is the ordinary record with `Operation = Remove` and an empty list, which `MainQuestUpdateCodec`
					// writes byte-for-byte (see `SendQuestLogRemove`).
					//
					// Sent only when the dropped quest was the last active one in its line. Every one of the six captured
					// Removes names a line whose section is going away - two are these deletes, the rest a zone change out
					// of the area - so "the line still has quests" is a case the corpus does not cover, and widening the
					// condition risks hiding entries that should still show. The journal is correct on the next login
					// either way.
					var droppedDef = await Database.GetQuestDefinition(questId);
					if (droppedDef != null)
					{
						var remaining = await Database.GetCharacterQuestsWithDefinitions(client.Session.Character.Id, QuestState.Active);
						if (remaining.All(q => q.Definition.QuestLineId != droppedDef.QuestLineId))
							await QuestBroadcaster.SendQuestLogRemove(client, droppedDef.QuestLineId);
					}

					break;
				}

				case IirTypes.CoreClaimIIR:
				{
					// The Claims/Redeem window: sub 0x0007 requests the list, sub 0x0003 redeems a claim. See
					// Economy/ClaimService.cs and docs/subsystems/item-claims.md.
					await _claims.HandleCoreClaim(client, body);

					break;
				}

				case IirTypes.QuestRewardSelectionIIR:
				{
					// The player picked one of the rewards a completing quest offered. The body names its own quest, so
					// QuestRewardService re-checks that this character actually completed it and has not already been
					// rewarded - the packet is not evidence of either.
					var pick = QuestRewardSelectionCodec.Parse(body.ToArray());
					await _questRewards.TryGrant(client, pick.QuestId, pick.Index);

					break;
				}

				case IirTypes.FeatIIR:
				{
					// Feat train: state 0x04 [feat ids] -> add to CharacterFeat -> s2c FeatIIR state 0x05 ack. See
					// Feats/FeatService.cs and docs/subsystems/feats.md.
					await _feats.HandleFeatTrain(client, rawPayload);

					break;
				}

				case IirTypes.SkillIIR:
				{
					// Skillpoint spend: a (statId, delta) list moving points out of the SkillPoints pool into a skill.
					// Answered with the two ordinary StatIIR records retail sends - see Progression/SkillPointService.cs.
					await _skillPoints.HandleAdjustSkill(client, SkillCodec.Parse(rawPayload.AsSpan(sizeof(uint))));

					break;
				}

				case IirTypes.TradeskillRequestIIR:
				{
					// The client opened the Crafting -> Recipes book and is asking for its recipe list. Answer with
					// the TradeskillReplyIIR (0x9062D559) catalog from the DB so the book populates. See
					// TradeskillRecipeCodec / docs/subsystems/tradeskill-recipes.md.
					//
					// The request carries a handle, an env byte and a version, and nothing else - parsed rather than
					// discarded so a request that ever did carry a recipe id would fail loudly here.
					var tradeskill = TradeskillRequestCodec.Parse(rawPayload.AsSpan(sizeof(uint)));

					Logger.LogDebug("TradeskillRequestIIR from handle 0x{Handle:X8} (env {Env}) - sending the recipe book.", tradeskill.Handle, tradeskill.Env);

					await _tradeskill.SendCraftingRecipeBook(client);

					break;
				}

				case IirTypes.UpdateInventoryIIR:
				{
					// c2s UpdateInventoryIIR is a probe, not a request that wants an answer: as an entity streams in
					// the client fires a pair asking for that entity's Equipped(101) then Bag(104) container -
					//   [handle][env 00][u32 pbLen][ProtoInventoryGame { 1: { 1: kind, 2: handle } }]
					// where the protobuf's inner id is the carrier handle again. It rides the same streaming burst as
					// UpdateQuestMarkerNPC + RequestAggroStatus on that handle.
					// Retail never answers it: across all 104 captures there are 34 c2s and 2 s2c, and the 2 s2c are an
					// unsolicited push of the player's own containers after an equip change (a RemoveItem, in
					// Hoxxling_mage-heraldofxotli_login_gameplay2 frame 5118) - zero of the 34 requests draw a reply.
					// Visible gear already arrives with the spawn snapshot, so drop it quietly like the teleport acks
					// below; answering would diverge from capture. The s2c push itself is unimplemented (see
					// UpdateInventory, currently emitted nowhere).

					Logger.LogDebug("GameServer 0x2000 inventory probe");

					break;
				}

				// After the player is relocated on resurrect (IntendedTeleport + n3Teleport) the client echoes the
				// teleport records back. Benign acks with no reply owed - handled here purely so they do not reach the
				// default branch's "Unknown 0x2000 Opcode" system message.
				case IirTypes.N3TeleportIIR:
				case IirTypes.IntendedTeleportIIR:
				{
					Logger.LogDebug("GameServer 0x2000 client teleport-ack {SubOpcodeHex} ({RawLength} bytes).", iirType.ToHex(), rawPayload.Length);
					break;
				}

				// The client reports how much shadow it is standing in - only it can compute that, since it owns the
				// lighting and the geometry, and its own `GameCode/Hiding` channel logs the same number as
				// `ShadowCoverage = <n>`. Retail echoes the record straight back with the env byte cleared, which is
				// what both captured exchanges show (client env 0x01 -> server env 0x00), so that is what we do.
				// Nothing consumes the value yet; stealth detection is where it will.
				case IirTypes.SetShadowCoverageIIR:
				{
					var shadow = SetShadowCoverageCodec.Parse(rawPayload);
					Logger.LogDebug("GameServer shadow coverage {Percent}% for handle {Handle}.", shadow.Percent, shadow.Handle);
					await WorldIir.Send(client, SetShadowCoverageCodec.Build(shadow.Handle, shadow.Percent));
					// Remembered for the hide stance: retail carries this number in the hide-enter behaviour's
					// tail, and the client reports it before pressing hide, so the two are separate arrivals.
					if (_simulation.PlayerWorldEntity(client) is { } shadowed)
						shadowed.ShadowCoverage = shadow.Percent;
					break;
				}

				// A GM command in the client sends FanaticCommandIIR ("Fanatic" is the client's own name for the GM
				// subsystem). What it asked for is readable, but the reply is not decoded - no corpus capture contains
				// one - so the parsed command is reported rather than answered with an invented record.
				case IirTypes.FanaticCommandIIR:
				{
					// The codec's Parse is strict; on an undecoded inbound path that means reporting the failure to the GM
					// rather than acting on a half-read record. `rawPayload` is the framed record, so the body starts past
					// its u32 length.
					FanaticCommandIIR f;
					try
					{
						f = FanaticCommandCodec.Parse(rawPayload.AsSpan(4));
					}
					catch (CodecParseException e)
					{
						Logger.LogWarning(e, "GameServer FanaticCommandIIR did not parse ({RawLength} bytes) raw={Raw}", rawPayload.Length, Convert.ToHexString(rawPayload));
						await _agentServerDispatcher.SendSystemMessage(client.Session.Character!.Id, // a GM command only arrives from a character in the world
							$"GM command received but the FanaticCommandIIR body did not parse ({rawPayload.Length} bytes).");
						break;
					}

					// The raw body rides along: the c2s grammar has no capture behind it (the corpus holds no GM
					// session), so the bytes are the only way to check a parse against reality.
					Logger.LogInformation("GameServer FanaticCommandIIR: text='{Text}' word0={Word0} targets=({TargetA:X8},{TargetB:X8}) ({RawLength} bytes) raw={Raw}",
						f.Text, f.Word0, f.TargetA, f.TargetB, rawPayload.Length, Convert.ToHexString(rawPayload));

					// The client's own GM commands run through the same table as the `.` ones: the command is entirely in
					// the text (`word0` is a constant 2) and arrives as "/verb args", the shape the GM command table
					// parses, so `/teleport` reaches the same implementation as `.teleport`.
					//
					// Still gated server-side on the account: the client only offers these when gm_level is non-zero, but
					// that is a value the server sends it, so it cannot be the authority on who may run them. The gate is
					// the account's GM state, not its WebAccountType - that governs the admin site and nothing here.
					if (!client.Session.Account!.IsGm)
					{
						Logger.LogWarning("FanaticCommandIIR '{Text}' from non-GM account {Account} ignored.", f.Text, client.Session.Account.Id);
						break;
					}

					if (f.Text.Length > 1 && f.Text[0] == '/')
						await _gmCommands.Handle(client, f.Text);
					else
						await _agentServerDispatcher.SendSystemMessage(client.Session.Character!.Id,
							$"GM command received with no verb ('{f.Text}').");
					break;
				}

				default:
				{
					var data = body.ReadRemainingArray();

					// The one sub-opcode that cannot be a case label: in-world the client streams 0x2000 packets whose
					// sub-opcode is its own instance id (the client-instance ack/keepalive stream). That value is the live
					// character id, so it can only be matched here. Treat it as a benign ping: log quietly rather than
					// answering with a system message.
					if (client.Session.Character is { } self && (uint)iirType == self.Id)
					{
						Logger.LogDebug("GameServer 0x2000 client-instance ping {SubOpcodeHex} ({RawLength} bytes).", iirType.ToHex(), rawPayload.Length);
						break;
					}

					Logger.LogDebug("Unhandled GameServer 0x2000 subopcode {SubOpcodeHex} raw payload: {RawPayload}", iirType.ToHex(), rawPayload.ToHex());

					await _agentServerDispatcher.SendSystemMessage(client.Session.Character!.Id, $"Unknown 0x2000 Opcode: {iirType.ToHex()} ({rawPayload.Length} bytes)"); // a 0x2000 only arrives from a character in the world

					break;
				}
			}
		}

		/// <summary>
		///     GameAgent (connection-class) dispatch: auth, chat, ping and server stats. Routed here by receiver class.
		/// </summary>
		private async Task HandleGameAgentPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var method = (GameAgentMethods)packet.Method;
			if (method is not GameAgentMethods.UpdateGameServerStats)
				LogPacketReceived(packet);

			switch (method)
			{
				case GameAgentMethods.Authenticate2:
				{
					if (!await _login.HandleAuthenticate2(client, body))
						return;

					break;
				}

				case GameAgentMethods.PassBlob:
				{
					await HandleIirCarrier(client, packet, body);

					break;
				}

				case GameAgentMethods.UpdateGameServerStats: // client 0x2005 (empty) -> server Pong 0x2009
				{
					await _keepAlive.HandlePing(client);

					break;
				}

				case GameAgentMethods.ChatVicinity:
				{
					// 0x2002 = GameAgent.ChatVicinity (typed chat, and the carrier for GM commands). The
					// GameCharAgent.RequestBindLocation collision on this opcode is resolved by the class-first route.
					LogPacketBodyDebug("GameServer ChatCommand body", body.ToArray());

					// GameAgent.ChatVicinity(const ID32& cSenderID, int nMode, BinaryStream& cMsg) - the client's own
					// signature (docs/re/aoc-client-rpc-methods.json), matched against the corpus:
					//   [u32 senderClass][u32 senderHandle]   the ID32; usually {0,0} (the client leaves the sender for
					//                                         the server to fill), occasionally {0xC350, ownHandle}
					//   [u32 nMode]                           chat mode; 0 for plain /say in every captured packet
					//   [u32 msgLen][ string text ][u8 0]     the BinaryStream, u32-length-framed, holding the wire
					//                                         string plus a trailing 0 - so msgLen == 2 + text + 1
					var senderClass = body.ReadUInt32();
					var senderHandle = body.ReadUInt32();
					var mode = body.ReadUInt32();
					var messageLength = body.ReadUInt32();
					var text = body.ReadString();

					Logger.LogDebug("ChatVicinity from ID32 {{{SenderClass:X}, {SenderHandle:X8}}}, mode {Mode}, {Length} message bytes.", senderClass, senderHandle, mode, messageLength);

					if (text.Length > 2 && text[0] == '.' && text[1] != '.' && client.Session.Account.IsGm)
						await _gmCommands.Handle(client, text);
					else
						// "say" is proximity chat - only same-map players within range hear it (the sender always does).
						// The GameServer owns the live positions, so it picks the recipients here.
						await _agentServerDispatcher.CharacterSay(client.Session.Character.Id, text, _simulation.SayRecipients(client));

					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		/// <summary>
		///     GameCharAgent dispatch: the 0x2002/0x2003 collision forms (bind and tutorial) plus every per-character
		///     method (loot, shop, respawn, shortcut, latency, ...). Routed here by receiver class from ReceivedPacket.
		/// </summary>
		private async Task HandleGameCharAgentPacket(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var method = (GameCharAgentMethods)packet.Method;
			if (method is not GameCharAgentMethods.Ping)
				LogPacketReceived(packet);

			switch (method)
			{
				// Player trade. The request itself arrives as a BCC (UseCharacter 0x16 at a player), handled elsewhere;
				// everything from the window opening onward is these five methods.
				case GameCharAgentMethods.StartTrade: // 0x2065 - the TARGET opens the window, creating the session
				{
					await _trade.Start(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				case GameCharAgentMethods.SetTradeCash: // 0x2067
				{
					await _trade.SetCash(client.Session.Character.Id, TradeCashCodec.Parse(body.ReadRemainingArray()).Tin);
					break;
				}

				// 0x2068 - the same method index as the s2c DispatchRequestTrade, on the other interface.
				case GameCharAgentMethods.AddTradeItem:
				{
					var request = AddTradeItemCodec.Parse(body.ReadRemainingArray());
					await _trade.AddItem(client.Session.Character.Id, request.ItemPos, request.DestPos);
					break;
				}

				case GameCharAgentMethods.AcceptTrade: // 0x206B, empty
				{
					await _trade.Accept(client.Session.Character.Id);
					break;
				}

				// 0x2069 RemoveTradeItem([int destPos]) - take an item back off the table.
				case GameCharAgentMethods.RemoveTradeItem:
				{
					await _trade.RemoveItem(client.Session.Character.Id, body.ReadUInt32());
					break;
				}

				// 0x206C AbortTrade (empty) and 0x2066 DeclineTrade([ID32]) - the cancel, and refusing the request before
				// the window opens. Both end the session; neither has moved anything, since the exchange only happens on
				// the double accept. What the server owes the partner to close their window is not decoded.
				// Open work tracked in Gitea #427.
				case GameCharAgentMethods.AbortTrade:
				{
					_trade.Abandon(client.Session.Character.Id);
					break;
				}

				case GameCharAgentMethods.DeclineTrade:
				{
					EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)); // the requester, echoed by the client
					_trade.Abandon(client.Session.Character.Id);
					break;
				}

				// 0x2011 GuildTransferMoney([u64 guildInstance][u64 amount]): a member pays money into the guild bank.
				// It arrives on this tier rather than the CS tier because the wallet lives here; `Guild.Cash` lives on
				// the CS tier, so the guild is credited across the dispatcher seam.
				//
				// The wallet is debited first and refunded if the credit fails, which is the same pessimistic order the
				// withdrawal uses in the other direction: whoever holds the money moves it first, so a failure between
				// the two steps loses the transfer rather than duplicating it.
				case GameCharAgentMethods.GuildTransferMoney:
				{
					await _guildBank.HandleGuildDeposit(client, GuildTransferMoneyCodec.Parse(body.ReadRemainingArray()));
					break;
				}

				// 0x2008 LeaveTeam (no body): a different interface's method 0x2008 than the CS tier's AcceptTeamInvite -
				// method indices are per interface, so the number collision is expected and must not be unified.
				case GameCharAgentMethods.LeaveTeam:
				{
					await _csPlayerAgentDispatcher.LeaveTeam(client.Session.Character.Id);
					break;
				}

				// 0x2009 KickFromTeam([ID32 target]): the leader removes a member. TeamService checks the leadership.
				case GameCharAgentMethods.KickFromTeam:
				{
					await _csPlayerAgentDispatcher.KickFromTeam(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				// 0x2091 RequestTeamInvite([member record]): the invited client resolves the inviter's details, the way
				// ClientRequestGuildInfo resolves a guild banner. Answer with that character's record.
				case GameCharAgentMethods.RequestTeamInvite:
				{
					var subject = TeamInviteCodec.Parse(body.ReadRemainingArray()).Member;

					if (await Database.GetCharacter(subject.CharacterId) is { } character)
						// Two things about the header, both of which cost an invite window when missed:
						//
						// The sender is the GameCharAgent object (f683: class GameCharAgent, instance 0x10), not the
						// world sender this file usually replies from - a reply belongs to the interface pair its
						// request arrived on.
						//
						// And it carries the trailing headerData 0x01, as KeepAliveService's DispatchPong on this same
						// interface does. Retail puts it on this reply (f682 and f683 both carry it) while the CS-tier
						// invite dispatches carry none; without it the client discards the reply and never raises the
						// invite window, whatever the body says.
						await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType),
							GameCharInterfaceMethods.DispatchReplyTeamInvite,
							ReplyTeamInviteCodec.Build(TeamMemberRecord.FromCharacter(character, GetCharacterLiveState(character.Id)?.MapId ?? character.MapId))));

					break;
				}

				case GameCharAgentMethods.RequestBindLocation: // 0x2002 = RequestBindLocation
				{
					await HandleRequestBindLocation(client, packet, body);
					return;
				}

				// 0x2003 SetTutorialStats(int nValue): the client naming a Tortage tutorial hint it has just shown.
				//
				// The value is a hint id, not a progress ordinal, and the case must stay filtered to the GameCharAgent
				// receiver class - 0x2003 collides with a GameAgent method that carries a character id. The captures
				// give 22 distinct values, all in 0..47, and they are not monotone: each session reports only the hints
				// newly seen in it (one character sends {45,15,16,46}, then {29,30,31,13}, then {10,28} across three
				// logins), so the client keeps its own cumulative set and sends the deltas.
				//
				// They accumulate into Character.TutorialStepsSeen, a bitmask. No reply: the client's method catalog
				// has no s2c counterpart, so this is a one-way notification - which is also why storing it cannot on
				// its own restore the hints on a fresh client. See the column's comment.
				case GameCharAgentMethods.SetTutorialStats:
				{
					var tutorialStep = body.ReadUInt32();
					var character = client.Session.Character;

					if (tutorialStep >= 64)
					{
						// Every observed id is in 0..47. A wider one would silently shift out of the mask, so it is
						// reported rather than dropped: it means the id space is bigger than the captures showed.
						Logger.LogWarning("SetTutorialStats id {Step} is outside the 0..63 the bitmask holds; not recorded (#461).", tutorialStep);
						return;
					}

					var updated = character.TutorialStepsSeen | (1UL << (int)tutorialStep);
					if (updated != character.TutorialStepsSeen)
					{
						character.TutorialStepsSeen = updated;
						await Database.SetCharacterTutorialSteps(character.Id, updated);
					}

					Logger.LogDebug("SetTutorialStats hint {Step} seen by {Character} (mask now 0x{Mask:X16}).", tutorialStep, character.Name, updated);
					return;
				}

				// Each opcode below takes its grammar from the client's own signature
				// (docs/re/aoc-client-rpc-methods.json), confirmed against the corpus.

				// 0x2001 RequestCharName(ID32::InstanceType nCharID): resolve a bare character instance (no class word -
				// that is what `ID32::InstanceType` means, and the captured body is 4 bytes) to a display name. Reply is
				// DispatchCharacterNameReply(i_cCharID, i_cName); the `i_c` prefix makes cCharID a full ID32, so it goes
				// back out as [class][instance]. No capture of the reply - it is built from the signature and still
				// needs in-client verification. Silence for an unknown id, rather than a placeholder name that would
				// look real.
				case GameCharAgentMethods.RequestCharName:
				{
					var subjectId = body.ReadUInt32();

					if (await Database.GetCharacter(subjectId) is { Name: { } name })
						await client.SendAsync(new Rpc(Endpoints.Endpoint(RpcInterfaces.GameCharAgentType, 0x10), client.Endpoint(RpcInterfaces.GameCharInterfaceType), GameCharInterfaceMethods.DispatchCharacterNameReply,
							CharacterNameReplyCodec.Build(new CharacterNameReply(subjectId, name))));
					else
						Logger.LogWarning("RequestCharName for unknown character {CharacterId:X8}; no reply.", subjectId);

					break;
				}

				// The player accepted the ACG instance bind box. There is no decline - sending this is the yes - so the
				// only thing to check is that the box was actually offered. The body carries the destination proxy and
				// nothing else: no character, no door, so without the pending offer any client could name any map.
				case GameCharAgentMethods.RequestACGTeleport:
				{
					var request = AcgTeleportRequestCodec.Parse(body.ReadRemainingArray());
					var offered = client.Session.PendingAcgTeleport;
					client.Session.PendingAcgTeleport = null;

					if (offered == null || offered.DestinationMapId != request.MapId)
					{
						Logger.LogInformation("RequestACGTeleport for map {MapId} from character {CharacterId} - matches no outstanding bind box ({Offered}).",
							request.MapId, client.Session.Character.Id, offered?.DestinationMapId.ToString() ?? "none");

						break;
					}

					Logger.LogInformation("Character {CharacterId} accepted the instance bind for map {MapId}.", client.Session.Character.Id, request.MapId);
					await _teleport.DoTeleport(client.Session.Character.Id, offered.DestinationMapId, offered.ArrivalPosition);

					break;
				}

				// 0x203B ScryMessageBoxAnswer(int nAnswer, int nScriptThreadCtxId, uint64 nCtxCookie): the player
				// answered a scry-script message box. Capture-verified as a pure echo pair - in all nine captures the
				// answer repeats the DispatchScryShowMessageBox (0x205E) context word-for-word and adds the button
				// index, e.g. show `...0024f5f7 0000 000000005a4db7a0` -> answer `00000001 0024f5f7 000000005a4db7a0`.
				// Every captured answer is 1 (confirm); no capture declines one.
				//
				// This is not the local-travel confirmation. The captured box that reads "Do you want to travel to
				// Ymir's Pass?" belongs to the zone-line door the player selects, not to the wagoneer they rode half a
				// minute earlier - a wagoner trip teleports straight off the destination pick with no box at all. The
				// only box raised here is the zone-line one, and the echo is what resumes its trip: no server-side
				// script thread is involved, here or in retail.
				case GameCharAgentMethods.ScryMessageBoxAnswer:
				{
					var reply = ScryMessageBoxAnswerCodec.Parse(body.ReadRemainingArray());
					var pending = client.Session.PendingTravelConfirm;
					client.Session.PendingTravelConfirm = null;

					if (pending == null || reply.ThreadCtxId != pending.ThreadCtxId || reply.Cookie != pending.Cookie)
					{
						Logger.LogInformation("ScryMessageBoxAnswer {Answer} for script thread 0x{Thread:X8} (cookie 0x{Cookie:X16}) - matches no outstanding box.",
							reply.Answer, reply.ThreadCtxId, reply.Cookie);

						break;
					}

					// Zero is the decline, not "anything but 1": `Answer` is the option index. The corpus carries a
					// `2` on the same box a `1` appears on, and it is a confirm of the second button - retail's
					// difficulty picker, "energed normal instance" against "enered epic instance", both followed by
					// the redirect that enters the zone. Every non-zero option therefore means the trip is accepted.
					if (reply.Answer == ScryMessageBoxAnswerCodec.Decline)
					{
						Logger.LogInformation("Character {CharacterId} declined the trip to map {MapId}.", client.Session.Character.Id, pending.DestinationMapId);

						break;
					}

					// The second button of the client's own difficulty picker is epic - the client renders the
					// normal/epic buttons itself on a destination that has an epic mode (the captured Spider Caverns
					// shows are byte-identical in shape to every plain travel box), so honouring the index is all the
					// server does. Any option past 2 still travels as normal, logged so an unmodelled picker shows up.
					var epic = reply.Answer == ScryMessageBoxAnswerCodec.ConfirmSecondOption;
					if (reply.Answer != ScryMessageBoxAnswerCodec.Confirm)
						Logger.LogInformation("ScryMessageBoxAnswer chose option {Answer} for the trip to map {MapId}{Epic}.", reply.Answer,
							pending.DestinationMapId, epic ? " - the EPIC difficulty" : " - we offer one option, so it travels as normal");

					await _teleport.DoTeleport(client.Session.Character.Id, pending.DestinationMapId, pending.ArrivalPosition, epic);

					break;
				}

				// 0x204D SetPassOnLootLevel(int i_Level): the "automatically pass on loot below this quality" setting.
				// Captured values are -1 (off) and 3. Session-scoped: the server does not arbitrate loot rolls yet, so
				// persisting it would store a setting nothing consults.
				case GameCharAgentMethods.SetPassOnLootLevel:
				{
					var passOnLootLevel = unchecked((int)body.ReadUInt32());

					Logger.LogInformation("SetPassOnLootLevel {Level} ({State}) - held for this session only.", passOnLootLevel, passOnLootLevel < 0 ? "off" : "on");

					break;
				}

				// 0x204F ShareQuest([ID32 quest]): offer a quest I hold to my team. The ID32's instance is the quest id;
				// its class word is the client's own and is not a character class, so only the instance is used.
				case GameCharAgentMethods.ShareQuest:
				{
					await _questSharing.Share(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				// 0x2050 ShareQuestDeclined / 0x2052 ShareQuestBusy ([ID32 requester]): the target pressed No, or their
				// client would not raise the window. The body names the requester, but who gets told is taken from our
				// own pending map - the same rule the team accept and the trade start follow - so the echo is read only
				// to keep the body accounted for.
				case GameCharAgentMethods.ShareQuestDeclined:
				case GameCharAgentMethods.ShareQuestBusy:
				{
					EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length));
					await _questSharing.Decline(client.Session.Character.Id, method == GameCharAgentMethods.ShareQuestBusy);
					break;
				}

				// 0x2051 ShareQuestAccepted(InstanceID_t): the target pressed Yes. `InstanceID_t` is one u32 on the wire
				// (the client's proxy byte-swaps a single dword out of it), and the number is the `i_nStartTaskTemplate`
				// we sent in DispatchQuestShareRequest, echoed back.
				case GameCharAgentMethods.ShareQuestAccepted:
				{
					await _questSharing.Accept(client.Session.Character.Id, body.ReadUInt32());
					break;
				}

				// 0x2054 RequestTeamQuestList(): no arguments (the captured body is empty). Retail's answer when there
				// is no list to send is not the list method - it is DispatchCancelTeamQuestList (0x204C) carrying
				// share code 2, twice in `login_2-clients_group-trade-friend` (frames 1978->1979, 2851->2853), the
				// only two occurrences of 0x204C in the corpus and one per request. We model no shared team quests,
				// so that is always our answer too - replayed byte-exact rather than left silent.
				case GameCharAgentMethods.RequestTeamQuestList:
				{
					await QuestShareOutbound.SendQuestShareRpc(client, GameCharInterfaceMethods.DispatchCancelTeamQuestList,
						CancelTeamQuestListCodec.Build(CancelTeamQuestListCodec.RetailNoList));

					break;
				}

				// 0x208D RequestMassivePvPScoreData(): no arguments, and retail answers nothing - all six occurrences
				// across four captures are followed only by the client's other login polls, never by a MassivePvPScore
				// dispatch. So silence here is retail's own behaviour for a character outside a Massive-PvP battle,
				// not a gap. Consume so it is not logged as unknown.
				case GameCharAgentMethods.RequestMassivePvPScoreData:
				{
					Logger.LogDebug("{Method}: retail sends no reply outside a Massive-PvP battle either; no reply.", method);

					break;
				}

				// 0x2092 RequestNewJoinHasHostileInFriends(const GroupElement_c& i_cWho): before a team join, the
				// client asks whether the joiner has a hostile on their friends list. `GroupElement_c` is the bare
				// member record and nothing else - unlike RequestTeamInvite's `(i_cWho, i_cMessage)` the signature
				// carries no message, so it must be read with TeamMemberRecordCodec: a codec that demands a trailing
				// message throws on a record that fills the body, and the parse exception drops the connection.
				//
				// The reply DispatchReplyNewJoinHasHostileInFriends carries only the subject and no boolean, i.e.
				// sending it is the "yes" - so silence is the "no", and the only answer available here: no hostility
				// between players is modelled.
				case GameCharAgentMethods.RequestNewJoinHasHostileInFriends:
				{
					var who = TeamMemberRecordCodec.Parse(body.ReadRemainingArray());

					Logger.LogDebug("RequestNewJoinHasHostileInFriends for {Name} ({CharacterId:X8}) - no hostility modelled, so no reply.", who.Name, who.CharacterId);

					break;
				}

				// 0x207E SetHideInspectOption(bool): the "hide my gear from inspect" privacy toggle. One byte.
				case GameCharAgentMethods.SetHideInspectOption:
				{
					var hideFromInspect = body.ReadByte() != 0;

					Logger.LogInformation("SetHideInspectOption {Hide}; not persisted - inspect is not implemented.", hideFromInspect);

					break;
				}

				case GameCharAgentMethods.CinematicTransition:
				{
					// GameCharAgent.CinematicTransition(const ID32& i_cClientId, const bool i_bEntering) - body is
					// [u32 0xC350][u32 handle][u8 bEntering], that exact 9-byte shape in all 402 captured packets
					// (255x bEntering=0, 147x bEntering=1). The routing below is on EntryMode, not on the bool - the
					// login flow uses this opcode as a ready trigger regardless of direction - but the bool IS the
					// enter-vs-leave discriminator if a branch ever needs one.
					body.ReadUInt32(); // EntityClass.Character (0xC350)
					var characterId = body.ReadUInt32();
					var entering = body.ReadByte() != 0;

					// An in-world achievement vista is the one case where this opcode really is what its name says -
					// the client bracketing a cinematic it is running itself - and the bool is the whole discriminator.
					// Handled first, and it consumes the packet: a mid-session cinematic must not also re-arm the login
					// handshake's follow-up.
					if (client.Session.ActiveVista is { } scene)
					{
						if (entering)
							await Scripting.VistaService.OnVistaCinematicEntered(client, scene);
						else
							await _vista.OnVistaCinematicLeft(client, scene);

						break;
					}

					// 0x205F is the cinematic transition (its trailing byte is begin/end); the login flow happens to use it
					// as its ready trigger, which makes "should this arm the follow-up?" a per-mode question rather than a
					// property of the packet.
					switch (client.Session.EntryMode)
					{
						case WorldEntryMode.CreateWorld:
						{
							// This connection is running an actual cinematic - the beach/Kalanthes intro - so it sends a
							// stream of these. Arming here would let the next 0x2020 poll fire ReadyForPlayScreen into the
							// middle of the cutscene; its ready sequence is the create burst's own.
							Logger.LogInformation("0x205F (cinematic transition) on the create-world connection: the intro drives its own ready sequence; not arming the login follow-up.");
							break;
						}

						case WorldEntryMode.CreateEditor:
						{
							// Retail sends no download in response - it acks the polls and waits for CharInPlayIIR
							// (which triggers the editor-launch burst).
							client.Session.HasPendingPost205FFollowUp = true;
							Logger.LogInformation("0x205F decoded: characterId={CharacterId:X8}, entering={Entering}. Create-editor phase; awaiting CharInPlayIIR.", characterId, entering);
							break;
						}

						case WorldEntryMode.Login:
						case WorldEntryMode.TeleportReentry:
						{
							client.Session.HasPendingPost205FFollowUp = true;
							Logger.LogInformation("0x205F decoded: characterId={CharacterId:X8}, entering={Entering}, expectedCharacterId={ExpectedCharacterId:X8}. Marked post-0x205F follow-up as pending.", characterId,
								entering, client.Session.Character.Id);
							break;
						}

						default:
						{
							Logger.LogError("0x205F on an unhandled entry mode {Mode}: not arming the ready follow-up, so this client may sit on its loading screen.", client.Session.EntryMode);
							break;
						}
					}

					break;
				}

				case GameCharAgentMethods.UpdateQuestMarkerNPC:
				{
					// Wire 0x2020 on the GameCharAgent class is method-index slot 0x20, whose RPC catalog label is
					// UpdateQuestMarkerNPC - but the world-entry handshake reuses it as the client's spawn poll and ready
					// trigger. Owned by WorldEntryService.HandleWorldEntrySpawnPoll, not decoded inline here.
					await _worldEntry.HandleWorldEntrySpawnPoll(client, packet, body);
					break;
				}

				case GameCharAgentMethods.DoSkillSellback:
				{
					// The inverse of a skillpoint spend, and the half of the feature that stays unimplemented: there is
					// no sellback traffic in the corpus and `DoSkillSellback(int i_eStat)` carries no amount, so the
					// refund is unrecoverable. Refunding everything would let a client cash out the base value every
					// character starts with. Logged until a capture settles it.
					LogPacketBodyDebug("GameServer 0x205C body", body.ToArray());

					break;
				}

				case GameCharAgentMethods.PlayStockphraseAsSubtitle:
				{
					// Create-cutscene speech: the client is asking the server to resolve a stock phrase ({stock_N})
					// and blocks up to 60 s for the 0x205D reply, so it must be answered (Kalanthes's beach lines)
					// rather than left to time out. See Dialogue/StockConversationService.cs.
					await _stockConversation.HandlePlayStockphraseAsSubtitle(client, body.ToArray());

					break;
				}

				case GameCharAgentMethods.RequestShortcutBar: // the client asks for its saved hotbar at login; reply with the restored bar
				{
					await _shortcutBar.SendRestoredBar(client);

					break;
				}

				case GameCharAgentMethods.SellItem:
				{
					await _shop.HandleSellItemToShop(client, packet, body);

					break;
				}

				case GameCharAgentMethods.UndoLastSoldItem:
				{
					await _shop.HandleUndoLastSoldItem(client);

					break;
				}

				case GameCharAgentMethods.BuyItem:
				{
					body.ReadUInt32(); // shop ID32 type (0xC75B)
					var shopId = body.ReadUInt32();
					var pos = body.ReadUInt32();

					// Resolve the clicked slot against the open shop, debit the wallet and grant the item live
					// (DispatchAddItem).
					await _shop.HandleBuyItemFromShop(client, shopId, pos);

					break;
				}

				// 0x2032 CloseShop(const ID32& i_cShopID): the player closed the vendor window. The ID32 is the vendor
				// (class 0xC350 - a shop's owner is a character, not the 0xC75B shop-content class), but we close
				// whatever shop this session has open, so it is read for the log only.
				case GameCharAgentMethods.CloseShop:
				{
					body.ReadUInt32(); // EntityClass.Character (0xC350)
					var vendorHandle = body.ReadUInt32();

					Logger.LogInformation("Shop {Vendor:X8} closed by {Character}.", vendorHandle, client.Session.Character?.Name);

					// Retail acks the close with `DispatchAbortTrade` (0x6A), zero-length, on 152 of the 153 frames the
					// corpus holds.
					await GameWire.SendTradeAbort(client);

					break;
				}

				case GameCharAgentMethods.TryLootAll: // the loot-all click on an open loot window; body = [typetag][bag handle]
				{
					await _loot.HandleLootAll(client, packet, body);

					break;
				}

				case GameCharAgentMethods.TryLootItem: // single drag of one item out of the loot window
				{
					await _loot.HandleLootItem(client, packet, body);

					break;
				}

				case GameCharAgentMethods.UntrainFeat: // "untrain all feats" -> send the confirm messagebox
				{
					await _feats.HandleFeatUntrainRequest(client);

					break;
				}

				case GameCharAgentMethods.FeatUntrainAnswer: // confirm-dialog answer -> reset if confirmed
				{
					await _feats.HandleFeatUntrainAnswer(client, packet, body);

					break;
				}

				case GameCharAgentMethods.CloseLootBag: // client closed the loot window; body = [typetag][bag handle]
				{
					await _loot.HandleCloseLootBag(client, packet, body);

					break;
				}

				case GameCharAgentMethods.TryLootCash: // loot the bag's coins; fires on every loot alongside TryLootAll
				{
					await _loot.HandleLootCash(client, packet, body);

					break;
				}

				// The two answers to DispatchResurrectRequest. The client holds the offer from the moment the
				// consumable is used and replies here when the player dies - which is why neither appears in the
				// corpus: the one captured session used its potion and never died.

				// 0x2022 ResurrectMe(uint32 resurrectSpellID): the offer is taken. The spell id is the token the server
				// sent, echoed back, and it must match - it is what distinguishes this offer from another one.
				case GameCharAgentMethods.ResurrectMe:
				{
					var resurrectSpell = body.Length >= sizeof(uint)
						? body.ReadUInt32()
						: 0;

					if (await _playerDeaths.ResurrectFromOffer(client, resurrectSpell))
						Logger.LogInformation("ResurrectMe (spell {Spell}): self-resurrection taken.", resurrectSpell);
					else
						// Not an error: a resurrect offered by another player arrives the same way and none are made
						// here, and a client that answers twice sends the second after the offer is gone.
						Logger.LogInformation("ResurrectMe (spell {Spell}) with no matching armed offer, or the player is not dead; ignored.", resurrectSpell);

					break;
				}

				// 0x2023 CancelResurrectionRequest(const ID32& resurrectRequesterID, uint32 resurrectSpellID): the
				// offer is declined. Both fields echo what the server sent; the drop keys on the spell, so a decline
				// cannot take away an offer it does not name.
				case GameCharAgentMethods.CancelResurrectionRequest:
				{
					var requester = EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length));
					var declinedSpell = body.Length >= sizeof(uint)
						? body.ReadUInt32()
						: 0;

					if (_playerDeaths.CancelSelfResurrection(client.Session.Character.Id, declinedSpell))
						Logger.LogInformation("CancelResurrectionRequest: {Character} declined the resurrection from {Requester:X8} (spell {Spell}).", client.Session.Character.Name, requester.Id,
							declinedSpell);
					else
						Logger.LogInformation("CancelResurrectionRequest (spell {Spell}) with no matching armed offer; ignored.", declinedSpell);

					break;
				}

				case GameCharAgentMethods.RespawnMe: // the player's release/resurrect click; body = [u32 token]
				{
					var token = body.Length >= sizeof(uint)
						? body.ReadUInt32()
						: 0;

					// Retail respawn is click-driven (vitals restore + tombstone + teleport + re-render at the
					// resurrection point); our simulation already implements that flow on a fallback timer - the
					// click just triggers it immediately.
					if (await _playerDeaths.TryRespawnNow(client))
						Logger.LogInformation("RespawnMe (token 0x{Token:X8}): player released; respawned.", token);
					else
						Logger.LogWarning("RespawnMe (token 0x{Token:X8}) from a player that is not dead; ignored.", token);

					break;
				}

				case GameCharAgentMethods.CancelBuff: // right-click-cancel a buff; body = [u32 buffId(base)][u32 charId]
				{
					var buffId = body.ReadUInt32();
					var charId = body.ReadUInt32();
					var self = client.Session.Character.Id;
					var entity = _simulation.PlayerWorldEntity(client);

					// The client sends the buff's base id (rank byte stripped, e.g. 0x0037F38C); our registry keys are
					// the applied wire id ((base<<8)|rank). Match by base so the removal twin carries the exact id.
					var appliedId = buffId << 8;
					if (entity != null)
					{
						foreach (var key in entity.ActiveBuffs.Keys)
						{
							if (key >> 8 == buffId)
							{
								appliedId = key;
								break;
							}
						}
					}

					// The client does not self-remove the icon on right-click (confirmed in-client), so the server has to
					// drive it. Retail's rank-1 end twin clears the icon live and is byte-exact against the captured
					// buff-end twin (NecroL80_..._castle-exit rec#735). There is no capture of the cancel reply itself, so
					// this is the in-client-proven behaviour rather than a replayed one.
					await _buffs.SendBuffRemoval(client, self, self, appliedId);

					if (entity != null && entity.ActiveBuffs.TryRemove(appliedId, out _))
					{
						await _buffs.SyncBuffStats(client); // drop whatever stats the cancelled buff was granting
						Logger.LogInformation("CancelBuff: buff 0x{Buff:X8} removed from {CharId:X8}.", appliedId, charId);
					}
					else
						Logger.LogDebug("CancelBuff: buff 0x{Buff:X8} was not tracked for {CharId:X8} (cast before the registry, or an aura).", appliedId, charId);

					// If the cancelled buff was the tracked Shield of the Risen, forget it so the next playfield entry
					// re-applies cleanly rather than trying to remove an id the client no longer shows.
					if (client.Session.ActiveShieldSpellBase == buffId)
						client.Session.ActiveShieldSpellBase = null;

					break;
				}

				case GameCharAgentMethods.Ping: // latency check (client 0x2085 -> server 0x2082)
				{
					await KeepAliveService.HandleLatencyCheck(client, packet, body);

					break;
				}

				// 0x206A MoveTradeItemInternal([int pos1][int pos2]): the player drags an offered item to another slot
				// of the trade window. Signature from the client's own catalog.
				case GameCharAgentMethods.MoveTradeItemInternal:
				{
					_trade.MoveItem(client.Session.Character.Id, body.ReadUInt32(), body.ReadUInt32());
					break;
				}

				case GameCharAgentMethods.GetInstanceListForCurrentPlayfield: // When the 'Select instance' button is clicked
				{
					// The click asks "which copies of this playfield may I pick between" with an empty body, and retail
					// answers DispatchInstanceSelectionForCurrentPlayfield: a plain-u32-counted list of PlayfieldProxy
					// records. Unanswered, the window opens on nothing.
					//
					// One copy of each playfield is hosted, so the answer is the instance the player is standing in -
					// which is also the only shape a capture pins: retail's single captured reply is a one-element list
					// naming the live instance of the player's current map, on the token the world-entry burst already
					// gave this client. A player not yet in a zone gets the empty list rather than a made-up one.
					var here = _simulation.PlayerWorldEntity(client);
					var selection = here != null
						? InstanceSelections.Current(here.MapId, _simulation.World.ZoneFor(here.MapId, here.InstanceId).PlayfieldToken)
						: InstanceSelections.None();

					await client.SendAsync(new Rpc(InstanceSelectionSender, client.Endpoint(RpcInterfaces.GameCharInterfaceType),
						GameCharInterfaceMethods.DispatchInstanceSelectionForCurrentPlayfield, InstanceSelectionCodec.Build(selection)));

					break;
				}

				case GameCharAgentMethods.PerformSpecialAction:
				{
					// 0x205A PerformSpecialAction is a generic action opcode multiplexed by its body word, not a
					// spellweave-only toggle. The selector catalog is docs/subsystems/player-actions.md; the ones routed
					// here are the spellweave toggle (0xFC necromancer / 0x10C demonologist - per class, see
					// StartingData.SpellweaveToggleAction) and the Path of Asura recall. 0xFF opens the Crafting ->
					// Recipes book and 0x6C/0x6D/0x70/0xF0 are melee combo inputs, so the selector must be matched
					// exactly: routing every 0x205A to spellweave starts a weave on the recipe book.
					var action = body.ReadUInt32();

					Logger.LogInformation("0x205A (PerformSpecialAction) param={Action:X4}", action);

					// The spellweave selector is the class's own, so a demonologist's 0x10C opens a trance while a
					// necromancer's 0x10C does not (and vice-versa for 0xFC) - matching retail, where each class only
					// ever sends its own id.
					if (StartingData.SpellweaveToggleAction.TryGetValue(client.Session.Character.Class, out var weaveAction) && action == weaveAction)
					{
						await _spellweave.ToggleAsync(client, action);
						break;
					}

					switch (action)
					{

						// Path of Asura: a recall to the bound location, which is not the Totem of Origins destination
						// (that returns to the racial home city). The client double-sends the action (capture F303+F307),
						// so the gate on IsTeleportingOut is required - the first recall arms the handoff, the duplicate
						// is absorbed.
						case PathOfAsuraSpecialAction when !client.Session.IsTeleportingOut:
						{
							await _teleport.RecallToBindCommand(client);
							break;
						}

						// The search stance. Retail answers with the player's own BehaviorControlCenterIIR at slot
						// BehaviorId.Search - the client waits for it before the stance visibly takes, so the press
						// does nothing at all without it. There is no server-side search state: the record is the
						// whole of retail's observed answer.
						case SearchSpecialAction:
						{
							await _stances.PerformSearchStance(client);
							break;
						}

						// The hide stance - a toggle, like the mount.
						case SneakHideSpecialAction:
						{
							await _stances.ToggleSneakHide(client);
							break;
						}

						// Rest - not a stance record at all, a self-cast of the `Resting` spell.
						case RestSpecialAction:
						{
							await _stances.PerformRestCast(client);
							break;
						}

						// The combat toggle. Retail answers with a `Transition` record and the rating quad; only the
						// quad is sent, because the record's animation action is per weapon and none of the five
						// captured ids is an RDB record, a spell or an ability - a per-class map would be a borrowed id
						// playing the wrong sheathe. A subset of retail's answer is preferred to an invented record.
						case CombatToggleSpecialAction:
						{
							await GameWire.SendCombatRatings(client);
							break;
						}
					}

					break;
				}

				case GameCharAgentMethods.StackInventoryItemIfPossible:
				{
					LogPacketBodyDebug("GameServer StackInventoryItemIfPossible body", body.ToArray());

					break;
				}

				case GameCharAgentMethods.SplitInventoryItem:
				{
					// GameCharAgent.SplitInventoryItem(const ID32& i_cInventory, int i_nSrcPos, int i_nSplitSize,
					// int i_nDstPos) - the client's own signature. The leading ID32 is the container being split in:
					// {type = the container kind (0x68 backpack, same value as CharacterInventory.Container), instance =
					// the owning character's handle}. Both captured splits are backpack-internal, which is why
					// PersistSplitStack assumes the bag.
					var containerKind = body.ReadUInt32();
					var containerOwner = body.ReadUInt32();
					var sourceSlot = body.ReadUInt32();
					var targetStackSize = body.ReadUInt32(); // i_nSplitSize - how many units move to the new stack
					var targetSlot = body.ReadUInt32();

					Logger.LogDebug("SplitInventoryItem in container {{{Kind:X2}, {Owner:X8}}}: slot {Source} -> {Target}, {Size} units.", containerKind, containerOwner, sourceSlot, targetSlot, targetStackSize);

					// Persist the split so both stacks survive logout (bag container; the packet carries no kind).
					await _equipment.PersistSplitStack(client, (int)sourceSlot, (int)targetSlot, targetStackSize);

					break;
				}

				case GameCharAgentMethods.RemoveShortcut: // body = [u32 slot]
				{
					await _shortcutBar.HandleRemoveShortcut(client, packet, body);

					break;
				}

				case GameCharAgentMethods.UpdateShortcut: // body = [u32 slot][u32 flag][u32 container]
				{
					await _shortcutBar.HandleUpdateShortcut(client, packet, body);

					break;
				}

				case GameCharAgentMethods.AddShortcut:
				{
					// 0x206E = MoveItemToBar: always a bar drag ([u32 type][u32 slot][u32 id][u32 unk]). During create the
					// client populates the new character's starting bar with it right after CharCreationFinished (the
					// Blablatest/Hoxxling create captures show the starting abilities and spells at slots 0x64.. /
					// 0x4E20..), before entering the world - so it must be persisted on the editor connection too, or a
					// fresh character loses its retail starting bar and falls back to the seeded default.
					await _shortcutBar.HandleAddShortcut(client, packet, body);

					// This must not arm the enter-world handoff: the client barring something is not a statement about
					// being ready for the world, and keying on it strands creation whenever the starting kit is empty.
					// The handoff is armed on a retail-measured timer at CharCreationFinished -
					// CreateFlowService.ScheduleEnterWorldHandoff.
					break;
				}

				case GameCharAgentMethods.QueuePetCommand:
				{
					await _pets.HandlePetCommand(client, packet, body);
					return;
				}

				// Three c2s methods the corpus carries. Each is consumed here so it does not fall through to
				// LogUnknownPacket.

				case GameCharAgentMethods.CloseBank: // 0x201C - no arguments
				{
					// The bank's other half. There is no server-side bank session to tear down - the bank is opened by
					// a BankIIR and its contents are the character's own container rows - so the close is recorded and
					// no state is invented to clear. It is the hook for whatever the close should do once there is
					// something to do.
					Logger.LogInformation("Bank: {Char} closed the bank.", client.Session.Character.Name);
					break;
				}

				case GameCharAgentMethods.SetSelectedTag: // 0x20A2 - body = [u32 tagId]
				{
					// The client telling the server which tag it has selected. Remembered on the session so a
					// tag-driven feature can read it; nothing consumes it yet, and it is deliberately not persisted -
					// a selection is UI state, and no capture shows it surviving a relog.
					// Open work tracked in Gitea #748.
					var tagId = body.ReadUInt32();
					client.Session.SelectedTagId = tagId;
					Logger.LogInformation("Tags: {Char} selected tag {Tag}.", client.Session.Character.Name, tagId);
					break;
				}

				case GameCharAgentMethods.RequestRecipeInfo: // 0x2090 - the tradeskill recipe query
				{
					// Parsed and logged, not answered: no capture carries the reply, so the response would have to be
					// invented. The request itself is worth recording - it carries two 16-byte DecryptionKey_c, so the
					// client holds catalog keys and hands them over here.
					var request = RecipeInfoRequestCodec.Parse(body.ReadArray((uint)(body.Length - body.Position)));
					Logger.LogInformation("Tradeskill: {Char} requested recipe info for level {Level}, templates {T1}/{T2} (keys {K1}/{K2}). No reply is captured, so none is sent.",
						client.Session.Character.Name, request.LevelId, request.TemplateId1, request.TemplateId2,
						Convert.ToHexString(request.Key1), Convert.ToHexString(request.Key2));
					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		/// <summary>
		///     Inbound RPCs on the GameCharAgentProject endpoint (0x5B9756FC). Routed here by class id from
		///     ReceivedPacket because its method ids (0x2000, ...) collide with ordinary GameServer opcodes.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Every RequestAggroStatus poll must be answered: once the playfield is built the client polls for the
		///         aggro state between the player and each nearby hostile NPC (~17 over a login) and will not leave the
		///         loading screen until each poll has a reply. An unanswered poll is retried indefinitely, so the client
		///         never reaches the chat connection either.
		///     </para>
		///     <para>
		///         Grammar, from the kalantes_chat retail capture (port 7015):
		///     </para>
		///     <code>
		///     req  C-&gt;S: recv=GameCharAgentProject, send=GameCharInterfaceProject/&lt;player&gt;, op 0x2000,
		///                body [u32 0xC350][u32 charInstance][u32 0xC350][u32 targetInstance]   (two ProtoID32s)
		///     resp S-&gt;C: send=GameCharAgentProject/&lt;zone handle 0x58&gt;, recv=GameCharInterfaceProject/&lt;player&gt;,
		///                op 0x2000, body [u32 0xC350][u32 targetInstance][u32 cCategory]
		///     </code>
		/// </remarks>
		private async Task HandleGameCharAgentProject(INetworkClient client, Rpc packet, FaolanStream body)
		{
			switch ((GameCharAgentProjectMethods)packet.Method)
			{
				case GameCharAgentProjectMethods.RequestAggroStatus: // 0x2000 - gates the loading screen, so it must be answered
				{
					await HandleRequestAggroStatus(client, packet, body);
					break;
				}

				case GameCharAgentProjectMethods.ToggleVanityArmorFilter: // 0x200B - persist the appearance filter
				{
					await HandleToggleVanityArmorFilter(client, packet, body);
					break;
				}

				// Apprenticeship. Three of the four name one party as an ID32 and carry nothing else; the fourth is
				// no-arg. The client filters on its own rules before sending, so what arrives here is normally already
				// legal - ApprenticeService still checks, and answers when it is not.
				case GameCharAgentProjectMethods.RequestApprenticeStudent: // 0x2004 - the teacher names a student
				{
					await _apprentice.Request(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				case GameCharAgentProjectMethods.AcceptApprenticeTeacher: // 0x2002
				{
					await _apprentice.Accept(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				case GameCharAgentProjectMethods.DeclineApprenticeTeacher: // 0x2003
				{
					await _apprentice.Decline(client.Session.Character.Id, EntityIdCodec.Parse(body.ReadArray(EntityIdCodec.Length)).Id);
					break;
				}

				case GameCharAgentProjectMethods.LeaveApprenticeMode: // 0x2005 - no arguments; either side ends it
				{
					await _apprentice.Leave(client.Session.Character.Id);
					break;
				}

				case GameCharAgentProjectMethods.SetSprintInputState: // 0x200C, the sprint key
				{
					// The body is `(bool sprintInput, bool clientCheck)` - two bytes, the client's own signature - and
					// all four combinations occur in the corpus, so only the first byte decides whether the player is
					// sprinting. Reading the pair as one u16 and testing `== 1` selects `00 01`, which is sprintInput
					// false with the client's check set: the most common frame of all, and the exact inverse of the
					// intended condition.
					//
					// Retail answers this with `DispatchSetIsSprinting`, once per change of the request, which is what
					// TrackSprintEnergy sends. The energy pool it drives is what Exhausted fires from at 0
					// (docs/subsystems/combos.md).
					var input = SprintInputStateCodec.Parse(body.ReadArray(2));
					await _energy.TrackSprintEnergy(client, input.SprintInput);
					break;
				}

				default:
				{
					LogUnknownPacket(packet);
					break;
				}
			}
		}

		/// <summary>
		///     The player moved one of their three shield guards.
		/// </summary>
		/// <remarks>
		///     <para>
		///         Unlike an NPC's rose - which the tick rolls and shifts - a player's rose belongs to the client: it owns
		///         stat 0xA5 and the shield keybinds write it locally (Ctrl-1 left / Ctrl-2 top / Ctrl-3 right moves one
		///         shield, double-tapping stacks all three on that arc), then it uploads the new spread through
		///         <c>n3EngineClient::SendIIRToServer</c>. This is the one direction a CombatShieldIIR travels for a
		///         player: the server accepts the spread within the client's own clamp, remembers it, and relays it to the
		///         opponents who need to draw it.
		///     </para>
		///     <para>
		///         Client side: <c>AgeOfConan.exe</c> FUN_01117a20 (the keybind handler, called from the five command
		///         stubs at 0x00455cb0..0x004562b0) -&gt; FUN_00595f90, whose failure string names it
		///         "n3EngineClient::SendIIRToServer(): Unable to write the IIR to the stream". The handle in the record is
		///         the client's claim, so the subject is taken from the session like every other c2s that names itself.
		///     </para>
		/// </remarks>
		private async Task HandlePlayerShieldRose(INetworkClient client, byte[] rawPayload)
		{
			var player = _simulation.PlayerWorldEntity(client);
			if (player == null)
				return;

			CombatShieldIIR rose;
			try
			{
				rose = CombatShieldCodec.Parse(rawPayload.AsSpan(sizeof(uint)));
			}
			catch (CodecParseException e)
			{
				// The codec is strict and every captured record is a download, so the c2s form of this message is
				// proven from the client's writer rather than from captured bytes. Logged with the body, because a
				// mismatch here is a decode gap worth reading - but not a reason to drop a player mid-fight over a
				// keypress.
				Logger.LogError("Unparseable c2s CombatShieldIIR from {Player} ({Length} bytes, {Body}): {Reason}", player.Name, rawPayload.Length,
					rawPayload.ToHex(), e.Message);
				return;
			}

			var distribution = CombatShieldRose.Sanitize(rose.Value);
			if (distribution == player.ShieldDistribution)
				return; // the client re-sent the spread it already has (a double-tap onto a full arc); nothing to relay

			player.ShieldDistribution = distribution;
			await _broadcaster.BroadcastShieldRose(player);

			var (left, mid, right) = CombatShieldDistribution.Unpack(distribution);
			Logger.LogDebug("Shield rose: {Player} guards {Left}/{Mid}/{Right} (claimed handle 0x{Claimed:X8}).", player.Name, left, mid, right, rose.Handle);
		}

		private async Task HandleRequestAggroStatus(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var request = RequestAggroStatusCodec.Parse(body.ReadRemainingArray());
			var targetInstance = request.Target.Id;

			var instance = client.Session.Character.Id;

			// The reply's third field (cCategory) is the client's overhead name-colour category for this target, not
			// a live-aggro toggle: 0 = friendly (green), 1 = hostile (red), 2 = neutral (yellow). Live gdb shows
			// DispatchUpdateAggroStatus -> FUN_0115eb10 emitting the disposition signal that writes the nameplate
			// colour index (color_friendly_npc/color_hostile_npc/color_neutral_npc), and retail's cCategory is
			// exactly 0/1/2. This RPC is the sole driver of that colour: the NpcStats.community carried in the spawn
			// is correct but the client ignores it here.
			//
			// The value sent is the target's captured retail disposition, the same source that gates aggro. Passing
			// the disposition through (rather than `Hostile ? 1 : 0`) is what lets a neutral NPC render yellow and
			// keeps a friendly civilian in a non-zero faction community from rendering red.
			var category = WorldEntity.DispositionFriendly;
			// Resolve the target in the player's own instance (copy) of the map, not the default shared zone.
			var player = _simulation.PlayerWorldEntity(client);
			if (player != null && _simulation.World.ZoneForEntity(player).TryGet(targetInstance, out var target))
				category = target.Disposition;
			else if (CreateEditorBursts.EditorSceneDispositions.TryGetValue(targetInstance, out var editorCategory))
				// The create-editor scene is a scripted burst, not a simulated zone, so its five characters are in no
				// zone to resolve - their retail categories are carried with the burst instead.
				category = editorCategory;

			await client.SendAsync(new Rpc(GameEndpoints.ExistingCharacterPostReadyReportServerIdSenderEndpoint, Endpoints.Endpoint(RpcInterfaces.GameCharInterfaceProjectType, instance),
				UpdateAggroStatusCodec.Method, UpdateAggroStatusCodec.Build(new UpdateAggroStatus(request.Target, category))));

			Logger.LogDebug("RequestAggroStatus(target=0x{Target:X8}) -> cCategory={Category}.", targetInstance, category);
		}

		private async Task HandleRequestBindLocation(INetworkClient client, Rpc packet, FaolanStream body)
		{
			// body = [u32 ID32 type][u32 requesterId]. A short or empty poll must be tolerated - the client sends a
			// bare-header variant - because the reply is derived from the character, not the body, and requesterId
			// is only for the log. Reading past a short body throws EndOfStreamException, which propagates out of
			// ReceivedPacket and closes the connection.
			uint requesterId = 0;
			if (body.Length >= 8)
			{
				body.ReadUInt32(); // ID32 type (0x0000C350)
				requesterId = body.ReadUInt32();
			}

			// The bind playfield is the map of the character's persisted bind RespawnPoint (set by .bind / a
			// wayfarer); an unbound character (or one whose point vanished) falls back to its current map.
			var character = client.Session.Character;
			var bindMapId = character?.MapId ?? Map.TortageBeachId;
			if (character?.BindRespawnPointId is { } bindPointId)
				bindMapId = (await Database.GetRespawnPoint(bindPointId))?.MapId ?? bindMapId;

			// Reply framing: sender/receiver swapped from the request, because retail answers a request on the
			// conversation it arrived on. (An unsolicited bind push - the shape a bindstone use would need, framed on
			// the login-world conversation instead - has no sender left: the bind is set once at character creation,
			// and nothing else moves it yet.)
			await client.SendAsync(new Rpc(packet.Receiver, packet.Sender, GameCharInterfaceMethods.DispatchUpdateBindLocation, s => s.WriteUInt32(bindMapId)));
			Logger.LogDebug("RequestBindLocation({RequesterId:X8}) -> bind playfield {MapId}.", requesterId, bindMapId);
		}

		/// <summary>
		///     ToggleVanityArmorFilter(int slot, uint32 removeditemOccupySlots, bool toggleState): the appearance
		///     panel's per-slot (and master) "hide this vanity piece" toggle.
		/// </summary>
		/// <remarks>
		///     Fire-and-forget like sprint - retail sends no reply and the client renders the show/hide itself. Body is
		///     [u32 slot][u32 removedItemOccupySlots][u8 toggleState]. The flag is persisted (CharacterEquipment vanity
		///     flags) so it survives a relog: a withheld vanity item then drops out of the character snapshot equipment
		///     (PlayerSpawnEquipment), which is how the client restores the toggle on login.
		/// </remarks>
		private async Task HandleToggleVanityArmorFilter(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;
			if (character == null || body.Length - body.Position < 9)
				return;

			var slot = body.ReadUInt32();
			body.ReadUInt32(); // removedItemOccupySlots (bitmask; 0 across all captures - not needed to persist)
			// Polarity, confirmed in-client and against the retail vanity-head-selected capture (checkbox on ->
			// bit 2 set): a checked box means the slot's vanity item is shown, sends toggleState=1 and sets the
			// slot's mask bit. The mask bit is "this slot's vanity toggle is on"; toggleState maps straight to it.
			var toggleState = Convert.ToBoolean(body.ReadByte());

			var applied = await Database.SetVanityFilter(character.Id, slot, toggleState);
			Logger.LogDebug("ToggleVanityArmorFilter(slot={Slot}, toggleState={toggleState}) {Applied}.", slot, toggleState, applied
				? "persisted"
				: "(unmodelled appearance slot, ignored)");
		}

	}
}
