using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Threading.Tasks;
using Faolan.Core;
using Faolan.Core.Database.Interfaces;
using Faolan.Core.Database.Repositories;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Feats;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Protocol;
using Faolan.Core.Network.Server.ChatServer;
using Microsoft.Extensions.Logging;

namespace Faolan.GameServer.Feats
{
	// Feat TRAINING + RESET (untrain-all). Feats ride the player spawn's pre-IIR list (see PlayerSpawnScaffoldRows /
	// docs/subsystems/feats.md); this is the live protocol that mutates that set:
	//   - TRAIN: c2s FeatIIR (0x28DA68A0) state 0x04 [feat ids] -> server adds them to CharacterFeat -> s2c FeatIIR
	//     state 0x05 (echoes the list). The client renders the trained feats off the ack; they persist for next spawn.
	//   - RESET (untrain all): retail shows a confirm dialog (DispatchFeatUntrainMessageBox -> FeatUntrainAnswer) and
	//     then RELOADS the world (the "loading screen" the capture is named for) so the fresh spawn carries the cleared
	//     list. We clear CharacterFeat and reload via the change-map re-entry (a teleport to the current spot).
	// Decoded from the hakotep untrain/train captures; #251. Extracted from GameServerListener as a composed
	// collaborator (#200): the world-reload rides a teleport delegate seam (the listener's DoTeleport).
	public sealed class FeatService
	{
		private readonly Func<uint, uint, Vector3?, Task> _teleport;
		private readonly ICharacterRepository _database;
		private readonly IChatServerDispatcher _chat;
		private readonly ILogger _logger;

		public FeatService(ICharacterRepository database, ILogger logger, IChatServerDispatcher chat, Func<uint, uint, Vector3?, Task> teleport)
		{
			_database = database;
			_logger = logger;
			_chat = chat;
			_teleport = teleport;
		}

		// .feat list | .feat train <id> [<id>...] | .feat reset  - the test bootstrap for the feat subsystem (the retail
		// trigger is the client's Feats window with its point economy, which we can't drive from the server). train/reset
		// reload the world so the change shows immediately.
		public async Task FeatCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;

			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var verb = parts.Length > 0
				? parts[0].ToLowerInvariant()
				: "list";

			switch (verb)
			{
				case "list":
				{
					var feats = await _database.GetCharacterFeats(character.Id);
					await _chat.SendSystemMessage(character.Id, feats.Length == 0
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
						await _chat.SendSystemMessage(character.Id, "Usage: .feat train <id> [<id> ...]");
						return;
					}

					var added = await TrainWithinBudget(character.Id, character.Level, ids);
					if (added < 0)
					{
						await _chat.SendSystemMessage(character.Id, $"Not enough feat points (budget {FeatRules.PointBudget(character.Level)} at level {character.Level}).");
						return;
					}

					await _chat.SendSystemMessage(character.Id, $"Trained {added} new feat(s); reloading...");
					await _teleport(character.Id, character.MapId, character.Position);
					return;
				}

				case "reset":
				{
					await _chat.SendSystemMessage(character.Id, "Resetting feats; reloading...");
					await ResetFeats(client, character); // GM reset bypasses the confirm dialog
					return;
				}

				default:
				{
					await _chat.SendSystemMessage(character.Id, "Usage: .feat list | .feat train <id> ... | .feat reset");
					return;
				}
			}
		}

		// c2s FeatIIR (0x2000 carrier). rawPayload = the whole 0x2000 sub-payload starting at the [u32 length] prefix;
		// the FeatIIR body (from the sub-opcode) is everything after that prefix.
		public async Task HandleFeatTrain(INetworkClient client, byte[] rawPayload)
		{
			FeatIIR request;
			try
			{
				request = FeatCodec.Parse(rawPayload.AsSpan(4));
			}
			catch (CodecParseException e)
			{
				// The codec is STRICT (#530 rule 4), so a body we cannot account for throws rather than decoding into
				// something plausible. Caught HERE because a malformed c2s frame is the peer's problem, not a reason to
				// tear the session down - but it is logged with the offset the reader stopped at.
				_logger.LogDebug("Unparseable FeatIIR ({Length} bytes): {Reason}", rawPayload.Length, e.Message);
				return;
			}

			if (request.State != FeatCodec.StateTrainRequest)
			{
				_logger.LogDebug("FeatIIR state 0x{State:X2} (not a train request) - ignored.", request.State);
				return;
			}

			var handle = request.CharHandle;
			var featIds = request.FeatIds;

			var character = client.Session.Character;

			var added = await TrainWithinBudget(character.Id, character.Level, featIds);
			if (added < 0)
			{
				// Over the feat-point budget (safety net; the client normally pre-validates). Don't add or ack.
				await _chat.SendSystemMessage(character.Id, "Not enough feat points to train that.");
				return;
			}

			// Ack: echo the requested id list back (state 0x05), byte-for-byte as retail does. The client renders the
			// trained feats off this; the next spawn regenerates them from the persisted CharacterFeat rows.
			await client.SendAsync(new Rpc(GameServerListener.WorldSenderEndpoint, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceMethods.DispatchPassBlob,
				b => b.WriteArrayUInt32Length(FeatCodec.Build(handle, FeatCodec.StateAck, featIds))));

			_logger.LogInformation("Character {CharacterId} trained {Added} feat(s) [{Ids}].", character.Id, added, string.Join(",", featIds));
		}

		// c2s FeatUntrainAnswer (0x203E): the confirm-dialog reply. Reset ALL feats only when the player CONFIRMED and
		// the echoed token matches the outstanding request (guards against a stray/replayed answer).
		public async Task HandleFeatUntrainAnswer(INetworkClient client, Rpc packet, FaolanStream body)
		{
			var character = client.Session.Character;

			FeatUntrainAnswer reply;
			try
			{
				reply = FeatUntrainAnswerCodec.Parse(body.ReadRemainingArray());
			}
			catch (CodecParseException e)
			{
				_logger.LogDebug("Unparseable FeatUntrainAnswer: {Reason}", e.Message);
				return;
			}

			var pending = client.Session.PendingFeatUntrainToken;
			client.Session.PendingFeatUntrainToken = null;
			if (reply.Answer != FeatUntrainAnswerCodec.AnswerConfirm || pending == null || reply.Echo.Token != pending)
			{
				_logger.LogDebug("Feat untrain answer {Answer:X2} (token {Token:X8}, pending {Pending:X8}) - not confirming.", reply.Answer, reply.Echo.Token, pending ?? 0);
				return;
			}

			await ResetFeats(client, character);
		}

		// c2s UntrainFeat (0x2041): the client clicked "untrain all feats". Retail answers with a CONFIRM messagebox
		// (DispatchFeatUntrainMessageBox 0x2064) and only resets once the client confirms - so we send the dialog and
		// remember its token; the reset happens in HandleFeatUntrainAnswer. No feats -> nothing to confirm.
		public async Task HandleFeatUntrainRequest(INetworkClient client)
		{
			var character = client.Session.Character;

			var feats = await _database.GetCharacterFeats(character.Id);
			if (feats.Length == 0)
				return;

			var token = character.Id; // a per-request token the client echoes back in the answer (player handle == id)
			client.Session.PendingFeatUntrainToken = token;

			await client.SendAsync(new Rpc(Endpoints.Endpoint(Endpoints.GameCharAgentType, 0x58), client.Endpoint(Endpoints.GameCharInterfaceType),
				GameCharInterfaceMethods.DispatchFeatUntrainMessageBox, FeatUntrainMessageBoxCodec.Build(1, 0, token))); // kind 1 = untrain-all; value 0 = free (no cost model yet)

			_logger.LogInformation("Sent feat untrain-all confirm to character {CharacterId} ({Count} feats).", character.Id, feats.Length);
		}

		// Clear ALL of the character's trained feats and reload the world so the fresh spawn carries the cleared list
		// (the "loading screen" the retail untrain triggers). Shared by the confirm-dialog answer and the .feat GM reset.
		private async Task ResetFeats(INetworkClient client, Character character)
		{
			var cleared = await _database.ClearCharacterFeats(character.Id);
			_logger.LogInformation("Character {CharacterId} reset {Cleared} feat(s).", character.Id, cleared);
			if (cleared > 0)
				await _teleport(character.Id, character.MapId, character.Position);
		}

		// Trains `requested` onto the character iff the resulting feat set is legal: within the feat-point budget AND
		// with every PREREQUISITE satisfied (FeatPrereq, the client's own feat-tree edges - #251). Returns the number
		// of feats added, or -1 when the train is rejected.
		private async Task<int> TrainWithinBudget(uint characterId, ushort level, IReadOnlyList<uint> requested)
		{
			var existing = await _database.GetCharacterFeats(characterId);
			var after = existing.Concat(requested.Where(id => !existing.Contains(id))).ToList();
			if (!FeatRules.WithinBudget(level, after))
			{
				_logger.LogWarning("Character {CharacterId} feat train rejected: {Spent} points > budget {Budget} (level {Level}).", characterId, FeatRules.TotalCost(existing.Concat(requested)),
					FeatRules.PointBudget(level), level);
				return -1;
			}

			// Checked against the RESULTING set, not against `requested`: training a feat and its prerequisite in one
			// request is legal, and so is a prerequisite the character already holds.
			var prereqs = await _database.GetFeatPrereqs(after);
			var missing = FeatRules.MissingPrerequisites(after, prereqs);
			if (missing.Count > 0)
			{
				_logger.LogWarning("Character {CharacterId} feat train rejected: {Count} unmet prerequisite(s), first {Feat} requires {Requires}.",
					characterId, missing.Count, missing[0].Feat, missing[0].Requires);
				return -1;
			}

			return await _database.AddCharacterFeats(characterId, requested);
		}
	}
}
