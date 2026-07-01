using Faolan.Core.Spawn;
using System;
using System.Buffers.Binary;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network;

namespace Faolan.GameServer.Spawn
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
		private const byte VecField1 = 0x0D; // protobuf field 1 fixed32 (x)
		private const byte VecField2 = 0x15; // field 2 (y)
		private const byte VecField3 = 0x1D; // field 3 (z)
		private const byte VecField4 = 0x25; // field 4 (w) - quat only

		// Produces the FA015807 player body (the packet[bodyStart..] form: [u32 bodyLen][subop][ack][handle][fieldA]
		// [transform][preFrame][CharacterIIR][post-IIR]). Returns null when the character has no scaffold rows yet
		// (the caller falls back to the captured-spawn splice path). `characterIir` is the ProtoCoreCharacterIIR blob.
		public static byte[] BuildFromScratch(uint handle, ulong playfield, Vector3 position, Quaternion rotation, byte[] characterIir, Character scaffoldCharacter)
		{
			ArgumentNullException.ThrowIfNull(characterIir);

			var scaffold = new PlayerSpawnScaffold
			{
				Ack = EntityClass.Character,
				Handle = handle,
				FieldA = 0,
				Vec3 = EncodeVec3(position),
				Quat = EncodeQuat(rotation),
				Scale = null,
				Playfield = playfield,
				CharacterIir = characterIir
			};

			// Fill the scaffold frame (pre-IIR + entry table + id list + pair lists) from the character's DB rows.
			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold))
				return null;

			return PlayerSpawnScaffoldCodec.Generate(scaffold);
		}

		// Vec3 = [0D x][15 y][1D z], each a little-endian fixed32 - the transform's protobuf float triple (15 bytes).
		private static byte[] EncodeVec3(Vector3 v)
		{
			var b = new byte[15];
			b[0] = VecField1;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1, 4), v.X);
			b[5] = VecField2;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6, 4), v.Y);
			b[10] = VecField3;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(11, 4), v.Z);
			return b;
		}

		// Quat = [0D x][15 y][1D z][25 w], each a little-endian fixed32 (20 bytes).
		private static byte[] EncodeQuat(Quaternion q)
		{
			var b = new byte[20];
			b[0] = VecField1;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(1, 4), q.X);
			b[5] = VecField2;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(6, 4), q.Y);
			b[10] = VecField3;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(11, 4), q.Z);
			b[15] = VecField4;
			BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(16, 4), q.W);
			return b;
		}
	}
}
