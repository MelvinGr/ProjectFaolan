using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The despawn / "entity left your view" packet - n3ToClientQuitIIR, decoded from 3271 retail records
	// (server->client only). The 0x2000 IIR body is:
	//   [subop][class][handle = entity to remove][0x01 marker][u32 playfield-instance token]
	// The second word is the removed object's class (EntityClass), not a constant ack. The client indexes the removal
	// by (class, handle), so a despawn sent with the wrong class silently misses the object: a static world object is a
	// SimpleDynel 0xC878 / a loot bag 0xC87A / a door 0xC748, while players and NPCs are Character 0xC350.
	// The marker is always 0x01. The token is not a handle - it is the per-playfield id the server announces at world
	// entry (woven into the 0x737A6DF9 snapshot tails, between markers 000000BF and 0000040B) and stamps into every
	// despawn so the client scopes the removal to that playfield instance. PlayfieldTokenScan finds it in the snapshot
	// we actually send, so the despawn token matches what the client learned, by construction.
	public static class N3ToClientQuitCodec
	{
		public const uint Opcode = (uint)IirTypes.N3ToClientQuitIIR;

		private const byte RecordMarker = 0x01; // constant in all 3271 captured records

		// Canonical - the only method here that writes bytes.
		public static byte[] Build(N3ToClientQuitIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		/// <summary>Removes <paramref name="handle" /> in playfield <paramref name="token" />.</summary>
		/// <param name="classWord">
		///     The removed object's entity class. Defaults to Character (players and NPCs); a static world object must
		///     pass its own class (WorldObjectCodec.TypeTagForClass) or the client will not remove it.
		/// </param>
		public static byte[] Build(uint handle, uint token, uint classWord = EntityClass.Character)
		{
			return Build(new N3ToClientQuitIIR(handle, classWord, token));
		}

		public static N3ToClientQuitIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(N3ToClientQuitCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static N3ToClientQuitIIR Read(ref SpanReader r)
		{
			r.ExpectU32Be(Opcode, "n3ToClientQuitIIR subop");
			var classWord = r.ReadU32Be(); // a field (Character / SimpleDynel / LootBag / ...), not a constant
			var handle = r.ReadU32Be();
			r.ExpectU8(RecordMarker, "record marker");
			return new N3ToClientQuitIIR(handle, classWord, r.ReadU32Be());
		}

		internal static void Write(FaolanStream s, N3ToClientQuitIIR v)
		{
			s.WriteUInt32(Opcode).WriteUInt32(v.ClassWord).WriteUInt32(v.Handle).WriteByte(RecordMarker).WriteUInt32(v.Token);
		}
	}

	// 17 bytes on the wire: [subop][class][handle][marker][token].
	public sealed record N3ToClientQuitIIR(uint Handle, uint ClassWord, uint Token);
}
