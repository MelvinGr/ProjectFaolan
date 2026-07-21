using System;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds a player's FA015807 spawn BODY from scratch - no captured template (#210, the player analog of #185).
	// The whole body is assembled into a typed PlayerSpawnScaffold and serialised by PlayerSpawnScaffoldCodec.Generate:
	//   - session/transform fields (handle, playfield token, position, rotation) set here,
	//   - the scaffold frame (pre-IIR frame + weighted-attribute entry table + feat/skill id list + pair lists) filled
	//     from the character's DB rows via PlayerSpawnScaffoldRows.TryApplyFrom (fresh chars get the fresh defaults),
	//   - the CharacterIIR (identity/appearance/equipment) supplied by the caller (built by the typed
	//     ProtoCoreCharacterIIR path - the same boundary the live splice path has).
	// The fresh-character constants below were confirmed by a sweep of every committed player-spawn golden (Strie,
	// Dikketest, Hoxxling all carry FieldA=0, no Scale, EntryTableX 0xB7, empty pair lists; pair lists + buffs are
	// runtime/level state, 0 on a fresh spawn). Byte-identical to SpawnStrieTemplate.bin when fed Strie's decomposed
	// parts (PlayerSpawnBuilderTests). Wrap the result with GameServerListener.BuildWorldEntrySpawn for the wire packet.
	public static class PlayerSpawnBuilder
	{
		// A fresh player spawn carries no per-instance scale; the transform's optional 0x25 scale field is absent.

		// Produces the FA015807 player body (the packet[bodyStart..] form: [u32 bodyLen][subop][ack][handle][fieldA]
		// [transform][preFrame][CharacterIIR][post-IIR]). Returns null when the character has no scaffold rows yet
		// (the caller falls back to the captured-spawn splice path). `characterIir` is the ProtoCoreCharacterIIR blob.
		public static byte[]? BuildFromScratch(uint handle, ulong playfield, Vector3 position, Quaternion rotation, byte[] characterIir, Character scaffoldCharacter)
		{
			ArgumentNullException.ThrowIfNull(characterIir);

			var scaffold = new PlayerSpawnScaffold
			{
				Ack = EntityClass.Character,
				Handle = handle,
				FieldA = 0,
				// The scaffold carries these as raw field payloads (it round-trips captured bodies), but BUILDING them
				// from a Vector3/Quaternion is construction, so it goes through the generated classes: ProtoVec3 is
				// {0D x, 15 y, 1D z} and ProtoQuat adds {25 w}, each a little-endian fixed32 - byte-identical to the
				// hand-written encoders this replaces, and the same path WorldObjectBuilder already used.
				Vec3 = ((ProtoVec3)position).ToProtobuf(),
				Quat = ((ProtoQuat)rotation).ToProtobuf(),
				Scale = null,
				Playfield = playfield,
				CharacterIir = characterIir
			};

			// Fill the scaffold frame (pre-IIR + entry table + id list + pair lists) from the character's DB rows.
			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold))
				return null;

			return PlayerSpawnScaffoldCodec.Generate(scaffold);
		}
	}
}
