using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Faolan.Core.Crypt;
using Faolan.Importer.Extractors;

namespace Faolan.Importer.Captures
{
	// Reads a Faolan-Wireshark tshark dump (`*.tshark.json`, produced by tools/capture/tshark_dump.sh) into the same
	// CapturedPacket list the analyzer-JSON reader produces. tshark is the AUTHORITATIVE capture source: it
	// TCP-reassembles, so it KEEPS the compressed download packets (conversation responses, world bursts) that the
	// C# PacketAnalyzer drops (~2/3 of compressed downloads). The dump's records carry the IIR body + the framing
	// fields (sender/receiver endpoints, crc), not the raw on-wire bytes, so we rebuild the exact on-wire packet
	// here - byte-exact, validated against the dump's own Crc32 (memory protocol_conversation_routing).
	//
	// On-wire packet = [u32 totalLen][u32 crc32][u32 headerLen][header][body], header =
	//   0x0a <vlq len> <sender endpoint> 0x12 <vlq len> <receiver endpoint> <opcode : u16 big-endian>
	// endpoint = 0x0d <class : u32 LITTLE-endian> 0x10 <instance : varint>   (instance omitted when absent)
	// crc32 = CRC32 over [headerLen][header][body].
	public static class TsharkCaptureReader
	{
		public static List<CapturedPacket> Read(string path)
		{
			var list = new List<CapturedPacket>();

			JsonDocument doc;
			try
			{
				doc = JsonDocument.Parse(File.ReadAllText(path));
			}
			catch
			{
				return list;
			}

			using (doc)
			{
				if (!doc.RootElement.TryGetProperty("Packets", out var packets) || packets.ValueKind != JsonValueKind.Array)
					return list;

				long seq = 0;
				foreach (var p in packets.EnumerateArray())
				{
					seq++;
					// Conan = the game/agent connections (the conversation lives here); skip the ChatServer chat stream.
					if (!p.TryGetProperty("Protocol", out var proto) || proto.GetString() != "Conan")
						continue;
					if (!p.TryGetProperty("BodyHex", out var bodyHex) || bodyHex.ValueKind != JsonValueKind.String)
						continue;

					byte[] body;
					try
					{
						body = Convert.FromHexString(bodyHex.GetString()!);
					}
					catch
					{
						continue;
					}

					var isDownload = p.TryGetProperty("IsDownload", out var d) && d.ValueKind == JsonValueKind.True;
					var opcode = ParseHex(p, "Opcode");
					// IIR sub-opcode = body[4:8] (after the IIR [u32 len][u32 subop] prefix).
					var subOpcode = body.Length >= 8
						? BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(4, 4))
						: 0u;

					// Real capture timestamp so packets sort in true wall-clock order ACROSS captures, not just within
					// one file. TimeUtc is an ISO-8601 string (e.g. "2026-07-04T09:12:42.741207819Z"), NOT a decimal
					// epoch - parse it as a DateTimeOffset and use its 100ns UtcTicks. Falls back to the per-file
					// sequence when absent/unparseable. (Used by CaptureCharacterStateImporter to take a character's
					// LATEST-in-time saved hotbar/wallet when it appears in several captures.)
					var tick = p.TryGetProperty("TimeUtc", out var tEl) && tEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tEl.GetString(), CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
						? timestamp.UtcTicks
						: seq;

					var full = Rebuild(p, body, opcode);
					if (full != null)
						list.Add(new CapturedPacket(tick, isDownload, opcode, subOpcode, full));
				}
			}

			return list;
		}

		private static uint ParseHex(JsonElement p, string key)
		{
			return p.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String && TryParseHexUInt(el.GetString(), out var v)
				? v
				: 0u;
		}

		// Rebuilds the exact on-wire packet from the dump's framing fields + body. Returns null if a required field
		// is missing/malformed (so a partial record is skipped rather than corrupting the stream).
		// internal so CorpusExtractWriter can store the FULL rebuilt packet, not just the body: the extractors
		// (QuestDefinitionExtractor, SnapshotJournalReader, ...) take CapturedPacket and strip the framing
		// themselves, so an extract holding only bodies cannot feed them.
		internal static byte[] Rebuild(JsonElement p, byte[] body, uint opcode)
		{
			if (!TryEndpoint(p, "SenderClass", "SenderInstance", out var sender))
				return null;
			if (!TryEndpoint(p, "ReceiverClass", "ReceiverInstance", out var receiver))
				return null;

			var header = new List<byte>
			{
				0x0a
			};
			header.AddRange(Varint((ulong)sender.Length));
			header.AddRange(sender);
			header.Add(0x12);
			header.AddRange(Varint((ulong)receiver.Length));
			header.AddRange(receiver);
			// The opcode is the u16 ConanPacket reads right after the receiver (header.ReadUInt16). The previous code
			// hardcoded 0x2000 here, so EVERY rebuilt packet reported opcode 0x2000 and consumers that key on the real
			// opcode (e.g. the 0x2052 ability-grant importer) saw nothing. Write the dump's actual opcode (big-endian).
			header.Add((byte)(opcode >> 8));
			header.Add((byte)(opcode & 0xFF));
			var headerBytes = header.ToArray();

			// payload = [u32 headerLen][header][body]; crc32 covers it; totalLen = crc + payload.
			var payload = new byte[4 + headerBytes.Length + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(0, 4), (uint)headerBytes.Length);
			headerBytes.CopyTo(payload, 4);
			body.CopyTo(payload, 4 + headerBytes.Length);

			var crc = Crc32.Calculate(payload);
			var full = new byte[8 + payload.Length];
			BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(0, 4), (uint)(4 + payload.Length));
			BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(4, 4), crc);
			payload.CopyTo(full, 8);
			return full;
		}

		// endpoint = 0x0d <class u32 LE> [0x10 <instance varint>]. Instance is omitted when the dump reports "None".
		private static bool TryEndpoint(JsonElement p, string classKey, string instanceKey, out byte[] endpoint)
		{
			endpoint = null;
			if (!p.TryGetProperty(classKey, out var clsEl) || clsEl.ValueKind != JsonValueKind.String)
				return false;
			if (!TryParseHexUInt(clsEl.GetString(), out var cls))
				return false;

			var bytes = new List<byte>
			{
				0x0d
			};
			var clsLe = new byte[4];
			BinaryPrimitives.WriteUInt32LittleEndian(clsLe, cls);
			bytes.AddRange(clsLe);

			if (p.TryGetProperty(instanceKey, out var instEl) && instEl.ValueKind == JsonValueKind.String && TryParseHexULong(instEl.GetString(), out var inst))
			{
				bytes.Add(0x10);
				bytes.AddRange(Varint(inst));
			}

			endpoint = bytes.ToArray();
			return true;
		}

		private static bool TryParseHexUInt(string s, out uint value)
		{
			value = 0;
			if (string.IsNullOrEmpty(s))
				return false;

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				s = s[2..];
			return uint.TryParse(s, NumberStyles.HexNumber, null, out value);
		}

		private static bool TryParseHexULong(string s, out ulong value)
		{
			value = 0;
			if (string.IsNullOrEmpty(s) || s == "None")
				return false;

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				s = s[2..];
			return ulong.TryParse(s, NumberStyles.HexNumber, null, out value);
		}

		private static IEnumerable<byte> Varint(ulong n)
		{
			do
			{
				var b = (byte)(n & 0x7f);
				n >>= 7;
				if (n != 0)
					b |= 0x80;
				yield return b;
			}
			while (n != 0);
		}
	}
}
