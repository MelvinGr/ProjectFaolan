using System;
using System.Collections.Generic;
using System.Numerics;
using System.Text;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// SimpleDynelVisualChangeIIR - changes one aspect of a world object's presentation: its mesh, its collision, an
	// animation, a particle effect, a fade, a name, its bounds.
	//
	//   [subop][u32 typeTag][u32 handle][u8 0][u32 kind][ kind-specific payload ]
	//
	// The tail is discriminated by `Kind`, a single big-endian u32.
	//
	// The switch is the client's own (`SimpleDynelVisualChangeIIR_c::ReadSubClass` = FUN_007a6910, reached through
	// the vtable's ReadSubClass slot 8), so the payload sizes are stated rather than inferred from what happened to
	// be captured. Each kind's meaning is the client's too: the applier beside the reader (FUN_007a6480) switches on
	// the same word and logs the enum's own name into `GameCode/simpledynel` before it acts, which is where every
	// `e_*` name in `DynelVisualChange` comes from. See that enum for the per-kind payloads; the ones that name an RDB
	// resource are checked against the client's index (the mesh a `ChangeMesh` names is a MESHINDEXFILE, and the
	// `SetKdTree` that follows it is that manifest's own `<CollisionMesh2 file>`).
	//
	// All 2633 captured bodies parse and re-encode byte-exact, with nothing left over
	// (SimpleDynelVisualChangeCodecTests). The corpus exercises kinds 0, 2, 3, 4, 5, 8 and 9; the rest come from the
	// client's reader, which is why they are implemented rather than left to throw.
	public static class SimpleDynelVisualChangeCodec
	{
		public const uint Opcode = (uint)IirTypes.SimpleDynelVisualChangeIIR;

		// The byte between the handle and the kind. Consumed by the base record's read rather than by ReadSubClass, and
		// zero in all 2633 captured bodies - so it is expected, not carried. It is the same slot `StatCodec` calls `Env`,
		// which is where it is non-zero.
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

		// Canonical - the only method here that writes bytes.
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

	// Exactly one of `Name` / `Words` carries the payload, selected by `Kind` - see the codec for the client's switch,
	// and `SimpleDynelVisualChanges` for the named forms.
	public sealed record SimpleDynelVisualChangeIIR(uint TypeTag, uint Handle, uint Kind, string? Name, IReadOnlyList<uint> Words);

	// The semantic forms of a visual change, named rather than told apart by their argument list. Each is one
	// `DynelVisualChange` case with its payload spelled out, so a caller says what it wants to happen.
	public static class SimpleDynelVisualChanges
	{
		/// <summary>Plays a base animation by name - the loot/container window's "Open", a door's "object_open" / "object_closed".</summary>
		public static SimpleDynelVisualChangeIIR SetBaseAnim(uint typeTag, uint handle, string animation)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.SetBaseAnim, animation, []);
		}

		/// <summary>
		///     Swaps the object's geometry to an RDB resource - the (id, type) pair its spawn record already carries
		///     (`WorldObject.GeometryAssetId` / `GeometryAssetType`).
		/// </summary>
		/// <remarks>Id 0 clears it: the object stays, with nothing drawn.</remarks>
		public static SimpleDynelVisualChangeIIR ChangeMesh(uint typeTag, uint handle, uint assetId, uint assetType)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.ChangeMesh, null, [assetId, assetType]);
		}

		/// <summary>
		///     Swaps the object's collision to a kd-tree resource - the mesh manifest's own <c>&lt;CollisionMesh2 file&gt;</c>, which
		///     is what `WorldObject.Kdtree` holds.
		/// </summary>
		/// <remarks>
		///     Id 0 drops collision entirely: that is the Casilda chain's "invisible wall" going away, and it is what a
		///     door sends when it opens.
		/// </remarks>
		public static SimpleDynelVisualChangeIIR SetKdTree(uint typeTag, uint handle, uint kdTreeId)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.SetKdTree, null, [kdTreeId]);
		}

		/// <summary>Fades the object in or out over <paramref name="seconds" /> (a big-endian float on the wire).</summary>
		/// <remarks>
		///     The client runs the ramp itself, so a state change belongs at the end of it: retail's mesh swap lands
		///     2.05 s after a 2.0 s fade-out. Captured durations are 2.0 on a harvest, 0.2 and 0.5 elsewhere.
		/// </remarks>
		public static SimpleDynelVisualChangeIIR FadeIn(uint typeTag, uint handle, float seconds)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.FadeIn, null, [BitConverter.SingleToUInt32Bits(seconds)]);
		}

		public static SimpleDynelVisualChangeIIR FadeOut(uint typeTag, uint handle, float seconds)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.FadeOut, null, [BitConverter.SingleToUInt32Bits(seconds)]);
		}

		/// <summary>Starts an RDB particle effect (type 1010207) on the object.</summary>
		/// <param name="effectResourceId">
		///     The record id only: the client pairs it with RDB type 1010207 itself (<c>FUN_0080a130</c>, "StartParticleEffect()
		///     Invalid particle resource id").
		/// </param>
		/// <param name="attractor">
		///     The attachment point on the object's skeleton. 0 places the effect free-standing at
		///     <paramref name="position" /> instead (the client's `if (attractor &lt; 1)` branch), which is what retail's
		///     quarry-brick harvest does.
		/// </param>
		/// <param name="rotationDegrees">Converted to radians by the client.</param>
		public static SimpleDynelVisualChangeIIR StartParticleEffect(uint typeTag, uint handle, uint effectResourceId, uint attractor, float rotationDegrees, Vector3 position)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.StartParticleEffect, null,
			[
				effectResourceId, attractor, BitConverter.SingleToUInt32Bits(rotationDegrees),
				BitConverter.SingleToUInt32Bits(position.X), BitConverter.SingleToUInt32Bits(position.Y), BitConverter.SingleToUInt32Bits(position.Z)
			]);
		}

		/// <summary>Stops a particle effect.</summary>
		/// <remarks>
		///     The one payload word is 0 in all 19 captured records, so which effect it names - or whether 0 means "all of
		///     them" - is not evidenced. [V0: the StopParticleEffect payload word.]
		/// </remarks>
		public static SimpleDynelVisualChangeIIR StopParticleEffect(uint typeTag, uint handle, uint effectResourceId = 0)
		{
			return new SimpleDynelVisualChangeIIR(typeTag, handle, (uint)DynelVisualChange.StopParticleEffect, null, [effectResourceId]);
		}
	}
}
