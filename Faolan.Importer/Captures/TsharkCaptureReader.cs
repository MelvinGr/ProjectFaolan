using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;
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
		// `includeAllProtocols` keeps the ChatServer stream as well as the Conan connections, for the timeline report -
		// a session's global/system chat is part of what happened. It is OPT-IN because every importer wants the
		// Conan-only set it has always had, and widening the default would change what the whole corpus imports.
		//
		// ⚠️ A ChatServer packet carries NO endpoints (no SenderClass/ReceiverClass/Crc32), so Rebuild cannot frame
		// it. Under the opt-in it is given a SYNTHETIC frame - [len][crc 0][headerLen 0][body] - so `.Body` returns
		// its payload like any other packet. That frame is not byte-exact and is not meant to be: `Protocol` is what
		// tells a consumer which packets have real framing.
		public static List<CapturedPacket> Read(string path, bool includeAllProtocols = false)
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
					// Conan = the game/agent connections (the conversation lives here); the ChatServer chat stream is
					// skipped unless the caller asked for everything.
					var protocol = p.TryGetProperty("Protocol", out var proto)
						? proto.GetString() ?? ""
						: "";
					if (protocol != "Conan" && !includeAllProtocols)
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
						? body.ReadU32Be(4)
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

					var full = Rebuild(p, body, opcode) ?? (includeAllProtocols ? Synthesise(body) : null);
					if (full == null)
						continue;

					var direction = p.TryGetProperty("Direction", out var dir)
						? dir.GetString() ?? ""
						: "";
					list.Add(new CapturedPacket(tick, isDownload, opcode, subOpcode, full,
						p.TryGetProperty("Frame", out var fr) && fr.TryGetInt32(out var frame) ? frame : 0,
						direction, PortOf(p, direction), protocol, ParseHex(p, "SenderClass"), ParseHex(p, "ReceiverClass")));
				}
			}

			return list;
		}

		// The GAME side's port, which is what names the service tier (7010 universe, 7011 player-agent, 7012 CS,
		// 7001 chat, 7017 game). On a c2s packet that is the DESTINATION, on s2c the SOURCE.
		private static int PortOf(JsonElement p, string direction)
		{
			var end = direction == "c2s" ? "Destination" : "Source";
			return p.TryGetProperty(end, out var e) && e.TryGetProperty("Port", out var port) && port.TryGetInt32(out var value)
				? value
				: 0;
		}

		// A minimal frame for a packet with no endpoints (the ChatServer stream): [len][crc 0][headerLen 0][body], so
		// CapturedPacket.Body returns the payload. Deliberately NOT byte-exact - there is no header to reproduce.
		private static byte[] Synthesise(byte[] body)
		{
			var full = new byte[12 + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(full, (uint)full.Length);
			body.CopyTo(full, 12);
			return full;
		}

		// Rebuilds the exact on-wire packet from the dump's framing fields + body. Returns null if a required field
		// is missing/malformed (so a partial record is skipped rather than corrupting the stream).
		// internal so FixtureArchiveWriter can store the FULL rebuilt packet, not just the body: the extractors
		// (QuestDefinitionExtractor, SnapshotJournalReader, ...) take CapturedPacket and strip the framing
		// themselves, so an extract holding only bodies cannot feed them.
		internal static byte[]? Rebuild(JsonElement p, byte[] body, uint opcode)
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
			// The opcode is the u16 Rpc.Parse reads right after the receiver (header.ReadUInt16). The previous code
			// hardcoded 0x2000 here, so EVERY rebuilt packet reported opcode 0x2000 and consumers that key on the real
			// opcode (e.g. the 0x2052 ability-grant importer) saw nothing. Write the dump's actual opcode (big-endian).
			header.Add((byte)(opcode >> 8));
			header.Add((byte)(opcode & 0xFF));
			var headerBytes = header.ToArray();

			// payload = [u32 headerLen][header][body]; crc32 covers it; totalLen = crc + payload.
			var payload = new byte[4 + headerBytes.Length + body.Length];
			payload.WriteU32Be(0, (uint)headerBytes.Length);
			headerBytes.CopyTo(payload, 4);
			body.CopyTo(payload, 4 + headerBytes.Length);

			var crc = Crc32.Calculate(payload);
			var full = new byte[8 + payload.Length];
			full.WriteU32Be(0, (uint)(4 + payload.Length));
			full.WriteU32Be(4, crc);
			payload.CopyTo(full, 8);
			return full;
		}

		private static uint ParseHex(JsonElement p, string key)
		{
			return p.TryGetProperty(key, out var el) && el.ValueKind == JsonValueKind.String && TryParseHexUInt(el.GetString(), out var v)
				? v
				: 0u;
		}

		// endpoint = 0x0d <class u32 LE> [0x10 <instance varint>]. Instance is omitted when the dump reports "None".
		private static bool TryEndpoint(JsonElement p, string classKey, string instanceKey, out byte[] endpoint)
		{
			endpoint = null!;
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

		private static bool TryParseHexUInt(string? s, out uint value)
		{
			value = 0;
			if (string.IsNullOrEmpty(s))
				return false;

			if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
				s = s[2..];
			return uint.TryParse(s, NumberStyles.HexNumber, null, out value);
		}

		private static bool TryParseHexULong(string? s, out ulong value)
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
