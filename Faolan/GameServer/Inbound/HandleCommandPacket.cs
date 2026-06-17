using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Faolan.Core.Data;
using Faolan.Core.Database.Tables;
using Faolan.Core.Enums;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.GameServer.Spawn;
using Microsoft.Extensions.Logging;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// Applies one editor SetMorphValue callback to the character's body. The value is a ZIGZAG-encoded
		// signed slider value; CharacterMorphs decodes it and stores the signed value in the matching
		// CharacterBody column (the spawn morph float is that value / 1000 - see CharacterMorphs). Resource
		// refs (*hairmesh/*beardmesh/*texscript - the chosen hair/colors/tattoos/scars) are recorded in
		// editor send order; they become the spawn scaffold's weighted entries via MorphTarget. Sliders not
		// yet seen in captures are logged and skipped.
		private void ApplyMorph(Character character, string morph, uint value)
		{
			if (morph[0] == '*')
			{
				RecordBodyResource(character, morph);
				return;
			}

			if (!CharacterMorphs.TryApply(EnsureBody(character), morph, value))
				Logger.LogDebug("Character-editor: unmapped morph '{Morph}'={Value} (slider not yet modelled).", morph, value);
		}

		private async Task CommitCharacterName(INetworkClient client, string name)
		{
			var character = client.Session.Character;
			if (character == null || string.IsNullOrWhiteSpace(name))
				return;

			if (string.Equals(character.Name, name, StringComparison.Ordinal))
				return;

			// ClientInstance is no longer a foreign key, so a normal SaveChanges no longer trips the temp
			// create-instance FK; this persists the name together with the editor's other character edits
			// (sex/race/class/...). A UNIQUE-constraint violation (name already taken) surfaces as an
			// exception we tolerate so the client can retry with another name.
			try
			{
				character.Name = name;
				// The class is chosen before naming (the final editor step), so regenerate vitals from the now-known
				// class/level - replacing the level-1 rogue baseline seeded at row allocation.
				character.RegenerateMaxStats();
				// Persist the whole accumulated editor graph (name + sex/race/morphs Body + MaxStats) as one unit of work.
				await Database.SaveCharacter(character);
				Logger.LogInformation("Character-editor: committed character name '{Name}' for character {CharacterId:X8}.", name, character.Id);
			}
			catch (Exception e)
			{
				character.Name = null;
				Logger.LogWarning(e, "Character-editor: failed to commit character name '{Name}' (possibly already taken).", name);
			}
		}

		// Handles the client->server 0xA36D3B74 character-creation script callbacks.
		//
		// Wire format (after the 0x2000 wrapper and the 0xA36D3B74 subopcode have been consumed):
		//   uint32 ack            (0x0000C350)
		//   uint32 clientInstance (the create-editor temp instance, e.g. 0x021662C8)
		//   byte   reserved       (always 0x00)
		//   uint32 pbLength       (length of the protobuf message that follows)
		//   protobuf message:
		//     field 1 (varint) = 5      (constant)
		//     field 2 (varint) = 2      (constant)
		//     field 3 (varint) = numeric value   (e.g. ChangeSex sex id, ChangeHeadMesh mesh id)
		//     field 4 (string) = token            (the callback name, e.g. "TheNameIs")
		//     field 5 (string) = value            (optional string, e.g. the chosen character name)
		//     field 6 (bytes)  = trailing nested block (ignored)
		//
		// These callbacks are how the editor reports the player's choices (sex/race/head/size/class/
		// voice/name) and lifecycle events (IntroDone, ClientLoadingDone, Move). The retail capture
		// Stricken_soldier-guardian_create_random-actions.json proves the server sends
		// NO responses to any of these (ChangeSex/Finish/SetMorphValue/TheNameIs all stream client->server
		// with zero downstream replies); the client self-advances. So this handler is parse-only.
		//
		// The chosen name is captured into Session.PendingCharacterName rather than Character.Name, so the
		// create-editor gating (which keys on Character.Name == null) is not disturbed mid-stream;
		// promoting it to a persisted character is the separate enter-world step.
		private async Task HandleCommandPacket(INetworkClient client, ConanPacket packet)
		{
			var ack = packet.Body.ReadUInt32();
			var clientInstance = packet.Body.ReadUInt32();
			var reserved = packet.Body.ReadByte();
			var pbLength = packet.Body.ReadUInt32();
			var pbEnd = packet.Body.Position + pbLength;

			uint numericValue = 0;
			string token = null;
			string valueString = null;

			while (packet.Body.Position < pbEnd)
			{
				var tag = packet.Body.ReadByte();
				var field = tag >> 3;
				var wireType = tag & 0x07;

				switch (wireType)
				{
					case 0: // varint
					{
						var v = ReadProtobufVarint(packet.Body);
						if (field == 3)
							numericValue = (uint)v;
						break;
					}

					case 2: // length-delimited (string / bytes)
					{
						var s = packet.Body.ReadStringByteLength();
						if (field == 4)
							token = s;
						else if (field == 5)
							valueString = s;
						break;
					}

					default:
					{
						// Unexpected wire type; bail out to avoid desyncing the rest of the packet.
						packet.Body.Position = pbEnd;
						break;
					}
				}
			}

			var character = client.Session.Character;
			Logger.LogInformation("Character-editor script callback: token={Token}, numericValue={NumericValue} (0x{NumericValueHex:X}), value={Value}, clientInstance={ClientInstance:X8}.", token, numericValue, numericValue,
				valueString ?? "", clientInstance);

			switch (token)
			{
				// The editor sends class/race as 2x the real id (verified against captured char-lists: editor
				// 36/68/86/82 -> class 0x12/0x22/0x2B/0x29; editor race 2/6 -> race 1/3). Store the halved
				// canonical id so the character-list packet (and everything else) gets a valid class/race; sending
				// the raw 2x value makes the client show dashes for race/class on the select screen.
				case "ChangeSex":
				{
					if (character != null)
						// Sex IS the editor's usual 2x convention: 4 -> wire 2 = MALE, 6 -> wire 3 = FEMALE
						// (anchored on Hoxxling, who is male: ChangeSex 4 + a chosen beardmesh; the earlier
						// "6 = male" read was inverted).
					{
						character.Sex = numericValue == 4
							? Sex.Male
							: Sex.Female;
					}

					break;
				}

				case "ChangeRace":
				{
					if (character != null)
						character.Race = (Race)(numericValue / 2);
					break;
				}

				case "ChangeClass":
				{
					if (character != null)
						character.Class = (Class)(numericValue / 2);
					break;
				}

				case "ChangeHeadMesh":
				{
					if (character != null)
					{
						EnsureBody(character)
							.HeadMesh = numericValue;
					}

					break;
				}

				case "ChangeSize":
				{
					if (character != null)
					{
						EnsureBody(character)
							.Size = (byte)numericValue;
					}

					break;
				}

				case "ChangeVoice":
				{
					if (character != null)
					{
						EnsureBody(character)
							.Voice = (byte)numericValue;
					}

					break;
				}

				case "TheNameIs":
				{
					// Commit the chosen name to the character row so the later world-entry Hello (a
					// separate connection/session that re-loads the character from the database) sees a
					// named character and takes the normal world-entry path. The client may try several
					// names (e.g. Stricken -> Strickennn -> Strickenen); the last one that sticks wins.
					client.Session.PendingCharacterName = valueString;
					await CommitCharacterName(client, valueString);

					// Only confirm the name to the client (NicknameOk + CharCreationFinished) if it actually
					// persisted - a UNIQUE-constraint failure leaves Character.Name null, in which case retail
					// would reject the name and the client retries with another. (Reject path not yet
					// implemented; the client simply re-sends TheNameIs with a new name.)
					if (client.Session.Character?.Name == valueString)
					{
						await SendNicknameAccepted(client, valueString);

						// The character is now named; its NEXT GameServer connection (opened after the 0x2002
						// enter-world handoff) is the create-world entry and must get the intro/cutscene. Mark it
						// pending so the world Hello recognises that connection (and not later normal logins).
						PendingCreateWorldEntry.TryAdd(client.Session.Character.Id, 0);
						Logger.LogInformation("Create-editor: character {CharacterId:X8} named '{Name}'; marked pending create-world entry.", client.Session.Character.Id, valueString);
					}

					break;
				}

				case "Finish":
				{
					// The editor is done customizing; its SetMorphValue stream follows (only for changed
					// morphs, so the set varies and may be empty if nothing was morphed). Debounce the
					// post-morph trio off Finish + each morph so it fires once the stream goes quiet,
					// regardless of which morphs were sent.
					SchedulePostMorphBurst(client);
					break;
				}

				case "SetMorphValue":
				{
					// The retail creator acks every non-empty morph with a 0xBADF5A4B packet that echoes the
					// morph value. (Confirmed from the dikketest capture, whose creator DL the analyzer
					// decoded - strickenen only LOOKED silent because its creator DL was dropped by the
					// compression-cut.) The post-morph trio is debounced off Finish + each morph rather than
					// keyed to a specific morph, since leaving the body default means "thigh"/body morphs are
					// never sent.
					if (!string.IsNullOrEmpty(valueString))
					{
						if (character != null)
							ApplyMorph(character, valueString, numericValue);
						await SendMorphValueAck(client, valueString);
					}

					SchedulePostMorphBurst(client);
					break;
				}

				case "IntroDone":
				{
					// On the post-creation WORLD connection this is the last cutscene trigger (the beach /
					// ApproachCasilda finale) - send the next chunk. On the EDITOR connection it instead marks
					// the editor interactive (the editor connection then drives itself; see the create flow).
					if (IsCreateWorldConnection(client))
					{
						await SendNextCreateWorldChunk(client);
						break;
					}

					Logger.LogInformation("Character-editor: IntroDone - editor is interactive; the editor connection is now silent until enter-world.");
					break;
				}

				// The intro cutscene is delivered one chunk per client script trigger, in order
				// (ClientLoadingDone -> StartScript -> StartScript -> Move -> IntroDone), matching retail.
				case "ClientLoadingDone":
				case "StartScript":
				case "Move":
				{
					if (IsCreateWorldConnection(client))
						await SendNextCreateWorldChunk(client);
					break;
				}

				default:
				{
					Logger.LogInformation("Character-editor: unhandled script callback token '{Token}' (numericValue={NumericValue}, value={Value}).", token, numericValue, valueString ?? "");
					break;
				}
			}
		}

		// Persists the chosen character name so the subsequent world-entry Hello (a fresh session that
		// re-loads the character from the database) sees a named character. Tolerates duplicate-name
		// failures (unique index) without dropping the editor connection.
		// Lazily creates and tracks the character's body row so the editor's morph / head-mesh / size / voice
		// edits have somewhere to persist (CharacterBodies was previously never populated). The body is saved
		// together with the character when the name is committed.
		private static CharacterBody EnsureBody(Character character)
		{
			// In-memory only: the body accumulates editor edits on the cached session character and is persisted as
			// part of the character graph at name-commit (SaveCharacter). No DB context is held during editing.
			character.Body ??= new CharacterBody { CharacterId = character.Id };
			return character.Body;
		}

		private static ulong ReadProtobufVarint(FaolanStream body)
		{
			ulong result = 0;
			var shift = 0;
			byte b;
			do
			{
				b = body.ReadByte();
				result |= (ulong)(b & 0x7F) << shift;
				shift += 7;
			}
			while ((b & 0x80) != 0 && shift < 64);

			return result;
		}

		// Appends the resource ref to the character's chosen-appearance list (deduplicated; the editor can
		// resend the set when a creation session is resumed). Persisted with the character graph at name-commit.
		private static void RecordBodyResource(Character character, string resourceRef)
		{
			character.BodyResources ??= new List<CharacterBodyResource>();
			if (character.BodyResources.Any(r => r.Ref == resourceRef))
				return;

			character.BodyResources.Add(new CharacterBodyResource
			{
				CharacterId = character.Id,
				Sequence = character.BodyResources.Count,
				Ref = resourceRef
			});
		}
	}
}
