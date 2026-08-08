using System;
using System.Collections.Generic;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;

namespace Faolan.Core.Network.Codecs.World
{
	// `DispatchInstanceSelectionForCurrentPlayfield` (GameCharInterface 0x80) - the answer to the client's
	// `GetInstanceListForCurrentPlayfield` (GameCharAgent 0x7B, an EMPTY body), i.e. what fills the "Select instance"
	// window (#309).
	//
	//   [u32 count][PlayfieldProxy x count]
	//
	// ⚠️ The count is a PLAIN u32, not the `(n+1) * 0x3F1` list frame most of our lists use. That is the client's own
	// reader, not an inference: `FUN_00a6d1b0` reads one big-endian word through `FUN_0048f900` and loops it as a
	// countdown, with no multiply or divide anywhere near it. A one-element list here really is `00000001`, where a
	// framed list would have been `000007E2`.
	//
	// Each element is the ordinary `PlayfieldProxy` stream (`FUN_00a6a600`, version byte 'b') already composed by the
	// teleport records, the team-member record and PlayfieldFIIR - so an instance in this list carries its map, its
	// owner (nobody / a character / a team) and its difficulty mode in exactly the slots those do. The client's own
	// name for the parameter is `i_cInstanceList`.
	//
	// Goldens: TWO captured replies, each a one-element list naming the live instance the player was standing in -
	// Ymir's Pass (map 1090) and Poitain (map 2070). Both are 38 bytes, and the second is annotated "tried to
	// 'select instace', no others where available", which is what a one-element list means. Two different maps and
	// two different tokens pin the layout by independent observation rather than by one sample.
	// [V2: NecroL80_mage-necromancer_login_gameplay_chat-annotated-4.tshark.json frame 10726]
	// [V2: NecroL80_mage-necromancer_login_gameplay_annotated_6.tshark.json frame 4059]
	public static class InstanceSelectionCodec
	{
		public const uint Method = (uint)GameCharInterfaceMethods.DispatchInstanceSelectionForCurrentPlayfield;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(InstanceSelection value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			s.WriteUInt32((uint)value.Instances.Count);
			foreach (var instance in value.Instances)
				instance.Write(s);

			return s.ToArray();
		}

		public static byte[] Build(IEnumerable<PlayfieldProxy> instances)
		{
			return Build(new InstanceSelection([.. instances]));
		}

		public static InstanceSelection Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(InstanceSelectionCodec));
			var count = r.ReadU32Be();
			var instances = new List<PlayfieldProxy>((int)count);
			for (var i = 0; i < count; i++)
				instances.Add(PlayfieldProxy.Read(ref r));

			r.EnsureAtEnd();
			return new InstanceSelection(instances);
		}
	}

	// The instances of the player's current playfield they may pick between. Empty is a legitimate answer and is what
	// an uninstanced playfield yields - the client opens the window with nothing in it rather than stalling.
	public sealed record InstanceSelection(IReadOnlyList<PlayfieldProxy> Instances);

	// The forms this list takes, beside the codec rather than as `Build` overloads (#530 rule 5a).
	public static class InstanceSelections
	{
		// The only shape a capture pins: the one live instance the player is standing in. Retail answered the click
		// with a single pinned proxy for the current map, so a server that hosts one copy of each playfield says
		// exactly this and nothing more.
		public static InstanceSelection Current(uint mapId, uint instanceToken)
		{
			return new InstanceSelection([PlayfieldProxy.ForInstance(mapId, instanceToken)]);
		}

		public static InstanceSelection None()
		{
			return new InstanceSelection([]);
		}
	}
}
