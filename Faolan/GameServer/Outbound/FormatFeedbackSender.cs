using System;
using System.Text;
using System.Threading.Tasks;
using Faolan.Core.Network;
using Faolan.Core.Network.Packets;
using Faolan.GameServer.Combat;
using Faolan.GameServer.World;

// ReSharper disable once CheckNamespace
namespace Faolan.GameServer
{
	// Sends a FormatFeedbackIIR (0x8D5A8B6B) - AoC's ON-SCREEN feedback / FIFO-message overlay (NOT chat). The
	// `showfifomessagefromserver(...)` token family drives the top-of-screen notification banners (zone-name reveal,
	// pickup notices, ...). Carried as an IIR on the world conversation (opcode 0x2000, WorldFraming sender/receiver);
	// the IIR body's target instance is the player's GameInterface = the world receiver instance (ClientInstance),
	// verified in the Strie capture. Body built byte-exact by FormatFeedbackCodec (FormatFeedbackCodecTests).
	public partial class GameServerListener
	{
		// The captured GenericDisplayAreaName FIFO banner (necrol80 Has-inventory #1753) - a known-good top-of-screen
		// notification. Replayed verbatim (key + localized string id unchanged) with only the live session instance
		// substituted, so it is guaranteed byte-valid. Arbitrary literal text would need a literal-text FIFO template
		// (or an RDB string id) - a follow-up.
		private const string AreaNameBannerXml = "<remoteformat token=\"showfifomessagefromserver(8)|main(1)|GenericDisplayAreaName\" category=\"50018\" " +
		                                         "key=\"pcAdIWYhSXfTFDe5I1g7\" knubot=\"0\"  ><parameter value=\"7;&lt;localized category=&#39;50025&#39; " +
		                                         "token=&#39;DisplayedText(3)|GenericDisplayAreaName|3474586&#39;&gt;\" name=\"DisplayedText\" /></remoteformat>";

		// Sends the proven GenericDisplayAreaName FIFO banner - the top-of-screen notification overlay.
		private Task SendAreaNameBanner(INetworkClient client)
		{
			return SendFeedback(client, AreaNameBannerXml);
		}

		// Pushes a FormatFeedback message (raw remoteformat XML) to the client's on-screen feedback. typeFlags =
		// FifoBannerTypeFlags for the top-of-screen FIFO banner. Addressed to the player's GameInterface instance.
		private async Task SendFeedback(INetworkClient client, string xml, uint typeFlags = FormatFeedbackCodec.FifoBannerTypeFlags, uint footer = 1)
		{
			var instance = client.Session.Account.ClientInstance;
			var body = FormatFeedbackCodec.Build(instance, typeFlags, footer, Encoding.ASCII.GetBytes(xml));

			var ps = new PacketStream();
			ps.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), (ushort)0x2000);
			ps.WriteArray(body);
			await ps.SendAsync(client);
		}

		// Emits the retail level-up dings: ONE NewLevelIIR_t (0x6D314743) per level gained - the event that fires the
		// client's "Congratulations, you have reached level N!" banner + the level-up visual/audio effect (#209). The
		// banner text is client-side and gated on this packet; the raw Level stat + AttributeChanged lines alone show
		// nothing. Retail sends a SEPARATE event per level (Hoxxling dinged L2/L3/L4 as three, frames 9937->), each
		// carrying [newLevel, currentXP, xpThisLevel, xpNextLevel, 1, 8, 0] (fields 5/6/7 constant across every
		// capture). Sent before the AttributeChanged burst (combat-levelup capture ordering).
		private async Task SendLevelUpDings(INetworkClient client, uint fromLevel, uint toLevel, uint finalRemainingXp)
		{
			var instance = client.Session.Account.ClientInstance;
			for (var lvl = fromLevel + 1; lvl <= toLevel; lvl++)
			{
				var thisXp = LevelingResolver.CumulativeXpForLevel(lvl);
				var nextXp = LevelingResolver.CumulativeXpForLevel(lvl + 1);
				var currentXp = lvl == toLevel ? thisXp + finalRemainingXp : thisXp; // only the final level carries partial XP
				ReadOnlySpan<uint> deltas = [currentXp, thisXp, nextXp, 1u, 8u, 0u];

				var ps = new PacketStream();
				ps.WriteHeader(WorldFraming.Sender, WorldFraming.Receiver(client), (ushort)0x2000);
				ps.WriteArray(NewLevelIirCodec.Build(instance, lvl, deltas));
				await ps.SendAsync(client);
			}
		}

		// Emits the level-up attribute feedback: one AttributeChanged/Increased on-screen line per primary attribute
		// that rose (#209). This is the VISIBLE half of a level-up; the caller still pushes the new vitals maxima.
		// `levelsGained` covers a multi-level jump (e.g. a `.level 40` set). The growth amounts are provisional
		// (Combat.AttributeGrowth) until real per-class tables exist.
		private async Task SendLevelUpFeedback(INetworkClient client, uint newLevel, uint levelsGained = 1)
		{
			var character = client.Session.Character;
			foreach (var (attributeId, amount) in AttributeGrowth.ForLevelGain(character.Class, newLevel, levelsGained))
				await SendFeedback(client, AttributeChangedFeedback.BuildXml(attributeId, amount), AttributeChangedFeedback.TypeFlags, AttributeChangedFeedback.Footer);
		}
	}
}
