using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	/// <summary>
	///     The client's <c>IOBuffer</c> as it rides the PlayerAgent settings methods - the payload of
	///     <c>UpdateSubscriptionSettings</c>, <c>UpdateCharacterSettings</c>, <c>UpdateCharacterPreview</c> and their s2c
	///     twins (<c>SendCharacterSettings</c> / <c>SendCharacterPreview</c>).
	/// </summary>
	/// <remarks>
	///     <code>
	///     [u32 byteLength][ "IOz1" ][u32 inflatedSize, little-endian][zlib stream]
	///     </code>
	///     <para>
	///         <c>byteLength</c> counts everything after it, so a length of 0 is a legitimately empty buffer with no
	///         marker following - which is what retail sends for a character that has never uploaded anything
	///         (<c>SendCharacterSettings</c> is then 12 bytes: the ID32 plus a zero length, 118 times in the corpus).
	///         <c>IOz1</c> is the client's compressed-buffer marker and the size after it is the inflated length; both are
	///         little-endian, because this is a client-side file format that happens to be shipped over a big-endian wire.
	///     </para>
	///     <para>
	///         Inflated, the buffer is a file archive - the client's own settings files, concatenated as
	///         <c>repeated [u32 nameLength][name, Latin1][u32 dataLength][data]</c>, both lengths little-endian. Observed
	///         members: <c>hotkeys.xml</c>, an account-wide <c>Prefs_2.xml</c>, <c>Chat\Windows.xml</c>,
	///         <c>DockAreas\SideBarLeft.xml</c>, <c>DockAreas\SideBarRight.xml</c>, a per-character <c>Prefs_2.xml</c>
	///         and <c>preview.bin</c>.
	///     </para>
	///     <para>
	///         The value carries the compressed payload verbatim, and <c>Build</c> writes those bytes back rather than
	///         recompressing. A deflate stream is not a function of its contents - level, window and implementation all
	///         move it - so re-deflating would change the bytes, and the checksum the client sends alongside is an MD5
	///         over exactly these bytes. <c>Files</c> is therefore a decoded view of <c>Payload</c>, not its source.
	///     </para>
	/// </remarks>
	public static class ClientSettingsBufferCodec
	{
		// The compressed-buffer marker "IOz1", ASCII, immediately before the inflated size.
		private const uint Marker = 0x494F7A31;

		// Canonical: the only method here that writes bytes. The u32 length belongs to the composing message, so this is
		// the payload alone - exactly what the server stores, and what `Parse` takes back.
		public static byte[] Build(ClientSettingsBuffer value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value.Payload;
		}

		// The archive authoring form: pack the members and deflate them into a fresh buffer. Retail never needs this, since
		// the client authors every archive the server holds, but a preview constructed here has to become one.
		//
		// The bytes are not retail's: a deflate stream is not a function of its contents, so the checksum of a packed buffer
		// will not match the one the client would have computed for the same files. That is fine for a buffer authored here
		// and would be wrong for one merely relayed, which is why Build(ClientSettingsBuffer) carries the payload verbatim.
		public static byte[] Build(IReadOnlyList<ClientFile> files)
		{
			ArgumentNullException.ThrowIfNull(files);

			return Build(new ClientSettingsBuffer(Pack(files), files));
		}

		public static ClientSettingsBuffer Parse(in ReadOnlySpan<byte> data)
		{
			return data.Length == 0 ? ClientSettingsBuffer.Empty : new ClientSettingsBuffer(data.ToArray(), Unpack(data));
		}

		internal static ClientSettingsBuffer Read(ref SpanReader r)
		{
			return Parse(r.ReadBytesU32Length());
		}

		internal static void Write(FaolanStream s, ClientSettingsBuffer v)
		{
			s.WriteArrayUInt32Length(Build(v));
		}

		// The archive inside the buffer: inflate, then walk the name/data pairs. Strict like any Parse - a truncated entry
		// or a trailing byte is a decode failure, not a shorter file list.
		private static IReadOnlyList<ClientFile> Unpack(in ReadOnlySpan<byte> payload)
		{
			var r = new SpanReader(payload, nameof(ClientSettingsBufferCodec));
			r.ExpectU32Be(Marker, "the IOz1 compressed-buffer marker");
			var inflatedSize = checked((int)r.ReadU32Le());

			var inflated = Inflate(r.ReadRemaining(), inflatedSize);

			var files = new List<ClientFile>();
			var contents = new SpanReader(inflated, nameof(ClientSettingsBufferCodec));
			while (!contents.AtEnd)
			{
				var name = Encoding.Latin1.GetString(contents.ReadBytes(checked((int)contents.ReadU32Le())));
				files.Add(new ClientFile(name, contents.ReadBytes(checked((int)contents.ReadU32Le())).ToArray()));
			}

			return files;
		}

		// The inverse of Unpack and Inflate: concatenate the name/data pairs, deflate them, and put the marker and the
		// inflated size in front. Private because it is not an encoding of the value type - Build(ClientSettingsBuffer) stays
		// the one method that turns a buffer into bytes, and this is what gives that buffer its payload.
		private static byte[] Pack(IReadOnlyList<ClientFile> files)
		{
			using var contents = new FaolanStream();
			foreach (var file in files)
			{
				var name = Encoding.Latin1.GetBytes(file.Name);
				contents.WriteUInt32LittleEndian((uint)name.Length).WriteArray(name).WriteUInt32LittleEndian((uint)file.Data.Length).WriteArray(file.Data);
			}

			var inflated = contents.ToArray();
			using var deflated = new MemoryStream();
			using (var zlib = new ZLibStream(deflated, CompressionLevel.Optimal, true))
			{
				zlib.Write(inflated);
			}

			using var payload = new FaolanStream();

			return payload.WriteUInt32(Marker).WriteUInt32LittleEndian((uint)inflated.Length).WriteArray(deflated.ToArray()).ToArray();
		}

		private static byte[] Inflate(in ReadOnlySpan<byte> deflated, int inflatedSize)
		{
			var inflated = new byte[inflatedSize];
			using var input = new MemoryStream(deflated.ToArray(), false);
			using var zlib = new ZLibStream(input, CompressionMode.Decompress);

			var read = zlib.ReadAtLeast(inflated, inflatedSize, false);
			if (read != inflatedSize || zlib.ReadByte() >= 0)
				throw CodecParseException.Unexpected(nameof(ClientSettingsBufferCodec), 0, $"{inflatedSize} inflated bytes", $"{read}");

			return inflated;
		}
	}

	/// <summary>One IOBuffer: the payload exactly as it went over the wire, plus the files it inflates to.</summary>
	public sealed record ClientSettingsBuffer(byte[] Payload, IReadOnlyList<ClientFile> Files)
	{
		public static readonly ClientSettingsBuffer Empty = new ClientSettingsBuffer([], []);

		public bool IsEmpty => Payload.Length == 0;

		/// <summary>
		///     The MD5 the client sends alongside an upload and the server hands back in
		///     <c>SendCharacterSettingsCheckSumMap</c>: 32 lowercase hex characters over the compressed payload, marker and
		///     size included. Verified against all three captured uploads that carry one.
		/// </summary>
		public string CheckSum => IsEmpty ? "" : Convert.ToHexStringLower(MD5.HashData(Payload));
	}

	/// <summary>One file in the archive. <c>Name</c> is a client-relative path and uses backslashes.</summary>
	public readonly record struct ClientFile(string Name, byte[] Data);
}
