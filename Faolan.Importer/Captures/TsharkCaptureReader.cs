using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using Faolan.Core.Crypt;
using Faolan.Core.Extensions;

namespace Faolan.Importer.Captures
{
	/// <summary>
	///     Reads a Faolan-Wireshark tshark dump (<c>*.tshark.json</c>, produced by tools/capture/tshark_dump.sh) into
	///     a <see cref="CapturedPacket" /> list. This is the authoritative capture source: it is TCP-reassembled, so
	///     it keeps the compressed download packets (conversation responses, world bursts).
	/// </summary>
	/// <remarks>
	///     The dump's records carry the IIR body plus the framing fields (sender/receiver endpoints, crc), not the raw
	///     on-wire bytes, so the exact on-wire packet is rebuilt here and validated against the dump's own Crc32.
	///     On-wire packet = [u32 totalLen][u32 crc32][u32 headerLen][header][body], where header is
	///     <c>0x0a &lt;vlq len&gt; &lt;sender endpoint&gt; 0x12 &lt;vlq len&gt; &lt;receiver endpoint&gt; 0x20
	///     &lt;method_instance: varint&gt;</c>, an endpoint is
	///     <c>0x0d &lt;class: u32 little-endian&gt; 0x10 &lt;instance: varint&gt;</c> (the instance is omitted when
	///     absent), and crc32 covers [headerLen][header][body].
	/// </remarks>
	public static class TsharkCaptureReader
	{
		/// <param name="includeAllProtocols">
		///     Keeps the ChatServer stream as well as the Conan connections, for the timeline report - a session's
		///     global and system chat is part of what happened. It is opt-in because the importers want the Conan-only
		///     set, and widening the default would change what the whole corpus imports.
		///     A ChatServer packet carries no endpoints (no SenderClass/ReceiverClass/Crc32), so
		///     <see cref="Rebuild" /> cannot frame it; under the opt-in it is given a synthetic frame
		///     ([len][crc 0][headerLen 0][body]) so that <c>.Body</c> returns its payload like any other packet. That
		///     frame is deliberately not byte-exact: <c>Protocol</c> is what tells a consumer which packets have real
		///     framing.
		/// </param>
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

					// Real capture timestamp so packets sort in true wall-clock order across captures, not just within
					// one file. TimeUtc is an ISO-8601 string (e.g. "2026-07-04T09:12:42.741207819Z"), not a decimal
					// epoch - parse it as a DateTimeOffset and use its 100ns UtcTicks, falling back to the per-file
					// sequence when absent or unparseable. CaptureCharacterStateImporter uses it to take a character's
					// latest-in-time saved hotbar/wallet when it appears in several captures.
					var tick = p.TryGetProperty("TimeUtc", out var tEl) && tEl.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(tEl.GetString(), CultureInfo.InvariantCulture,
						DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var timestamp)
						? timestamp.UtcTicks
						: seq;

					var methodInstance = ParseHex(p, "MethodId");
					var full = Rebuild(p, body, opcode, methodInstance) ?? (includeAllProtocols ? Synthesise(body) : null);
					if (full == null)
						continue;

					var direction = p.TryGetProperty("Direction", out var dir)
						? dir.GetString() ?? ""
						: "";
					list.Add(new CapturedPacket(tick, isDownload, opcode, subOpcode, full,
						p.TryGetProperty("Frame", out var fr) && fr.TryGetInt32(out var frame) ? frame : 0,
						direction, PortOf(p, direction), protocol, ParseHex(p, "SenderClass"), ParseHex(p, "ReceiverClass"), methodInstance,
						// The TCP stream, i.e. which connection carried this. It is the only thing that can tell a
						// broadcast from a private reply: with two clients on the wire, a record reaching both streams
						// is a broadcast (`--report broadcast-check`). `Port` cannot answer that - it is the server
						// port, identical for every client on a tier.
						p.TryGetProperty("Stream", out var st) && st.TryGetInt32(out var stream) ? stream : -1));
				}
			}

			return list;
		}

		/// <summary>
		///     The game side's port, which names the service tier (7010 universe, 7011 player-agent, 7012 CS, 7001
		///     chat, 7017 game). On a c2s packet that is the destination, on s2c the source.
		/// </summary>
		private static int PortOf(JsonElement p, string direction)
		{
			var end = direction == "c2s" ? "Destination" : "Source";
			return p.TryGetProperty(end, out var e) && e.TryGetProperty("Port", out var port) && port.TryGetInt32(out var value)
				? value
				: 0;
		}

		/// <summary>
		///     A minimal frame for a packet with no endpoints (the ChatServer stream): [len][crc 0][headerLen 0][body],
		///     so <c>CapturedPacket.Body</c> returns the payload. Deliberately not byte-exact - there is no header to
		///     reproduce.
		/// </summary>
		private static byte[] Synthesise(byte[] body)
		{
			var full = new byte[12 + body.Length];
			BinaryPrimitives.WriteUInt32BigEndian(full, (uint)full.Length);
			body.CopyTo(full, 12);
			return full;
		}

		/// <summary>
		///     Rebuilds the exact on-wire packet from the dump's framing fields and body.
		/// </summary>
		/// <returns>
		///     The framed packet, or null if a required field is missing or malformed, so a partial record is skipped
		///     rather than corrupting the stream.
		/// </returns>
		/// <remarks>
		///     Internal so <see cref="FixtureArchiveWriter" /> can store the full rebuilt packet rather than the body:
		///     the extractors (QuestDefinitionExtractor, SnapshotJournalReader,...) take a CapturedPacket and strip
		///     the framing themselves, so an extract holding only bodies cannot feed them.
		/// </remarks>
		internal static byte[]? Rebuild(JsonElement p, byte[] body, uint opcode, uint methodInstance = 0)
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
			// `method_instance` is field 4: the tag 0x20 followed by a varint, not a u16. The dump's two-byte "Opcode"
			// is a lossy rendering of it - the tag plus the varint's first group only (see CapturedPacket.WireOpcode) -
			// so writing that verbatim ends the header on a byte with the continuation bit set and nothing after it
			// whenever a method's constant needs more than seven bits, and Rpc.Parse rejects the frame with "RpcHeader
			// ended mid-field". The dump records the full constant as `MethodId` (0x9cb2cb03 for an Authenticate whose
			// opcode renders as 0x2083 - 0x03 being its low seven bits), so the varint is written from that.
			header.Add(0x20);
			if (methodInstance != 0)
			{
				header.AddRange(Varint(methodInstance));
			}
			else
			{
				// No MethodId in this dump - write the single group the Opcode carries rather than inventing a
				// constant. A frame that then cannot parse shows up in `--report frame-health`.
				header.Add((byte)(opcode & 0xFF));
			}
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

		/// <summary>
		///     Builds an endpoint: <c>0x0d &lt;class u32 LE&gt; [0x10 &lt;instance varint&gt;]</c>. The instance is
		///     omitted when the dump reports "None".
		/// </summary>
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
