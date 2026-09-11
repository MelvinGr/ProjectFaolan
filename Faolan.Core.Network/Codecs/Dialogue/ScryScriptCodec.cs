using System;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;
using Faolan.Core.Protobuf;

namespace Faolan.Core.Network.Codecs.Dialogue
{
	/// <summary>
	///     <c>ScryScriptIIR</c> - the Tortage destiny/tutorial step driver. Server to client it pushes a named scripted
	///     step the client's intro flow runs ("PlayOrSkip", "IntroDone", "ChangeSex", "ApproachCasilda", or the
	///     "NEW_character_creation Slave Galley" creation step).
	/// </summary>
	/// <remarks>
	///     Decoded byte-exact from captures and built here from scratch. The wire, from the sub-opcode onward (the 4-byte
	///     carrier length is added by the send layer):
	///     <code>
	///     [subop][ack entityClass][u32 playerHandle][u8 flag=0][u32 BE pbLen][protobuf pbLen bytes]
	///     </code>
	///     The protobuf is a <c>ProtoScryScriptIIR</c>: version=1, type=2, int_parameter=3, string_parameter_one=4 (the
	///     step token), string_parameter_two=5 (resources), id_parameter=6 (a ProtoID32 pair).
	///     <para>
	///         <c>int_parameter</c> (field 3) is a <c>sint32</c> and therefore zigzag-encoded. The loose <c>Build</c>
	///         overloads here pass the raw wire varint - the values were lifted straight out of captures, e.g. 48126 or
	///         385910 - rather than the decoded integer, so the raw value is zigzag-decoded on the way in and protobuf-net
	///         re-encodes it to the identical bytes. Across the corpus field 3 is even in 1151 occurrences and odd in only
	///         14, which is the signature of zigzag (positives even, negatives odd) rather than of arbitrary ids, so a
	///         "scriptGroupId" of 48126 is really int_parameter 24063.
	///     </para>
	/// </remarks>
	public static class ScryScriptCodec
	{
		public const uint Opcode = (uint)IirTypes.ScryScriptIIR;
		private const byte FramingFlag = 0x00;

		public static byte[] Build(ScryScriptIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32(Opcode).WriteUInt32(EntityClass.Character).WriteUInt32(value.PlayerHandle).WriteByte(value.Flag).WriteArrayUInt32Length(value.Script.ToProtobuf());

			return s.ToArray();
		}

		// The no-token form: a StartScriptGroupOnClient (type 0) callback that runs a script group by id, with no step token
		// or resources (f4/f5 omitted) - the post-morph nudge the create-world handshake sends. The protobuf is
		// {f1=5 version, f2=0 type, f3=scriptGroupId, f6={0,0}}.
		public static byte[] Build(uint playerHandle, uint scriptGroupId)
		{
			return Build(new ScryScriptIIR(playerHandle, FramingFlag, new ProtoScryScriptIIR
			{
				Version = 5,
				type = ProtoScryScriptIIR.Type.StartScriptGroupOnClient,
				IntParameter = ZigZagDecode(scriptGroupId),
				IdParameter = new ProtoID32
				{
					Type = 0,
					Instance = 0
				}
			}));
		}

		// A named scripted step. Mode 2 is the simple step-token form ("IntroDone", "ChangeSex", "StartFixed3d", …); mode 7
		// is the rich creation step ("NEW_character_creation …" carried as an f4 remoteformat token, an f5 resource-preload
		// list "id:value;…", and a non-zero f6 pair). f6Field1/f6Field2 are the trailing sub-message pair, both 0 for the
		// simple steps.
		public static byte[] Build(uint playerHandle, string stepToken, uint mode = 2, uint flag = 0, string resources = "", ulong f6Field1 = 0, ulong f6Field2 = 0)
		{
			return Build(new ScryScriptIIR(playerHandle, FramingFlag, new ProtoScryScriptIIR
			{
				Version = 5,
				type = (ProtoScryScriptIIR.Type)mode,
				IntParameter = ZigZagDecode(flag),
				StringParameterOne = stepToken ?? "",
				StringParameterTwo = resources ?? "",
				IdParameter = new ProtoID32
				{
					Type = (uint)f6Field1,
					Instance = (uint)f6Field2
				}
			}));
		}

		public static ScryScriptIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(ScryScriptCodec));
			r.ExpectU32Be(Opcode, "the ScryScriptIIR sub-opcode");
			r.ExpectU32Be(EntityClass.Character, "the character entity class");
			var handle = r.ReadU32Be();
			var flag = r.ReadU8();
			var value = new ScryScriptIIR(handle, flag, r.ReadBytesU32Length().FromProtobuf<ProtoScryScriptIIR>());
			r.EnsureAtEnd();

			return value;
		}

		// The raw wire varint, which protobuf-net zigzag-re-encodes back to that exact varint.
		private static int ZigZagDecode(ulong raw)
		{
			return (int)(raw >> 1) ^ -(int)(raw & 1);
		}
	}

	public sealed record ScryScriptIIR(uint PlayerHandle, byte Flag, ProtoScryScriptIIR Script);
}
