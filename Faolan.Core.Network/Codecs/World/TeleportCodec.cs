using System;
using System.Numerics;
using Faolan.Core.Enums;
using Faolan.Core.Extensions;
using Faolan.Core.Network.Constants;

namespace Faolan.Core.Network.Codecs.World
{
	// The change-map / teleport prep records the retail server sends before the 0x2002 handoff (#219), decoded
	// byte-for-byte from the changemap captures (frames 4200/4212 map 2010, 4417/4420 map 2000) against the IIR
	// readers recovered in #233 (full reader walk in docs/subsystems/teleport-changemap.md):
	//   - IntendedTeleportIIR_c (0x10D27BC0, "begin teleport to map N")  -> reader FUN_005bfce0
	//   - n3TeleportIIR_t       (0x5A32F0D7, "destination map + position") -> reader FUN_005d1ae0
	// Both embed a PlayfieldProxy stream (FUN_00a6a600): `[u8 version='b'(0x62)][u32 x8][u8 bool]`, where u32[0] is
	// a playfield-class word and u32[1] the destination map. The "begin" proxy carries only those two (the
	// destination instance isn't pinned yet); the dest-announce proxy is fully specified - u32[5]=1 (a valid/ready
	// flag), u32[6]=0x9C50, u32[7]=the destination's playfield-INSTANCE token. The dest body then carries the spawn
	// position inside a length-prefixed blob (FUN_00cbf5d0). Every byte is reader-accounted (TeleportHandoffTests).
	//
	// A FAMILY (#530 rule 5a): both IIRs are [carrier][env][...][PlayfieldProxy][...], so the base TeleportIIR really
	// does hold what they share and Parse discriminates on the subop.
	public static class TeleportCodec
	{
		// The IIR types this codec constructs or parses.
		public static readonly GameServer0X2000Opcodes[] HandledTypes = [GameServer0X2000Opcodes.IntendedTeleportIIR, GameServer0X2000Opcodes.N3TeleportIIR];

		private const uint PositionBlobTag = 0x00018704; // first word of the length-prefixed spawn-position blob

		// The revive body's proxy uses this class word instead of PlayfieldProxy.PlayfieldClass; which of
		// 0xC79C/0xC79E/0xC7A0 a packet carries is per-PACKET and undecoded (see ResurrectionSpotsBody).
		private const uint RevivePlayfieldClass = 0x0000C7A0;

		// CANONICAL - the only method here that writes bytes.
		public static byte[] Build(TeleportIIR value)
		{
			ArgumentNullException.ThrowIfNull(value);
			using var s = new FaolanStream();
			Write(s, value);
			return s.ToArray();
		}

		// 0x10D27BC0 "begin teleport to map N": carrier + env + a PlayfieldProxy carrying only the class word and the
		// destination map (instance slots zero) + a trailing bool.
		//   [u32 subop][u32 0xC350][u32 clientInstance][u8 env=01][PlayfieldProxy][u8 bool=0]
		public static byte[] Build(uint clientInstance, uint destMapId)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForMap(destMapId), 0x00));
		}

		// The SAME-MAP teleport begin (#214): the single IntendedTeleport (0x10D27BC0) that pins the target playfield
		// for a same-map relocation WITHOUT a loading screen - retail sends it for the death revive AND same-playfield
		// zone-line doors (the inn; via WorldSimulation.RelocateSameMap -> SendReviveTeleport). ONE record, NO
		// position (the client positions off the RespawnMe spawnPointId / its RDB / the following n3Teleport). The
		// PlayfieldProxy is pinned to the CURRENT playfield: map + the player's own handle in slot3 + the ready flag +
		// the LIVE playfield token in slot7. CRITICAL: playfieldToken MUST be the token the client currently holds
		// (Zone.PlayfieldToken, captured from the burst-less world-entry snapshot - the same one SpawnPlayfieldRewriter
		// stamps into rendered spawns), NOT the MapWorldBurst token (ResolveMapPlayfieldToken): a mismatch makes the
		// client treat it as a real changemap and show a LOADING SCREEN. Decoded byte-for-byte from the beach resurrect
		// (Stihetfv #1387, map 0xFAA): trailing IIR bool = 1.
		public static byte[] Build(uint clientInstance, uint mapId, uint playerHandle, uint playfieldToken)
		{
			return Build(new IntendedTeleportIIR(clientInstance, 0x01, PlayfieldProxy.ForInstance(mapId, playfieldToken) with { OwnerId = playerHandle }, 0x01));
		}

		// 0x5A32F0D7 CROSS-MAP destination announce: carrier + 7 leading words (the last = 1.0f) + the fully-specified
		// destination PlayfieldProxy + 2 words + a length-prefixed spawn-position blob + a trailing bool.
		// destPlayfieldToken is the destination playfield-INSTANCE token (NOT a per-map constant - the captured
		// 0x00503570 was that session's instance); the live change-map path sources it from the destination map's
		// burst via WorldBroadcaster.ResolveMapPlayfieldToken so it matches the reconnect's world-entry stamp (#211).
		// destDescriptor (0x14 map 2010 / 0xBE map 2000) is the position blob's trailing word - a per-zone descriptor
		// not yet derivable from the data. Both default to the map-2010 capture values for the golden / a burst-less map.
		public static byte[] Build(uint clientInstance, uint destMapId, Vector3 position, uint destPlayfieldToken = 0x00503570u, uint destDescriptor = 0x00000014u)
		{
			return Build(new N3TeleportIIR(clientInstance, 0x00, position, PlayfieldProxy.ForInstance(destMapId, destPlayfieldToken), N3TeleportForm.CrossMap, DestDescriptor: destDescriptor));
		}

		// The SAME-MAP destination announce: the n3Teleport (0x5A32F0D7) that actually relocates the player on their
		// CURRENT playfield with no loading screen. Retail sends it for BOTH the death revive (#214, after the revive
		// IntendedTeleport) AND a same-playfield zone-line door - e.g. the Thirsty Dog Inn, which repositions within
		// City of Tortage rather than change-mapping (WorldSimulation.RelocateSameMap reaches it via
		// SendReviveDestination; WorldTeleportExtractor decodes this same variant). Its grammar DIFFERS from the
		// cross-map form: the spawn position is right after env (not in a trailing length-prefixed blob), the proxy
		// carries 0xC7A0 (not 0xC79C) + map + player handle with the instance slots ZERO, and there's no leading
		// 6-zero/1.0f block. Decoded byte-for-byte from the beach resurrect (Stihetfv #1277, map 0xFAA).
		public static byte[] Build(uint clientInstance, uint mapId, uint playerHandle, Vector3 position, float facingX, float facingZ)
		{
			// Instance slots zero: a same-map move does not re-pin the playfield instance.
			return Build(new N3TeleportIIR(clientInstance, 0x00, position, new PlayfieldProxy(mapId, RevivePlayfieldClass, playerHandle), N3TeleportForm.SameMap, facingX, facingZ, Trailing2: 0x01));
		}

		public static TeleportIIR Parse(in ReadOnlySpan<byte> data)
		{
			var r = new SpanReader(data, nameof(TeleportCodec));
			var value = Read(ref r);
			r.EnsureAtEnd();
			return value;
		}

		internal static TeleportIIR Read(ref SpanReader r)
		{
			var subopOffset = r.Position;
			var subop = r.ReadU32Be();
			r.ExpectU32Be(EntityClass.Character, "entity class");
			var clientInstance = r.ReadU32Be();
			var env = r.ReadU8();

			if (subop == (uint)GameServer0X2000Opcodes.IntendedTeleportIIR)
			{
				var proxy = PlayfieldProxy.Read(ref r);
				return new IntendedTeleportIIR(clientInstance, env, proxy, r.ReadU8());
			}

			if (subop != (uint)GameServer0X2000Opcodes.N3TeleportIIR)
				throw CodecParseException.Unexpected(nameof(TeleportCodec), subopOffset, "IntendedTeleportIIR or n3TeleportIIR", $"0x{subop:X8}");

			// The two n3Teleport forms are told apart by their LEADING BLOCK: the cross-map announce opens with six
			// zero words and a 1.0f, the same-map one with the arrival position and a 2D heading. They are only
			// ambiguous for a same-map move to exactly (0,0,0) with facingX 0 and facingZ 1.0 - no playfield has a
			// walkable origin, so that shape does not occur.
			var w0 = r.ReadU32Be();
			var w1 = r.ReadU32Be();
			var w2 = r.ReadU32Be();
			var w3 = r.ReadU32Be();
			var w4 = r.ReadU32Be();
			var w5 = r.ReadU32Be();
			var w6 = r.ReadU32Be();
			if ((w0 | w1 | w2 | w3 | w4 | w5) == 0 && w6 == BitConverter.SingleToUInt32Bits(1.0f))
			{
				var proxy = PlayfieldProxy.Read(ref r);
				r.ExpectU32Be(0, "obj+0x6c");
				r.ExpectU32Be(0, "obj+0x70");
				var blob = new SpanReader(r.ReadBytesU32Length(), nameof(TeleportCodec) + ".positionBlob");
				blob.ExpectU32Be(PositionBlobTag, "spawn-position blob tag");
				var position = blob.ReadVector3Be();
				var descriptor = blob.ReadU32Be();
				blob.EnsureAtEnd();
				return new N3TeleportIIR(clientInstance, env, position, proxy, N3TeleportForm.CrossMap, DestDescriptor: descriptor, Trailing: r.ReadU8());
			}

			var arrival = new Vector3(BitConverter.UInt32BitsToSingle(w0), BitConverter.UInt32BitsToSingle(w1), BitConverter.UInt32BitsToSingle(w2));
			if (w3 != 0 || w5 != 0)
				throw CodecParseException.Unexpected(nameof(TeleportCodec), r.Position - 16, "the same-map heading's two zero words", $"0x{w3:X8} / 0x{w5:X8}");

			var sameMapProxy = PlayfieldProxy.Read(ref r);
			r.ExpectU32Be(0, "same-map trailing word 0");
			r.ExpectU32Be(0, "same-map trailing word 1");
			r.ExpectU32Be(0, "same-map trailing word 2");
			return new N3TeleportIIR(clientInstance, env, arrival, sameMapProxy, N3TeleportForm.SameMap,
				BitConverter.UInt32BitsToSingle(w4), BitConverter.UInt32BitsToSingle(w6), Trailing: r.ReadU8(), Trailing2: r.ReadU8());
		}

		internal static void Write(FaolanStream s, TeleportIIR v)
		{
			switch (v)
			{
				case IntendedTeleportIIR begin:
				{
					s.WriteUInt32(GameServer0X2000Opcodes.IntendedTeleportIIR).WriteUInt32(EntityClass.Character).WriteUInt32(begin.ClientInstance).WriteByte(begin.Env);
					begin.Proxy.Write(s);
					s.WriteByte(begin.Trailing); // IntendedTeleportIIR trailing bool (-> obj+0x40)
					break;
				}

				case N3TeleportIIR { Form: N3TeleportForm.CrossMap } dest:
				{
					using var blob = new FaolanStream();
					blob.WriteUInt32(PositionBlobTag).WriteVector3(dest.Position).WriteUInt32(dest.DestDescriptor);

					s.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR)
						.WriteUInt32(EntityClass.Character)
						.WriteUInt32(dest.ClientInstance)
						.WriteByte(dest.Env)
						.WriteArray(new byte[24]) // 6 leading words = 0
						.WriteFloat(1.0f); // 7th leading word = 1.0
					dest.Proxy.Write(s);
					s.WriteUInt32(0).WriteUInt32(0) // obj+0x6c / +0x70
						.WriteArrayUInt32Length(blob.ToArray())
						.WriteByte(dest.Trailing); // n3TeleportIIR trailing bool
					break;
				}

				case N3TeleportIIR sameMap:
				{
					s.WriteUInt32(GameServer0X2000Opcodes.N3TeleportIIR)
						.WriteUInt32(EntityClass.Character)
						.WriteUInt32(sameMap.ClientInstance)
						.WriteByte(sameMap.Env)
						.WriteVector3(sameMap.Position)
						.WriteUInt32(0)
						.WriteFloat(sameMap.FacingX)
						.WriteUInt32(0)
						.WriteFloat(sameMap.FacingZ);
					sameMap.Proxy.Write(s);
					s.WriteUInt32(0).WriteUInt32(0).WriteUInt32(0).WriteByte(sameMap.Trailing).WriteByte(sameMap.Trailing2);
					break;
				}

				default:
					throw new CodecParseException($"{nameof(TeleportCodec)}: unsupported teleport record {v.GetType().Name}");
			}
		}
	}

	// Which n3Teleport grammar a record uses - the cross-map announce (a loading screen) or the same-map relocate.
	public enum N3TeleportForm
	{
		CrossMap,
		SameMap
	}

	// What the two teleport records share: the addressed client instance, the carrier's env byte, and the
	// PlayfieldProxy naming the destination playfield.
	public abstract record TeleportIIR(uint ClientInstance, byte Env, PlayfieldProxy Proxy);

	// IntendedTeleportIIR_c (0x10D27BC0) - "begin teleport". Trailing is the IIR's own bool at obj+0x40: 0 for a
	// cross-map begin, 1 for the same-map one.
	public sealed record IntendedTeleportIIR(uint ClientInstance, byte Env, PlayfieldProxy Proxy, byte Trailing) : TeleportIIR(ClientInstance, Env, Proxy);

	// n3TeleportIIR_t (0x5A32F0D7) - the destination announce, in whichever of its two forms.
	public sealed record N3TeleportIIR(uint ClientInstance, byte Env, Vector3 Position, PlayfieldProxy Proxy, N3TeleportForm Form,
		float FacingX = 0f, float FacingZ = 0f, uint DestDescriptor = 0, byte Trailing = 0, byte Trailing2 = 0) : TeleportIIR(ClientInstance, Env, Proxy);
}
