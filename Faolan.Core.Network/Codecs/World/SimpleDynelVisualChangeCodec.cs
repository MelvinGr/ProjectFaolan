using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// SimpleDynelVisualChangeIIR (0xE7F4F8F8) - changes ONE aspect of a world object's presentation: its mesh, its
	// collision, an animation, a particle effect, a fade, a name, its bounds.
	//
	//   [subop][u32 typeTag][u32 handle][u8 0][u32 kind][ kind-specific payload ]
	//
	// ⚠️ THE TAIL IS DISCRIMINATED BY `Kind`, and the record's own field split was off by one byte (#535). This used
	// to read `[u32 word0][u8 state]` and carry everything after as an opaque `Trailer`, on the reasoning that "the
	// two captured shapes DISAGREE and nothing in the corpus reconciles them" - one ending in four zero bytes, the
	// other in a length-prefixed name. They are the same grammar: `word0`'s three low bytes and the `state` byte are
	// ONE big-endian u32, and that u32 selects the payload. Reading a u32 as `[u32][u8]` is why the leading word
	// looked like a constant zero and the discriminator looked like a byte.
	//
	// The switch is the client's own (`SimpleDynelVisualChangeIIR_c::ReadSubClass` = FUN_007a6910, reached through
	// the vtable's ReadSubClass slot 8), so the payload sizes are stated rather than inferred from what happened to
	// be captured. Each kind's MEANING is the client's too (#678): the applier beside the reader (FUN_007a6480)
	// switches on the same word and logs the enum's own name into `GameCode/simpledynel` before it acts - which is
	// where every `e_*` name in `DynelVisualChange` comes from, and why none of them is a guess. See that enum for
	// the per-kind payloads; the ones that name an RDB resource were then checked against the client's index (the
	// mesh a `ChangeMesh` names really is a MESHINDEXFILE, and the `SetKdTree` that follows it really is that
	// manifest's own `<CollisionMesh2 file>`).
	//
	// All 2633 captured bodies parse and re-encode byte-exact, with nothing left over
	// (SimpleDynelVisualChangeCodecTests). The corpus exercises kinds 0, 2, 3, 4, 5, 8 and 9; the rest come from the
	// client's reader, which is why they are implemented rather than left to throw.
	//
	// This codec merges what used to be TWO emitters of this IIR (SimpleDynelMessageCodec.BuildVisualChange and
	// WorldLoot.BuildOpenWindowBody), which is exactly the duplicate the one-IIR-one-codec rule exists to surface.
	public static class SimpleDynelVisualChangeCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleDynelVisualChangeIIR;

		// The byte between the handle and the kind. Consumed by the BASE record's read rather than by ReadSubClass,
		// and zero in all 2633 captured bodies - so it is expected, not carried. (The same slot `StatCodec` calls
		// `Env`, which is where it is non-zero.)
		private const byte BaseTail = 0x00;

		// How many u32 each non-named kind's payload carries, straight off the client's switch.
		private static readonly Dictionary<uint, int> PayloadWords = new Dictionary<uint, int>
		{
			[(uint)DynelVisualChange.StartParticleEffect] = 6,
			[(uint)DynelVisualChange.StopParticleEffect] = 1,
			[(uint)DynelVisualChange.ChangeMesh] = 2,
			[(uint)DynelVisualChange.SetKdTree] = 1,
			[(uint)DynelVisualChange.FadeIn] = 1,
			[(uint)DynelVisualChange.FadeOut] = 1,
			[(uint)DynelVisualChange.ChangeDecal] = 1,
			[(uint)DynelVisualChange.SetBounds] = 12,
			[(uint)DynelVisualChange.SetInverseObbMatrix] = 16
		};

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(SimpleDynelVisualChangeIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		public static SimpleDynelVisualChangeIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(SimpleDynelVisualChangeCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static SimpleDynelVisualChangeIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "SimpleDynelVisualChangeIIR subop");
			var typeTag = r.ReadU32Be();
			var handle = r.ReadU32Be();
			r.ExpectU8(BaseTail, "the base record's byte ahead of the kind");
			var kind = r.ReadU32Be();

			if (IsNamed(kind))
				return new SimpleDynelVisualChangeIIR(typeTag, handle, kind, Encoding.Latin1.GetString(r.ReadBytesU16Length()), []);

			if (!PayloadWords.TryGetValue(kind, out var words))
				throw CodecParseException.Unexpected(nameof(SimpleDynelVisualChangeCodec), r.Position - 4, "a kind the client's reader knows (0-12)", kind.ToString());

			var payload = new uint[words];
			for (var i = 0; i < words; i++)
				payload[i] = r.ReadU32Be();

			return new SimpleDynelVisualChangeIIR(typeTag, handle, kind, null, payload);
		}

		internal static void Write(FaolanStream s, SimpleDynelVisualChangeIIR v)
		{
			ArgumentNullException.ThrowIfNull(v);
			s.WriteUInt32(Opcode).WriteUInt32(v.TypeTag).WriteUInt32(v.Handle).WriteByte(BaseTail).WriteUInt32(v.Kind);

			if (IsNamed(v.Kind))
			{
				var text = Encoding.Latin1.GetBytes(v.Name ?? "");
				s.WriteUInt16(text.Length).WriteArray(text);

				return;
			}

			if (!PayloadWords.TryGetValue(v.Kind, out var words))
				throw new ArgumentOutOfRangeException(nameof(v), v.Kind, "not a kind the client's reader knows (0-12)");

			if (v.Words.Count != words)
				throw new ArgumentOutOfRangeException(nameof(v), v.Words.Count, $"kind {v.Kind} takes exactly {words} payload words");

			foreach (var word in v.Words)
				s.WriteUInt32(word);
		}

		// The four kinds whose payload is a length-prefixed name. They are one branch in the client's switch, split
		// across `case 0/6/7` and `case 1` only because the decompiler kept the duplicated tails apart.
		private static bool IsNamed(uint kind)
		{
			return kind is (uint)DynelVisualChange.SetBaseAnim or (uint)DynelVisualChange.PlayActionAnim
				or (uint)DynelVisualChange.SetName or (uint)DynelVisualChange.SetDisplayName;
		}
	}

	// SimpleDynelVisualChangeIIR (0xE7F4F8F8). Exactly one of `Name` / `Words` carries the payload, selected by
	// `Kind` - see the codec for the client's switch, and `SimpleDynelVisualChanges` for the named forms.
	public sealed record SimpleDynelVisualChangeIIR(uint TypeTag, uint Handle, uint Kind, string? Name, IReadOnlyList<uint> Words);

	// The semantic forms of a visual change, named rather than told apart by their argument list (#530 rule 8). Each
	// is one `DynelVisualChange` case with its payload spelled out, so a caller says what it wants to happen.
	public static class SimpleDynelVisualChanges
	{
		// The RDB type the client hardcodes for a particle effect - it reads only the record id off the wire and
		// pairs it with 1010207 itself ("StartParticleEffect() Invalid particle resource id", FUN_0080a130).
		public const uint ParticleEffectResourceType = 1010207;

		// Play a base animation by NAME - the loot/container window's "Open", a door's "object_open" /
		// "object_closed".
		public static SimpleDynelVisualChangeIIR SetBaseAnim(uint typeTag, uint handle, string animation)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.SetBaseAnim, animation, []);
		}

		// Swap the object's GEOMETRY to an RDB resource - the (id, type) pair its spawn record already carries
		// (`WorldObject.GeometryAssetId` / `GeometryAssetType`). Id 0 clears it: the object stays, with nothing drawn.
		public static SimpleDynelVisualChangeIIR ChangeMesh(uint typeTag, uint handle, uint assetId, uint assetType)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.ChangeMesh, null, [assetId, assetType]);
		}

		// Swap the object's COLLISION to a kd-tree resource - the mesh manifest's own `<CollisionMesh2 file>`, which
		// is what `WorldObject.Kdtree` holds. Id 0 drops collision entirely: that is the Casilda chain's "invisible
		// wall" going away (#267), and it is what a door sends when it opens.
		public static SimpleDynelVisualChangeIIR SetKdTree(uint typeTag, uint handle, uint kdTreeId)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.SetKdTree, null, [kdTreeId]);
		}

		// Fade the object IN / OUT over `seconds` (a big-endian float on the wire; 2.0 on a harvest, 0.2 and 0.5
		// elsewhere). The client runs the ramp itself, so the state change belongs at the END of it - retail's mesh
		// swap lands 2.05 s after a 2.0 s fade-out.
		public static SimpleDynelVisualChangeIIR FadeIn(uint typeTag, uint handle, float seconds)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.FadeIn, null, [BitConverter.SingleToUInt32Bits(seconds)]);
		}

		public static SimpleDynelVisualChangeIIR FadeOut(uint typeTag, uint handle, float seconds)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.FadeOut, null, [BitConverter.SingleToUInt32Bits(seconds)]);
		}

		// Start an RDB particle effect (type 1010207) on the object. `attractor` is the attachment point on its
		// skeleton - 0 places the effect free-standing at `position` instead (the client's `if (attractor < 1)`
		// branch), which is what retail's quarry-brick harvest does. `rotationDegrees` is converted to radians by the
		// client.
		public static SimpleDynelVisualChangeIIR StartParticleEffect(uint typeTag, uint handle, uint effectResourceId, uint attractor, float rotationDegrees, Vector3 position)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.StartParticleEffect, null,
			[
				effectResourceId, attractor, BitConverter.SingleToUInt32Bits(rotationDegrees),
				BitConverter.SingleToUInt32Bits(position.X), BitConverter.SingleToUInt32Bits(position.Y), BitConverter.SingleToUInt32Bits(position.Z)
			]);
		}

		// Stop a particle effect. Its one word is 0 in all 19 captured records, so which effect it names (or whether
		// 0 means "all of them") is not evidenced. [V0: the StopParticleEffect payload word.]
		public static SimpleDynelVisualChangeIIR StopParticleEffect(uint typeTag, uint handle, uint effectResourceId = 0)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.StopParticleEffect, null, [effectResourceId]);
		}
	}
}
