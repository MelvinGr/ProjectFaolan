using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	/// <summary>
	///     MoveTaskIIR - how the server tells clients where an entity is and which way it faces. The full recursive
	///     grammar is decoded (docs/re/movetask-grammar.md) and every one of the 6,225 captured records round-trips
	///     byte-exact (MoveTaskCodecCorpusTests), so this codec models the whole record. The body is
	///     <c>[subop][class][handle][u16 0][from: 3 raw u32][MoveTaskNode]</c>; the u32 length prefix belongs to the
	///     framing sender.
	/// </summary>
	/// <remarks>
	///     The semantic forms - a bare position, a rotate, a walk, a patrol, a knockback - are <see cref="MoveTaskNode" />
	///     values built by <see cref="MoveTaskNodes" />. They are not overloads here because they are not encoders: several
	///     share a parameter list, a waypoint list plus a float meaning a path to one and a patrol to another, and picking
	///     between them by signature would be ambiguous.
	/// </remarks>
	public static class MoveTaskCodec
	{
		public const uint Opcode = (uint)IirTypes.MoveTaskIIR;

		/// <summary>Canonical build: the only method here that writes bytes.</summary>
		public static byte[] Build(MoveTaskIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(value.Handle).WriteUInt16(0).WriteUInt32(value.FromX).WriteUInt32(value.FromY).WriteUInt32(value.FromZ);
			MoveTaskNodeCodec.Write(s, value.Task);
			return s.ToArray();
		}

		/// <summary>
		///     Parses the inner record, starting at the sub-opcode, throwing on any structure the grammar does not cover so
		///     an unrecognised form is never silently mis-encoded.
		/// </summary>
		public static MoveTaskIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(MoveTaskCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static MoveTaskIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "MoveTaskIIR subop");
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var handle = r.ReadU32Be();
			r.ExpectU16Be(0, "framing word");
			var fromX = r.ReadU32Be();
			var fromY = r.ReadU32Be();
			var fromZ = r.ReadU32Be();
			return new MoveTaskIIR(handle, fromX, fromY, fromZ, MoveTaskNodeCodec.Read(ref r));
		}
	}

	/// <summary>
	///     The MoveTaskIIR body. The from-position is kept as raw u32 bits so a captured float round-trips exactly.
	/// </summary>
	public sealed record MoveTaskIIR(uint Handle, uint FromX, uint FromY, uint FromZ, MoveTaskNode Task)
	{
		public MoveTaskIIR(uint handle, Vector3 from, MoveTaskNode task)
			: this(handle, BitConverter.SingleToUInt32Bits(from.X), BitConverter.SingleToUInt32Bits(from.Y), BitConverter.SingleToUInt32Bits(from.Z), task)
		{
		}
	}
}
