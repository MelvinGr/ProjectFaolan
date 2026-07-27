using System;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Faolan.Core.Crypt;

namespace Faolan.Importer.Captures
{
	// Hunts the per-template TEA keys for the ENCRYPTED feat records (Gitea #251).
	//
	// RDB type 1000020 holds items and feats together; ~79% are `RDBH` TEA-encrypted and the key is per-template. For
	// ITEMS the key rides the wire - the server streams it as the item's ProtoDecryptionKey and we store it in
	// Item.Key - so item templates decrypt. For FEATS no such carrier is known, which is why the catalog covers only
	// the plaintext ~21%.
	//
	// The hypothesis this tests (#251's own plan): the feat key rides the UNLOCK/TRAIN event, the way an item's key
	// arrives when the item is acquired rather than on every login. An earlier attempt scanned only already-specced
	// characters' LOGIN captures - i.e. exactly where the plan predicted the key would NOT be - and found nothing.
	//
	// Method: treat every 16-byte window in every captured packet body as a candidate key and TEA-decrypt the target
	// record's FIRST BLOCK ONLY. That block is decisive and cheap: a plaintext feat record opens
	// [u32 type 1000020][u32 id], so a correct key yields exactly those 8 bytes and a wrong one effectively
	// never will. One 8-byte decrypt per candidate rather than a whole record.
	public static class FeatKeyScanner
	{
		// A decrypted template opens with its own RDB TYPE word - 1000020, which renders as "TB\x0f\x00" in a dump
		// but is not a magic. That plus the id is the decisive two-word oracle for a correct key.
		private const uint TemplateTypeWord = 1000020;

		// One target: the encrypted record we are trying to open, and the id its first block must reveal.
		public readonly record struct Target(uint TemplateId, byte[] FirstBlock);

		public readonly record struct Hit(uint TemplateId, uint[] Key, string Source, int Offset);

		// Scans `captureFiles` for a key that opens any of `targets`. Windows are BYTE-aligned, not word-aligned: a
		// key need not sit on a 4-byte boundary within a packet body, and assuming it does is the kind of shortcut
		// that makes a negative result meaningless.
		public static List<Hit> Scan(IEnumerable<string> captureFiles, IReadOnlyList<Target> targets, Action<string>? log = null)
		{
			var hits = new ConcurrentBag<Hit>();
			var files = captureFiles.ToList();
			var scanned = 0L;

			foreach (var path in files)
			{
				var bodies = ReadBodies(path);
				long windows = 0;
				Parallel.ForEach(bodies, body =>
				{
					if (body.Length < 16)
						return;

					var local = 0L;
					for (var i = 0; i + 16 <= body.Length; i++)
					{
						local++;
						Span<uint> key =
						[
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 4)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 8)),
							BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(i + 12))
						];

						foreach (var target in targets)
						{
							var v0 = BinaryPrimitives.ReadUInt32LittleEndian(target.FirstBlock);
							var v1 = BinaryPrimitives.ReadUInt32LittleEndian(target.FirstBlock.AsSpan(4));
							Tea.DecryptBlock(ref v0, ref v1, key);
							if (v0 != TemplateTypeWord || v1 != target.TemplateId)
								continue;

							hits.Add(new Hit(target.TemplateId, [key[0], key[1], key[2], key[3]], Path.GetFileName(path), i));
						}
					}

					System.Threading.Interlocked.Add(ref windows, local);
				});

				scanned += windows;
				log?.Invoke($"  {Path.GetFileName(path)}: {windows:N0} windows (running total {scanned:N0}){(hits.IsEmpty ? "" : $"  ** {hits.Count} HIT(S) **")}");
			}

			return [.. hits];
		}

		// Every packet body in a tshark dump, as raw bytes. Deliberately NOT filtered to the Conan protocol: a key
		// could ride any stream, and filtering is how the previous sweep could have missed it.
		private static List<byte[]> ReadBodies(string path)
		{
			var list = new List<byte[]>();
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

				foreach (var p in packets.EnumerateArray())
				{
					if (!p.TryGetProperty("BodyHex", out var hex) || hex.ValueKind != JsonValueKind.String)
						continue;

					try
					{
						list.Add(Convert.FromHexString(hex.GetString()!));
					}
					catch
					{
						// a body we cannot parse is one we cannot scan - skip it rather than abort the file
					}
				}
			}

			return list;
		}
	}
}
