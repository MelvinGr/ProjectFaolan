using System;
using System.Collections.Generic;
using System.Numerics;
using Faolan.Core.Database.Tables;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Spawn
{
	/// <summary>
	///     Builds a player's spawn body from scratch, with no captured template. The whole record is assembled into a typed
	///     <see cref="SimpleCharFullUpdateIIR" /> and serialised by <see cref="SimpleCharFullUpdateCodec" />:
	///     <list type="bullet">
	///         <item>the session and transform fields - handle, playfield token, position, rotation - set here;</item>
	///         <item>
	///             the scaffold frame (pre-IIR frame, weighted-attribute entry table, feat and skill id list, pair lists)
	///             filled from the character's database rows through PlayerSpawnScaffoldRows.TryApplyFrom, a fresh character
	///             getting the fresh defaults;
	///         </item>
	///         <item>
	///             the CharacterIIR - identity, appearance, equipment - supplied by the caller from the typed
	///             ProtoCoreCharacterIIR path.
	///         </item>
	///     </list>
	/// </summary>
	/// <remarks>
	///     The fresh-character constants are confirmed by a sweep of every committed player-spawn golden: all carry
	///     FieldA 0, no scale, the face-paint element first and empty pair lists, the pair lists and buffs being runtime and
	///     level state that is empty on a fresh spawn. The result is byte-identical to the captured assassin template when
	///     fed that character's decomposed parts (PlayerSpawnBuilderTests). Wrap it with the world-entry spawn builder for
	///     the wire packet.
	/// </remarks>
	public static class PlayerSpawnBuilder
	{
		/// <summary>
		///     Produces the player spawn record:
		///     <c>[subop][ack][handle][fieldA][transform][preIir][CharacterIIR][post-IIR]</c>. A fresh player spawn carries
		///     no per-instance scale, so the transform's optional scale field is absent. The u32 carrier length belongs to
		///     the framing sender. The caller hands over the typed character message and the codec serialises it, so nothing
		///     on this path deals in its bytes.
		/// </summary>
		/// <remarks>
		///     <paramref name="morphRegistry" /> is the MorphTarget catalog by ref name
		///     (IDatabaseRepository.GetMorphTargetRegistry, 1072 rows of static seed content cached for the process). It is
		///     what turns the character's stored editor choices into the spawn's weighted appearance entries, and it is
		///     required: without it a character wears the captured template's face.
		///     <para>
		///         A character with no scaffold rows throws. The rows are seeded at character creation, so a character
		///         without them is corruption, and there is no replay path to fall back to.
		///     </para>
		/// </remarks>
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

			// Fill the scaffold frame - pre-IIR, entry table, id list, pair lists - from the character's database rows.
			if (!PlayerSpawnScaffoldRows.TryApplyFrom(scaffoldCharacter, scaffold, morphRegistry))
			{
				throw new InvalidOperationException(
					$"Character {scaffoldCharacter.Id:X8} has no spawn-scaffold rows, so its FA015807 cannot be built. They are seeded at character creation (CreateFlowService.CommitCharacterName).");
			}

			return SimpleCharFullUpdateCodec.Build(scaffold);
		}
	}
}
