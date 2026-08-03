using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.PlayerAgent
{
	// The client's `IOBuffer` as it rides the PlayerAgent settings methods - the payload of UpdateSubscriptionSettings,
	// UpdateCharacterSettings, UpdateCharacterPreview and their s2c twins (SendCharacterSettings / SendCharacterPreview).
	// Decoded in Gitea #611 from the three captured mid-session uploads.
	//
	//   [u32 byteLength][ "IOz1" ][u32 inflatedSize, LITTLE-endian][zlib stream]
	//
	// `byteLength` counts everything after it, so a length of 0 is a legitimately EMPTY buffer and no marker follows -
	// which is what retail sends for a character that has never uploaded anything (`SendCharacterSettings` is 12 bytes:
	// the ID32 plus a zero length, x118 in the corpus). `IOz1` is the client's compressed-buffer marker and the size
	// after it is the inflated length; both are little-endian because this is a client-side file format that happens to
	// be shipped over a big-endian wire, not a wire record.
	//
	// Inflated, the buffer is a FILE ARCHIVE - the client's own settings files, concatenated:
	//
	//   repeated [u32 nameLength][name, LATIN1][u32 dataLength][data]      (both lengths little-endian)
	//
	// Observed members: `hotkeys.xml`, `Prefs_2.xml` (subscription = account-wide), `Chat\Windows.xml`,
	// `DockAreas\SideBarLeft.xml`, `DockAreas\SideBarRight.xml`, `Prefs_2.xml` (per character) and `preview.bin` (the
	// character-select preview - a binary appearance record, not decoded here).
	//
	// ⚠️ The value carries the COMPRESSED payload verbatim, and `Build` writes those bytes back rather than
	// recompressing. That is not laziness: a deflate stream is not a function of its contents (level, window and
	// implementation all move it), so re-deflating would change the bytes - and the checksum the client sends alongside
	// is an MD5 over exactly these bytes (`CheckSum`), so the server can only reproduce it by storing them. `Files` is
	// therefore a decoded VIEW of `Payload`, not its source.
	public static class ClientSettingsBufferCodec
	{
		// The compressed-buffer marker "IOz1", ASCII, immediately before the inflated size.
		private const uint Marker = 0x494F7A31;

		// CANONICAL - the only method here that writes bytes. The u32 length belongs to the composing message, so this
		// is the payload alone: exactly what the server stores, and what `Parse` takes back.
		public static byte[] Build(ClientSettingsBuffer value)
		{
			ArgumentNullException.ThrowIfNull(value);

			return value.Payload;
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

		// The archive INSIDE the buffer: inflate, then walk the name/data pairs. Strict like any Parse - a truncated
		// entry or a trailing byte is a decode failure, not a shorter file list.
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

	// One IOBuffer: the payload exactly as it went over the wire, plus the files it inflates to.
	public sealed record ClientSettingsBuffer(byte[] Payload, IReadOnlyList<ClientFile> Files)
	{
		public static readonly ClientSettingsBuffer Empty = new([], []);

		public bool IsEmpty => Payload.Length == 0;

		// The MD5 the client sends alongside an upload and the server hands back in SendCharacterSettingsCheckSumMap:
		// 32 lowercase hex chars over the COMPRESSED payload (marker and size included). Verified against all three
		// captured uploads that carry one.
		public string CheckSum => IsEmpty ? "" : Convert.ToHexStringLower(MD5.HashData(Payload));
	}

	// One file in the archive. `Name` is a client-relative path and uses BACKSLASHES (`Chat\Windows.xml`).
	public readonly record struct ClientFile(string Name, byte[] Data);
}
