namespace Faolan.Core.Network.Codecs.World
{
	// Construct-side codec for the dynel-family full-update records (#187, docs/iir-readers-combat-world.md):
	// DoorFullUpdateIIR_c (0x33A56FB0), DestructibleFullUpdateIIR_c (0x6BD56736), ClimbingDynelFullUpdateIIR_c
	// (0x1FD6412B) and TombStoneFullUpdateIIR_c (0x0D80E7C9). They share the SimpleDynel shape:
	//   [u32 len][u32 sub][u32 typeWord][u32 handle][u8 env][u32 blobLen][transform blob][ geometry tail ]
	// The transform blob is the length-prefixed base transform (ProtoGameCodeDynel; the template name rides in the geometry tail);
	// the geometry tail (the FUN_0080c620 kd-tree/bounds struct + the type's trailing u32 scalars) is RDB geometry, so
	// it stays opaque here and is preserved verbatim. typeWord varies per dynel class (e.g. Door=0xC748,
	// TombStone=0xC87B) so it is read from the record, not hardcoded. Round-trip golden tests in
	// DynelFullUpdateCodecTests prove byte-exact reconstruction.
	public static class DynelFullUpdateCodec
	{
		public static byte[] Build(uint subOpcode, uint typeWord, uint handle, byte env, byte[] identityBlob, byte[] geometryTail)
		{
			var c = new FaolanStream().WriteUInt32(subOpcode).WriteUInt32(typeWord).WriteUInt32(handle).WriteByte(env).WriteArrayUInt32Length(identityBlob).WriteArray(geometryTail);
			return CharDCMoveCodec.WithLength(c.ToArray());
		}

		public static byte[] Build(Parsed p)
		{
			return Build(p.SubOpcode, p.TypeWord, p.Handle, p.Env, p.TransformBlob, p.GeometryTail);
		}

		public static Parsed Parse(byte[] record)
		{
			var s = new FaolanStream(record);
			s.ReadUInt32(); // len
			var sub = s.ReadUInt32();
			var type = s.ReadUInt32();
			var handle = s.ReadUInt32();
			var env = s.ReadByte();
			var blob = s.ReadArrayUInt32Length(); // [u32 blobLen][blob]
			var tail = s.ReadRemainingArray(); // kd-tree/bounds + trailing scalars (opaque RDB geometry)
			return new Parsed(sub, type, handle, env, blob, tail);
		}

		public sealed record Parsed(uint SubOpcode, uint TypeWord, uint Handle, byte Env, byte[] TransformBlob, byte[] GeometryTail);
	}
}
