using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Reflection;
using Faolan.Core.Database.Tables;

namespace Faolan.GameServer.Packets.FullCharSnapshot
{
	// Builds the beach 0x737A6DF9 snapshot TAILS from a SERVER-OWNED template asset (BeachTailsTemplate.bin, the
	// content-fixed regions: the PFAccessWindow_ table, the fresh-character record skeleton, the format scaffolding)
	// plus the live character's GENERATED fields (level, vitals/XP). This decouples the snapshot tails from the
	// captured world-burst packet - the server now owns and assembles them - and is the first step of constructing
	// the snapshot from the ground up (the prefix is built by BeachSnapshotPrefix, the root by FullCharSnapshotBuilder).
	//
	// What is GENERATED today: Level, current HP/Mana/Stamina, current XP (the per-character stat fields the client
	// renders). What is still TEMPLATE (content-fixed or not-yet-decoded, see [[snapshot_tails_construction_scope]]):
	// the access-window table, the per-character name-format XML, and the UnknownBefore progression record block.
	// For a fresh beach character these are the same fixed shape, so the template is correct; growing them into
	// generated output is the remaining decode work.
	public static class BeachTailsBuilder
	{
		// The captured instance baked into the template (Strie's); substituted for the live character's instance so
		// the per-session entity references in the tails address the logged-in player (as the burst path re-addresses).
		private const uint TemplateInstance = 0x02165EFF;

		private static readonly byte[] Template = LoadTemplate();

		// True when the server-owned template is available (it always is once embedded); guards the caller's fallback.
		public static bool IsAvailable => Template.Length > 0;

		// Assembles the beach tails for a character: the template with the live level + vitals patched in. Byte-
		// identical to the captured beach tails when given the captured character's values (BeachSnapshotConstruction
		// / golden test).
		public static byte[] Build(Character character)
		{
			return Build(character, TemplateInstance);
		}

		// As Build(character), but re-addresses the template's captured instance to `instance` (the live character's
		// client instance) in the assembled tails - the tails carry raw big-endian entity references.
		public static byte[] Build(Character character, uint instance)
		{
			var tails = BigboyTailsFile.Deserialize(Template);

			if (character != null)
			{
				tails.Level = character.Level;
				if (tails.HasKnownRepeatedStatTable && character.MaxStats != null)
				{
					tails.PostAccessBlock!.CurrentXp = character.Experience;
					tails.PostAccessBlock.CurrentHp = character.MaxStats.Health;
					tails.PostAccessBlock.CurrentMana = character.MaxStats.Mana;
					tails.PostAccessBlock.CurrentStamina = character.MaxStats.Stamina;
				}
			}

			var bytes = tails.ToArray();
			if (instance != TemplateInstance)
				ReplaceRawUInt32(bytes, TemplateInstance, instance);

			return bytes;
		}

		// Replaces every big-endian uint32 occurrence of `find` with `replace` (the tails are raw records, not
		// protobuf, so entity references appear as raw 4-byte big-endian values).
		private static void ReplaceRawUInt32(byte[] data, uint find, uint replace)
		{
			Span<byte> needle = stackalloc byte[4];
			BinaryPrimitives.WriteUInt32BigEndian(needle, find);
			for (var i = 0; i + 4 <= data.Length; i++)
			{
				if (data[i] == needle[0] && data[i + 1] == needle[1] && data[i + 2] == needle[2] && data[i + 3] == needle[3])
				{
					BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(i, 4), replace);
					i += 3;
				}
			}
		}

		private static byte[] LoadTemplate()
		{
			var assembly = Assembly.GetExecutingAssembly();
			var name = assembly.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("BeachTailsTemplate.bin", StringComparison.Ordinal));
			if (name == null)
				return [];

			using var stream = assembly.GetManifestResourceStream(name);
			if (stream == null)
				return [];

			using var ms = new MemoryStream();
			stream.CopyTo(ms);
			return ms.ToArray();
		}
	}
}
