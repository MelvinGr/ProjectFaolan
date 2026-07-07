using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.World;
using Faolan.Core.Network.Codecs;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Sends a ChatTextIIR (0xE0B51102) - a WORLD/SYSTEM chat line into the client's chat window (#103), distinct
	// from FormatFeedbackIIR's on-screen FIFO overlay and from the AgentServer channel chat. Carried as an IIR on
	// the world conversation (opcode 0x2000, WorldFraming sender/receiver); the IIR body's target instance is the
	// player's GameInterface = the world receiver instance (ClientInstance). Body built byte-exact by ChatTextCodec.
	public partial class GameServerListener
	{
		// A captured ChatTextIIR system line (demol80 randomActions #7564): a localized `CriteriaYou` remoteformat.
		// Replayed verbatim (localized token ids + keys unchanged) with only the live session instance substituted,
		// so it is guaranteed byte-valid. Arbitrary LITERAL text would need a literal-text remoteformat template (or
		// RDB string ids) - the same follow-up the FormatFeedback FIFO banner has.
		private const string CriteriaSystemLineXml = "<remoteformat id=\"562\" category=\"100\" key=\"6segSK/UI?Kc`Tzg.tio\" knubot=\"0\"  >" +
		                                             "<parameter value=\"7;&lt;localized token=&quot;CriteriaYou&quot; category=&quot;130&quot; flags=&quot;&quot; " +
		                                             "key=&quot;^4L2&amp;#36;dU5AGjcTX_xJ[*Q&quot; &gt;\" /><parameter value=\"7;&lt;localized id=&quot;700&quot; " +
		                                             "category=&quot;55200&quot; flags=&quot;&quot; key=&quot;MpRf.Cx.%^MGdFXT&amp;quot;9X(&quot; &gt;\" /></remoteformat>";

		// Sends the proven captured system line - the test bootstrap for the ChatTextIIR path until a literal-text
		// remoteformat template lands.
		private Task SendSampleWorldChat(INetworkClient client)
		{
			return SendWorldChat(client, CriteriaSystemLineXml);
		}

		// Pushes a ChatTextIIR (raw remoteformat XML) to the client's chat window. Addressed to the player's
		// GameInterface instance, on the world conversation.
		private async Task SendWorldChat(INetworkClient client, string xml)
		{
			var instance = client.Session.Account.ClientInstance;
			var body = ChatTextCodec.Build(instance, Encoding.ASCII.GetBytes(xml));

			var ps = new PacketStream();
			ps.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), (ushort)0x2000);
			ps.WriteArray(body);
			await ps.SendAsync(client);
		}
	}
}
