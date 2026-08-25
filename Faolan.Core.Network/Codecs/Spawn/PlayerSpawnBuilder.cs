using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	// Builds a player's FA015807 spawn BODY from scratch - no captured template (#210, the player analog of #185).
	// The whole record is assembled into a typed SimpleCharFullUpdateIIR and serialised by SimpleCharFullUpdateCodec.Build:
	//   - session/transform fields (handle, playfield token, position, rotation) set here,
	//   - the scaffold frame (pre-IIR frame + weighted-attribute entry table + feat/skill id list + pair lists) filled
	//     from the character's DB rows via PlayerSpawnScaffoldRows.TryApplyFrom (fresh chars get the fresh defaults),
	//   - the CharacterIIR (identity/appearance/equipment) supplied by the caller (built by the typed
	//     ProtoCoreCharacterIIR path - the same boundary the live splice path has).
	// The fresh-character constants below were confirmed by a sweep of every committed player-spawn golden (Strie,
	// Dikketest, Hoxxling all carry FieldA=0, no Scale, the face-paint element first (0xB7), empty pair lists; pair lists + buffs are
	// runtime/level state, 0 on a fresh spawn). Byte-identical to SpawnAssassinTemplate.bin when fed Strie's decomposed
	// parts (PlayerSpawnBuilderTests). Wrap the result with GameServerListener.BuildWorldEntrySpawn for the wire packet.
	public static class PlayerSpawnBuilder
	{
		// A fresh player spawn carries no per-instance scale; the transform's optional 0x25 scale field is absent.

		// Produces the FA015807 player RECORD ([subop][ack][handle][fieldA][transform][preIir][CharacterIIR]
		// [post-IIR]). The u32 carrier length belongs to the sender (#530 rule 2). The caller hands over the typed
		// character message; the codec serialises it, so nobody on this path deals in its bytes.
		//
		// `morphRegistry` is the MorphTarget catalog by ref name (IDatabaseRepository.GetMorphTargetRegistry - 1072
		// rows of static seed content, cached for the process). It is what turns the character's stored editor
		// choices into the spawn's weighted appearance entries; it is REQUIRED, because without it the character
		// used to wear Dikketest's captured face and nothing said so.
		//
		// A character with no scaffold rows THROWS. It used to return null so the caller could fall back to splicing a
		// captured spawn, but that path is gone (construct-don't-replay) and the rows are seeded at character creation,
		// so a character without them is corruption - and the "fallback" it enabled was a player nobody could see.
		public static byte[] BuildFromScratch(uint handle, ulong playfield, Vector3 position, Quaternion rotation, ProtoCoreCharacterIIR characterIir, Character scaffoldCharacter,
			IReadOnlyDictionary<string, MorphTarget> morphRegistry)
		{
			ArgumentNullException.ThrowIfNull(characterIir);

			var scaffold = new SimpleCharFullUpdateIIR
			{
				Ack = EntityClass.Character,
				Handle = handle,
				FieldA = 0,
				Position = position,
				Rotation = rotation,
				Scale = null,
				Playfield = playfield,
				Character = characterIir,
				Post = SpawnPostIirs.Player()
			};

			// Fill the scaffold frame (pre-IIR + entry table + id list + pair lists) from the character's DB rows.
			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold, morphRegistry))
			{
				throw new InvalidOperationException(
					$"Character {scaffoldCharacter.Id:X8} has no spawn-scaffold rows, so its FA015807 cannot be built. They are seeded at character creation (CreateFlowService.CommitCharacterName).");
			}

			return SimpleCharFullUpdateCodec.Build(scaffold);
		}
	}
}
