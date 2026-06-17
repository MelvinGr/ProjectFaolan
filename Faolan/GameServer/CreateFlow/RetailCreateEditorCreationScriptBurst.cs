using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Conan;
using Faolan.Core.Network.Packets;
using Faolan.Core.Network.Packets.Opcodes;
using Faolan.GameServer.World;

namespace Faolan.GameServer
{
	public partial class GameServerListener
	{
		// The create-world ScryScript step content (decoded by Captures/decode_scryscript.py, #223): the PlayOrSkip(72)
		// remoteformat token for the "Slave Galley" creation step (f4) and the RDB resource manifest the client preloads
		// (f5, "rdbId:value;..."). Carried as named strings now (was a verbatim hex blob); ScryScript.BuildStep frames them.
		internal const string CreateWorldScriptToken =
			"<remoteformat token=\"playorskip(72)|onclientconnected(1)|NEW_character_creation Slave Galley (server-side)\" category=\"50023\" key=\"&quot;B]SZ.AGRGrAyOGe*BG-\" knubot=\"0\"  ></remoteformat>";
		internal const string CreateWorldResourceManifest =
			"1010004:793713;1010004:793714;1070003:4000;1000097:4000;1010030:156211;1010030:239448;1010030:565913;1010004:3728533;1060668:4000;1000059:4000;1060667:167368;1010004:3075445;1010004:3180025;1010004:221285;1010004:3663190;1010004:208923;1010004:208266;1010004:226891;1010004:3663191;1010004:3656109;1010030:210069;1010001:210070;1010004:3075452;1010004:3075450;1010004:3075451;1010300:4000;1000049:4000;1066611:4000;1000013:40000000;1000015:40000000;1010506:4000;1000058:4000;1010207:110006;1010207:262241;1010207:568806;1010207:3420331;1010207:167183";

		// Retail create-editor CHARACTER-CREATION SCRIPT burst from
		// Captures/account-stricken998-character-create-no-worldenter.json (GameServer port 7030), rows 274-302/324-331.
		//
		// This is the burst that starts the character-creation cutscene. The key packets are the two ScryScriptIIR
		// remoteformats: token "playorskip(72)|...|NEW_character_creation Slave Galley (server-side)" then "PlayOrSkip".
		// Without them the playfield loads (ambient audio) but the Slave Galley scene never launches (black screen).
		// The preceding StatUpdate / BehaviorControlCenter / MagicBuff / Energy / Stat packets set up the initial
		// character/world state the script expects; the trailing SetMorphValue packets are part of the same setup.
		// All packets ride one conversation (sender hash("GameAgent") sub-0x59 -> receiver hash("GameInterface") +
		// instance varint) and reuse the editor temp instance; a byte-for-byte replay reproduces retail exactly
		// (locked by BurstGoldenTests). The 32-bit values are the client's IIR message-type hashes (see
		// GameServer0X2000RespondsOpcodes / docs/aoc-opcode-ids.md); bodies are kept verbatim - they are opaque
		// editor-state (entity handles 0x0009FFxx, morph mesh names, stat blobs) the client choreography expects.

		internal static async Task SendRetailCreateEditorCreationScriptBurst(INetworkClient client)
		{
			var id = client.Session.Account.ClientInstance;
			var sender = new byte[] { 0x0D, 0x13, 0xCE, 0x71, 0xB1, 0x10, 0x59 };
			var receiver = ConanEndpoints.EndpointBytes(ConanEndpoints.GameInterfaceType, id);

			byte[] C350 = [0x00, 0x00, 0xC3, 0x50];

			// Initial vitals + behavior/action setup for the editor entities (handles 0x0009FF92..96).
			await Iir(GameServer0X2000RespondsOpcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF92u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000RespondsOpcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF93u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000RespondsOpcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF94u, 5, 85843, 1, 24, 0));
			await Iir(GameServer0X2000RespondsOpcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF94u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000RespondsOpcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF95u, 7, 41413, 1, 24, 0));
			await Iir(GameServer0X2000RespondsOpcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF95u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));
			await Iir(GameServer0X2000RespondsOpcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF96u, 7, 41413, 1, 24, 0));
			await Iir(GameServer0X2000RespondsOpcodes.StatUpdateIIR, CreateEditorRecordBuilder.BuildStatUpdateIir(0x0009FF96u, [(0x1Bu, 0x189Cu), (0x1F8u, 0x189Cu), (0x1FAu, 0x189Cu)]));

			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x00, 0x00, 0x00, 0x03, 0x60, 0x00, 0x00, 0x00, 0x02);

			// MagicBuff carries the instance twice (caster + target).
			await new PacketStream()
				.WriteHeader(sender, receiver, GameServerCarrierOpcodes.IirCarrier)
				.WriteArrayUInt32Length(s => s
					.WriteUInt32(GameServer0X2000RespondsOpcodes.ApplySpell)
					.WriteArray(0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(id)
					.WriteArray(0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00, 0x00, 0xC3, 0x50)
					.WriteUInt32(id)
					.WriteArray(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x27, 0x10, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x0B, 0x90, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00,
						0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x00, 0x3D, 0x5F, 0xC9, 0x66))
				.SendAsync(client);

			await IirId(GameServer0X2000RespondsOpcodes.EnergyIIR, C350, 0x01, 0x00, 0x00, 0x00, 0x00);

			// Stat sequence (entity stat ids 0x0566..0x0573, 0x0575, 0x0580, then 0x0F428F).
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x66, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x67, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x68, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x69, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6A, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6B, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6C, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6D, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6E, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x6F, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x70, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x71, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x72, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x73, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x75, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x01, 0x00, 0x00, 0x05, 0x80, 0x00, 0x00, 0x00, 0x00);
			await IirId(GameServer0X2000RespondsOpcodes.StatIIR, C350, 0x00, 0x00, 0x0F, 0x42, 0x8F, 0x00, 0x00, 0x00, 0x00);

			await IirId(GameServer0X2000RespondsOpcodes.StatUpdateIIR, C350, 0x00, 0x00, 0x00, 0x00, 0x06, 0x00, 0x00, 0x00, 0x1B, 0x00, 0x00, 0x1F, 0x40, 0x00, 0x00, 0x00, 0xBA, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0xFF,
				0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x03, 0x01, 0x00, 0x00, 0x02, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0x5F, 0x59, 0xEA, 0x5C, 0x54);

			// ScryScriptIIR: the create-world "PlayOrSkip(72) Slave Galley" remoteformat + its RDB resource manifest,
			// then the "PlayOrSkip" ScriptMessage (decoded by Captures/decode_scryscript.py).
			await IirRecord(ScryScript.BuildStep(id, CreateWorldScriptToken, mode: 7, resources: CreateWorldResourceManifest, f6Field1: 6177372, f6Field2: 9422303));
			await IirRecord(ScryScript.BuildStep(id, "PlayOrSkip"));

			// SetMorphValue setup (hair meshes, then a texscript) on the editor entities.
			await Iir(GameServer0X2000RespondsOpcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF94u, "hairmesh_207920", 1.0f, 0x00, 0x00000001u));
			await Iir(GameServer0X2000RespondsOpcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF95u, "hairmesh_203666", 1.0f, 0x00, 0x00000001u));
			await Iir(GameServer0X2000RespondsOpcodes.BehaviorControlCenterIIR, CreateEditorRecordBuilder.BuildBehaviorControlCenterIir(0x0009FF94u, 5, 85843, 2, 24, 0));
			await Iir(GameServer0X2000RespondsOpcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(id, "texscript_183875", 1.0f, 0x01, 0x00000001u));
			await Iir(GameServer0X2000RespondsOpcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF95u, "texscript_183875", 1.0f, 0x01, 0x00000002u));
			await Iir(GameServer0X2000RespondsOpcodes.SetMorphValueIIR, CreateEditorRecordBuilder.BuildSetMorphValueIir(0x0009FF94u, "texscript_183875", 1.0f, 0x01, 0x00000002u));
			return;

			// One IIR packet: 0x2000 envelope -> [u32 length][u32 IIR-id][body]. The length is recomputed, so editing
			// a body never desyncs it.
			Task Iir(GameServer0X2000RespondsOpcodes iir, params byte[] body)
			{
				return new PacketStream()
					.WriteHeader(sender, receiver, GameServerCarrierOpcodes.IirCarrier)
					.WriteArrayUInt32Length(s => s
						.WriteUInt32(iir)
						.WriteArray(body))
					.SendAsync(client);
			}

			// Same, for the packets that carry the client instance mid-body: [prefix][instance][suffix].
			Task IirId(GameServer0X2000RespondsOpcodes iir, byte[] prefix, params byte[] suffix)
			{
				return new PacketStream()
					.WriteHeader(sender, receiver, GameServerCarrierOpcodes.IirCarrier)
					.WriteArrayUInt32Length(s => s
						.WriteUInt32(iir)
						.WriteArray(prefix)
						.WriteUInt32(id)
						.WriteArray(suffix))
					.SendAsync(client);
			}

			// Sends a fully-built IIR record (one that already begins with its sub-opcode, e.g. ScryScript.BuildStep);
			// the length prefix is recomputed by WriteArrayUInt32Length.
			Task IirRecord(byte[] record)
			{
				return new PacketStream()
					.WriteHeader(sender, receiver, GameServerCarrierOpcodes.IirCarrier)
					.WriteArrayUInt32Length(s => s.WriteArray(record))
					.SendAsync(client);
			}
		}
	}
}
