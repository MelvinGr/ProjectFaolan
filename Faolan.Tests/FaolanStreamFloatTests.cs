using Faolan.Core.Data;
using Xunit;

namespace Faolan.Tests
{
	// Locks in the deliberate float-endianness contract (see FaolanStream / docs note + protocol_float_endianness):
	// protobuf fixed32 floats are LITTLE-endian (WriteFloat), raw packet-body floats are BIG-endian
	// (WriteFloatBigEndian / ReadFloat). The WriteFloat(LE) vs ReadFloat(BE) asymmetry is intentional - this guards
	// against "fixing" it back into a single endianness (which corrupts one of the two contexts).
	public class FaolanStreamFloatTests
	{
		[Fact]
		public void WriteFloat_IsLittleEndian()
		{
			var bytes = new FaolanStream().Also(s => s.WriteFloat(1.0f)).ToArray();
			// 1.0f = 0x3F800000; little-endian on the wire.
			Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, bytes);
		}

		[Fact]
		public void WriteFloatBigEndian_IsBigEndian_AndRoundTripsWithReadFloat()
		{
			var bytes = new FaolanStream().Also(s => s.WriteFloatBigEndian(1.0f)).ToArray();
			Assert.Equal(new byte[] { 0x3F, 0x80, 0x00, 0x00 }, bytes);

			// ReadFloat is big-endian, so it round-trips a big-endian write.
			Assert.Equal(1.0f, new FaolanStream(bytes).ReadFloat());
		}

		[Fact]
		public void ReadFloat_DecodesCapturedBigEndianPosition()
		{
			// MapChange X position from the retail capture: bytes 44 78 D4 6A decode big-endian to 995.319.
			var x = new FaolanStream(new byte[] { 0x44, 0x78, 0xD4, 0x6A }).ReadFloat();
			Assert.Equal(995.319f, x, 3);
		}

		[Fact]
		public void WriteVector3BigEndian_WritesThreeBigEndianFloats()
		{
			var bytes = new FaolanStream().Also(s => s.WriteVector3BigEndian(new System.Numerics.Vector3(1.0f, 2.0f, 0.0f))).ToArray();
			// 1.0=0x3F800000, 2.0=0x40000000, 0.0=0x00000000, all big-endian.
			Assert.Equal(new byte[] { 0x3F, 0x80, 0x00, 0x00, 0x40, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 }, bytes);
		}
	}

	internal static class FaolanStreamTestExtensions
	{
		// Small helper so a write can be expressed inline (FaolanStream's writers return the stream, but the ctor
		// chain reads cleaner with this).
		public static FaolanStream Also(this FaolanStream stream, System.Action<FaolanStream> write)
		{
			write(stream);
			return stream;
		}
	}
}
