using Faolan.Core.Enums;
using Faolan.GameServer.Dialogue;
using Faolan.Tests.Support;
using Xunit;

namespace Faolan.Tests
{
	// #217: closing an NPC dialogue must leave it CLOSED. The retail client signals the close with its own
	// KnubotCloseChatWindow (0xC4AF39EB, c2s - decoded from the gameplay captures, the client double-sends it);
	// our server now tears down the conversation session state on it instead of dropping it into the unknown-opcode
	// default (which left the state live AND spammed a system message). With the state cleared, the next click
	// starts a fresh conversation rather than re-entering the closed one.
	public class ConversationCloseTests
	{
		[Fact]
		public void CloseOpcode_IsTheKnubotCloseChatWindowIir()
		{
			// The c2s close opcode == the same IIR type the server sends s2c to close the window.
			Assert.Equal(0xC4AF39EBu, (uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR);
			Assert.Equal((uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR, (uint)GameServer0X2000Opcodes.KnubotCloseChatWindowIIR);
		}

		[Fact]
		public void EndConversation_ClearsAllConversationSessionState()
		{
			var client = new RecordingNetworkClient(0x0089ABCD)
			{
				Session =
				{
					ConversationAnswerPath = "0>1",
					ConversationNpcName = "Casilda",
					ConversationTypeId = 13,
					ConversationNpcHandle = 0x000818D6
				}
			};

			ConversationService.EndConversation(client);

			Assert.Null(client.Session.ConversationAnswerPath);
			Assert.Null(client.Session.ConversationNpcName);
			Assert.Null(client.Session.ConversationTypeId);
			Assert.Null(client.Session.ConversationNpcHandle);
			Assert.Empty(client.Sent); // a client-initiated close gets NO reply (the window is already closed)
		}
	}
}
