using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Feats;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Conan;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer
{
	// Feat TRAINING + RESET (untrain-all). Feats ride the player spawn's pre-IIR list (see PlayerSpawnScaffoldRows /
	// docs/subsystems/feats.md); this is the live protocol that mutates that set:
	//   - TRAIN: c2s FeatIIR (0x28DA68A0) state 0x04 [feat ids] -> server adds them to CharacterFeat -> s2c FeatIIR
	//     state 0x05 (echoes the list). The client renders the trained feats off the ack; they persist for next spawn.
	//   - RESET (untrain all): retail shows a confirm dialog (DispatchFeatUntrainMessageBox -> FeatUntrainAnswer) and
	//     then RELOADS the world (the "loading screen" the capture is named for) so the fresh spawn carries the cleared
	//     list. We clear CharacterFeat and reload via the change-map re-entry (DoTeleport to the current spot).
	// Decoded from the hakotep untrain/train captures; #251.
	public partial class GameServerListener
	{
		// .feat list | .feat train <id> [<id>...] | .feat reset  - the test bootstrap for the feat subsystem (the retail
		// trigger is the client's Feats window with its point economy, which we can't drive from the server). train/reset
		// reload the world so the change shows immediately.
		private async Task FeatCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var verb = parts.Length > 0
				? parts[0].ToLowerInvariant()
				: "list";

			switch (verb)
			{
				case "list":
				{
					var feats = await Database.GetCharacterFeats(character.Id);
					await _agentServerDispatcher.SendSystemMessage(character.Id, feats.Length == 0
						? "No trained feats."
						: $"{feats.Length} feat(s): {string.Join(", ", feats)}");
					return;
				}

				case "train":
				{
					var ids = parts
						.Skip(1)
						.Select(p => uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
							? v
							: 0u)
						.Where(v => v != 0)
						.ToArray();
					if (ids.Length == 0)
					{
						await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .feat train &lt;id&gt; [&lt;id&gt; ...]");
						return;
					}

					var added = await TrainWithinBudget(character.Id, character.Level, ids);
					if (added < 0)
					{
						await _agentServerDispatcher.SendSystemMessage(character.Id, $"Not enough feat points (budget {FeatRules.PointBudget(character.Level)} at level {character.Level}).");
						return;
					}

					await _agentServerDispatcher.SendSystemMessage(character.Id, $"Trained {added} new feat(s); reloading...");
					await DoTeleport(character.Id, character.MapId, character.Position);
					return;
				}

				case "reset":
				{
					await _agentServerDispatcher.SendSystemMessage(character.Id, "Resetting feats; reloading...");
					await ResetFeats(client, character); // GM reset bypasses the confirm dialog
					return;
				}

				default:
					await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .feat list | .feat train &lt;id&gt; ... | .feat reset");
					return;
			}
		}

		// c2s FeatIIR (0x2000 carrier). rawPayload = the whole 0x2000 sub-payload starting at the [u32 length] prefix;
		// the FeatIIR body (from the sub-opcode) is everything after that prefix.
		private async Task HandleFeatTrain(INetworkClient client, byte[] rawPayload)
		{
			if (rawPayload.Length < 4 || !FeatIirCodec.TryParse(rawPayload[4..], out var handle, out var state, out var featIds))
			{
				Logger.LogDebug("Unparseable FeatIIR ({Length} bytes).", rawPayload.Length);
				return;
			}

			if (state != FeatIirCodec.StateTrainRequest)
			{
				Logger.LogDebug("FeatIIR state 0x{State:X2} (not a train request) - ignored.", state);
				return;
			}

			var character = client.Session.Character;
			if (character == null)
				return;

			var added = await TrainWithinBudget(character.Id, character.Level, featIds);
			if (added < 0)
			{
				// Over the feat-point budget (safety net; the client normally pre-validates). Don't add or ack.
				await _agentServerDispatcher.SendSystemMessage(character.Id, "Not enough feat points to train that.");
				return;
			}

			// Ack: echo the requested id list back (state 0x05), byte-for-byte as retail does. The client renders the
			// trained feats off this; the next spawn regenerates them from the persisted CharacterFeat rows.
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(FeatIirCodec.Build(handle, FeatIirCodec.StateAck, featIds))
				.SendAsync(client);

			Logger.LogInformation("Character {CharacterId} trained {Added} feat(s) [{Ids}].", character.Id, added, string.Join(",", featIds));
		}

		// c2s FeatUntrainAnswer (0x203E): the confirm-dialog reply. Reset ALL feats only when the player CONFIRMED and
		// the echoed token matches the outstanding request (guards against a stray/replayed answer).
		private async Task HandleFeatUntrainAnswer(INetworkClient client, ConanPacket packet)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var body = packet.Body.ReadRemainingArray();
			if (!FeatUntrainMessageBoxCodec.TryParseAnswer(body, out var answer, out var token))
				return;

			var pending = client.Session.PendingFeatUntrainToken;
			client.Session.PendingFeatUntrainToken = null;
			if (answer != FeatUntrainMessageBoxCodec.AnswerConfirm || pending == null || token != pending)
			{
				Logger.LogDebug("Feat untrain answer {Answer:X2} (token {Token:X8}, pending {Pending:X8}) - not confirming.", answer, token, pending ?? 0);
				return;
			}

			await ResetFeats(client, character);
		}

		// c2s UntrainFeat (0x2041): the client clicked "untrain all feats". Retail answers with a CONFIRM messagebox
		// (DispatchFeatUntrainMessageBox 0x2064) and only resets once the client confirms - so we send the dialog and
		// remember its token; the reset happens in HandleFeatUntrainAnswer. No feats -> nothing to confirm.
		private async Task HandleFeatUntrainRequest(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var feats = await Database.GetCharacterFeats(character.Id);
			if (feats.Length == 0)
				return;

			var token = character.Id; // a per-request token the client echoes back in the answer (player handle == id)
			client.Session.PendingFeatUntrainToken = token;

			await new PacketStream()
				.WriteHeader(Endpoints.EndpointBytes(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType), GameCharInterfaceOpcodes.DispatchFeatUntrainMessageBox)
				.WriteArray(FeatUntrainMessageBoxCodec.Build(1, 0, token)) // kind 1 = untrain-all; value 0 = free (no cost model yet)
				.SendAsync(client);

			Logger.LogInformation("Sent feat untrain-all confirm to character {CharacterId} ({Count} feats).", character.Id, feats.Length);
		}

		// Clear ALL of the character's trained feats and reload the world so the fresh spawn carries the cleared list
		// (the "loading screen" the retail untrain triggers). Shared by the confirm-dialog answer and the .feat GM reset.
		private async Task ResetFeats(INetworkClient client, Character character)
		{
			var cleared = await Database.ClearCharacterFeats(character.Id);
			Logger.LogInformation("Character {CharacterId} reset {Cleared} feat(s).", character.Id, cleared);
			if (cleared > 0)
				await DoTeleport(character.Id, character.MapId, character.Position);
		}

		// Trains `requested` onto the character iff the resulting feat set stays within its feat-point budget
		// (FeatRules). Returns the number of feats added, or -1 when the train is rejected as over-budget.
		private async Task<int> TrainWithinBudget(uint characterId, ushort level, IReadOnlyList<uint> requested)
		{
			var existing = await Database.GetCharacterFeats(characterId);
			var after = existing.Concat(requested.Where(id => !existing.Contains(id)));
			if (!FeatRules.WithinBudget(level, after))
			{
				Logger.LogWarning("Character {CharacterId} feat train rejected: {Spent} points > budget {Budget} (level {Level}).", characterId, FeatRules.TotalCost(existing.Concat(requested)),
					FeatRules.PointBudget(level), level);
				return -1;
			}

			return await Database.AddCharacterFeats(characterId, requested);
		}
	}
}
