using System;
using System.Collections;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Reflection;
using ProtoBuf;
using ProtoBuf.Meta;

namespace Faolan.Core.Extensions
{
	public static class ProtobufExtensions
	{
		// The packed repeated members of a type, cached: `Length == 0` for almost every message, which is what keeps
		// the re-pack below a single dictionary lookup on the common path.
		private static readonly ConcurrentDictionary<Type, ValueMember[]> PackedMembers = new();

		public static T FromProtobuf<T>(this Span<byte> data) where T : IExtensible
		{
			return Serializer.Deserialize<T>(data);
		}

		public static T FromProtobuf<T>(this ReadOnlySpan<byte> data) where T : IExtensible
		{
			return Serializer.Deserialize<T>(data);
		}

		public static T FromProtobuf<T>(this byte[] data) where T : IExtensible
		{
			return Serializer.Deserialize<T>(data);
		}

		public static byte[] ToProtobuf(this IExtensible obj)
		{
			using var ms = new MemoryStream();
			Serializer.Serialize(ms, obj);

			return RepackSingleElementFields(obj, ms.ToArray());
		}

		// ⚠️ protobuf-net writes a repeated primitive holding EXACTLY ONE element UNPACKED (`08 <v>`) even when the
		// member is declared `[packed=true]` and the runtime model reports `IsPacked = True` - the writer optimises
		// past it, and there is no schema or model knob that turns it off. Retail always packs, so a captured message
		// whose packed field carries one element re-encoded exactly ONE byte short: `<tag|0><v>` where the wire has
		// `<tag|2><len><v>` (Gitea #538 - 7 of the 151 captured FullChar snapshot roots, and precisely the 7 that
		// carry a single `character_tag`; the 82 with two or more pack correctly and the 62 with none are unaffected).
		//
		// So the packing decision is taken back here, after the fact. Both forms are valid protobuf and the client
		// reads either - this is about reproducing a CAPTURED message byte-for-byte, which is what the codec
		// round-trip gates ask for.
		//
		// TOP-LEVEL only, and that is exact rather than partial: `character_tag` (ProtoFullCharacterIIR field 1) is the
		// ONLY `[packed=true]` field in `aoc-client-protos.proto`, and it sits at the root of its message. The other
		// two repeated scalars (`child_id`, `generic_stats`) are deliberately NOT marked packed - they are populated in
		// zero captures, so nothing proves their form (see the `protobuf_net_packing_and_corpus_gates` memory). If one
		// of them is ever marked, check whether it is nested before trusting this.
		private static byte[] RepackSingleElementFields(IExtensible obj, byte[] encoded)
		{
			var packed = PackedMembers.GetOrAdd(obj.GetType(), static t => RuntimeTypeModel.Default[t].GetFields().Where(f => f.IsPacked).ToArray());
			if (packed.Length == 0)
				return encoded;

			var single = packed.Where(f => ValueOf(f, obj) is ICollection { Count: 1 }).Select(f => f.FieldNumber).ToArray();

			return single.Length == 0 ? encoded : Repack(encoded, single);
		}

		private static object? ValueOf(ValueMember member, object obj)
		{
			return member.Member switch
			{
				PropertyInfo p => p.GetValue(obj),
				FieldInfo f => f.GetValue(obj),
				_ => null
			};
		}

		// Rewrites each `fields` member from its native wire type to length-delimited, keeping the payload bytes.
		private static byte[] Repack(byte[] encoded, int[] fields)
		{
			using var s = new MemoryStream(encoded.Length + 2 * fields.Length);
			var i = 0;
			while (i < encoded.Length)
			{
				var tagAt = i;
				var tag = ReadVarint(encoded, ref i);
				var payloadAt = i;
				switch (tag & 7)
				{
					case 0:
						ReadVarint(encoded, ref i);
						break;
					case 1:
						i += 8;
						break;
					case 2:
					{
						// NB the length is read FIRST: `i += ReadVarint(encoded, ref i)` evaluates `i` before the
						// call, so it would discard the varint's own advance and desync the walk.
						var length = (int)ReadVarint(encoded, ref i);
						i += length;
						break;
					}
					case 5:
						i += 4;
						break;
					default:
						// Groups (3/4). Unreachable: the only field this method is ever called for is a packed
						// repeated scalar, whose message has no group fields. Loud rather than silently copied.
						throw new NotSupportedException($"cannot re-pack a message carrying a group field ({tag & 7}) at offset {tagAt}");
				}

				if ((tag & 7) != 2 && fields.Contains((int)(tag >> 3)))
				{
					WriteVarint(s, ((tag >> 3) << 3) | 2);
					WriteVarint(s, (ulong)(i - payloadAt));
					s.Write(encoded, payloadAt, i - payloadAt);
				}
				else
				{
					s.Write(encoded, tagAt, i - tagAt);
				}
			}

			return s.ToArray();
		}

		private static ulong ReadVarint(byte[] data, ref int i)
		{
			ulong value = 0;
			for (var shift = 0; i < data.Length; shift += 7)
			{
				var b = data[i++];
				value |= (ulong)(b & 0x7F) << shift;
				if ((b & 0x80) == 0)
					break;
			}

			return value;
		}

		private static void WriteVarint(Stream s, ulong value)
		{
			while (value > 0x7F)
			{
				s.WriteByte((byte)((value & 0x7F) | 0x80));
				value >>= 7;
			}

			s.WriteByte((byte)value);
		}
	}
}
