using System;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Codecs.Feats;
using Faolan.Core.Network.Constants;
using Faolan.Core.Network.Packets;
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

			var added = await Database.AddCharacterFeats(character.Id, featIds);

			// Ack: echo the requested id list back (state 0x05), byte-for-byte as retail does. The client renders the
			// trained feats off this; the next spawn regenerates them from the persisted CharacterFeat rows.
			await new PacketStream()
				.WriteHeader(WorldFraming.Sender, client.Endpoint(Endpoints.GameInterfaceType), GameInterfaceOpcodes.DispatchPassBlob)
				.WriteArrayUInt32Length(FeatIirCodec.Build(handle, FeatIirCodec.StateAck, featIds))
				.SendAsync(client);

			Logger.LogInformation("Character {CharacterId} trained {Added} feat(s) [{Ids}].", character.Id, added, string.Join(",", featIds));
		}

		// c2s UntrainFeat (0x2041) / FeatUntrainAnswer (0x203E): reset ALL trained feats. Retail confirms via a dialog
		// first (not yet built), so we act on either signal. Clears CharacterFeat, then reloads the world so the client
		// re-reads the (now empty) feat list from the fresh spawn - the same loading screen the retail untrain triggers.
		private async Task HandleFeatReset(INetworkClient client)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var cleared = await Database.ClearCharacterFeats(character.Id);
			Logger.LogInformation("Character {CharacterId} reset {Cleared} feat(s).", character.Id, cleared);
			if (cleared > 0)
				await DoTeleport(character.Id, character.MapId, character.Position); // reload so the cleared list takes effect
		}

		// .feat list | .feat train <id> [<id>...] | .feat reset  - the test bootstrap for the feat subsystem (the retail
		// trigger is the client's Feats window with its point economy, which we can't drive from the server). train/reset
		// reload the world so the change shows immediately.
		private async Task FeatCommand(INetworkClient client, string arguments)
		{
			var character = client.Session.Character;
			if (character == null)
				return;

			var parts = (arguments ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
			var verb = parts.Length > 0 ? parts[0].ToLowerInvariant() : "list";

			switch (verb)
			{
				case "list":
				{
					var feats = await Database.GetCharacterFeats(character.Id);
					await _agentServerDispatcher.SendSystemMessage(character.Id,
						feats.Length == 0 ? "No trained feats." : $"{feats.Length} feat(s): {string.Join(", ", feats)}");
					return;
				}

				case "train":
				{
					var ids = parts.Skip(1)
						.Select(p => uint.TryParse(p, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0u)
						.Where(v => v != 0)
						.ToArray();
					if (ids.Length == 0)
					{
						await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .feat train &lt;id&gt; [&lt;id&gt; ...]");
						return;
					}

					var added = await Database.AddCharacterFeats(character.Id, ids);
					await _agentServerDispatcher.SendSystemMessage(character.Id, $"Trained {added} new feat(s); reloading...");
					await DoTeleport(character.Id, character.MapId, character.Position);
					return;
				}

				case "reset":
				{
					var cleared = await Database.ClearCharacterFeats(character.Id);
					await _agentServerDispatcher.SendSystemMessage(character.Id, $"Reset {cleared} feat(s); reloading...");
					if (cleared > 0)
						await DoTeleport(character.Id, character.MapId, character.Position);
					return;
				}

				default:
					await _agentServerDispatcher.SendSystemMessage(character.Id, "Usage: .feat list | .feat train &lt;id&gt; ... | .feat reset");
					return;
			}
		}
	}
}
