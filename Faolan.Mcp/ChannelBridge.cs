using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Faolan.Mcp
{
	/// <summary>
	///     Faolan in-game chat -> Claude Code CHANNEL bridge (research-preview channels; docs/en/channels-reference
	///     on code.claude.com), and the entry point that makes this assembly an executable. Claude Code spawns it as
	///     a stdio MCP server (.mcp.json "faolan-chat"); it polls the GAME SERVER's MCP endpoint
	///     (read_claude_channel) and PUSHES each new in-game post on the "Claude" chat channel into the running
	///     Claude Code session as a notifications/claude/channel event - Claude reacts to in-game chat without
	///     polling himself. The <c>reply</c> tool routes the answer back through send_claude_channel_message
	///     (broadcast in-game as "Claude").
	///     <para>
	///         Enable per session: <c>claude --dangerously-load-development-channels server:faolan-chat</c>
	///         (custom channels are not on the research-preview allowlist, so the development flag is required).
	///     </para>
	///     <para>
	///         Resilient to the game server being down/restarting: the poll loop just reconnects. Only player posts
	///         (FromClaude=false) are forwarded, and the backlog that predates bridge startup is skipped. Sender
	///         gating: the game server binds 127.0.0.1 and this is a single-user dev setup - every in-game post is
	///         the local user by construction.
	///     </para>
	/// </summary>
	public static class ChannelBridge
	{
		private const string ChannelNotificationMethod = "notifications/claude/channel";

		private static readonly string FaolanMcpUrl = Environment.GetEnvironmentVariable("FAOLAN_MCP_URL") ?? "http://127.0.0.1:7099/mcp";
		private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(double.TryParse(Environment.GetEnvironmentVariable("FAOLAN_CHANNEL_POLL_MS"), out var ms)
			? ms
			: 1500);

		private static McpClient _gameServer;

		public static async Task Main()
		{
			var options = new McpServerOptions
			{
				ServerInfo = new Implementation { Name = "faolan-chat", Version = "0.1.0" },
				Capabilities = new ServerCapabilities
				{
					// This key is what makes the server a CHANNEL: Claude Code registers a notification
					// listener for it and injects our events into the running session. The value must be a
					// JsonElement - the SDK's source-generated serializer cannot serialize a plain
					// Dictionary<string, object> boxed as object (initialize fails with a -32603).
					Experimental = new Dictionary<string, object> { ["claude/channel"] = JsonSerializer.Deserialize<JsonElement>("{}") }
				},
				ServerInstructions = "In-game Age of Conan chat from the Faolan server's \"Claude\" channel arrives as "
				                     + "<channel source=\"faolan-chat\" sender=\"...\" sender_id=\"...\">. These are players talking to you "
				                     + "in-game. Reply with the faolan-chat reply tool (plain text, chat-length): pass the event's sender_id "
				                     + "as whisper_to to answer that player PRIVATELY as a whisper (the default conversational mode), or "
				                     + "omit it to broadcast on the channel to everyone. The same log is also readable via the faolan MCP "
				                     + "server's read_claude_channel tool."
			};
			options.ToolCollection ??= [];
			options.ToolCollection.Add(McpServerTool.Create(Reply, new McpServerToolCreateOptions
			{
				Name = "reply",
				Description = "Reply in-game as \"Claude\": pass whisper_to (the channel event's sender_id) for a private whisper to that player, omit it to broadcast on the Claude channel."
			}));

			await using var server = McpServer.Create(new StdioServerTransport("faolan-chat"), options);

			using var cts = new CancellationTokenSource();
			var serverRun = server.RunAsync(cts.Token);
			var pollRun = PollLoop(server, cts.Token);

			await serverRun; // returns when Claude Code closes the session
			await cts.CancelAsync();
			try
			{
				await pollRun;
			}
			catch (OperationCanceledException)
			{
				// normal shutdown
			}
		}

		[Description("Reply in-game as \"Claude\": a private whisper to one player (pass whisper_to = the channel event's sender_id) or a broadcast on the Claude channel (omit whisper_to).")]
		private static async Task<string> Reply([Description("The chat message to send in-game.")] string text,
			[Description("Character id to whisper privately (the sender_id from the channel event). Omit to broadcast on the channel.")] string whisper_to = null)
		{
			try
			{
				return await SendInGame(text, whisper_to);
			}
			catch
			{
				// One reconnect attempt - the game server may have restarted under us.
				_gameServer = null;
				return await SendInGame(text, whisper_to);
			}
		}

		private static async Task<string> SendInGame(string text, string whisperTo)
		{
			var client = await GameServer(CancellationToken.None);
			var result = uint.TryParse(whisperTo, out var characterId)
				? await client.CallToolAsync("send_claude_whisper", new Dictionary<string, object> { ["characterId"] = characterId, ["message"] = text })
				: await client.CallToolAsync("send_claude_channel_message", new Dictionary<string, object> { ["message"] = text });
			return ResultText(result) ?? "sent";
		}

		private static async Task PollLoop(McpServer server, CancellationToken cancellationToken)
		{
			ulong? cursor = null;
			using var timer = new PeriodicTimer(PollInterval);
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				try
				{
					var client = await GameServer(cancellationToken);
					var read = ParseChannelRead(ResultText(await client.CallToolAsync("read_claude_channel",
						new Dictionary<string, object> { ["afterSequence"] = cursor ?? 0 }, cancellationToken: cancellationToken)));

					if (cursor == null)
					{
						// First successful read: skip the pre-bridge backlog, only forward what comes after.
						cursor = read.Latest;
						await Console.Error.WriteLineAsync($"[faolan-chat] connected; skipping backlog through sequence {cursor}");
						continue;
					}

					if (read.Latest < cursor.Value)
					{
						// The game server restarted: its channel log is in-memory, so sequences reset below our
						// cursor and new posts became invisible to the old afterSequence filter. Rewind so
						// everything posted since the restart gets delivered on the next poll.
						await Console.Error.WriteLineAsync($"[faolan-chat] sequence rewind (latest {read.Latest} < cursor {cursor}); game server restarted - rewinding");
						cursor = 0;
						continue;
					}

					cursor = Math.Max(cursor.Value, read.Latest);
					foreach (var message in read.Messages.Where(m => !m.FromClaude))
					{
						await server.SendNotificationAsync(ChannelNotificationMethod, new ChannelEvent
						{
							Content = message.Message ?? "",
							Meta = new Dictionary<string, string>
							{
								["sender"] = message.SenderName ?? "",
								["sender_id"] = message.SenderId.ToString(),
								["sequence"] = message.Sequence.ToString()
							}
						}, cancellationToken: cancellationToken);
					}
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					if (_gameServer != null)
						await Console.Error.WriteLineAsync($"[faolan-chat] poll failed ({ex.Message}); will reconnect");
					_gameServer = null;
				}
			}
		}

		private static async Task<McpClient> GameServer(CancellationToken cancellationToken)
		{
			if (_gameServer != null)
				return _gameServer;

			var transport = new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(FaolanMcpUrl),
				Name = "faolan"
			});
			_gameServer = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
			return _gameServer;
		}

		private static string ResultText(CallToolResult result)
		{
			return result?.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		}

		// read_claude_channel returns either a JSON array of messages or
		// "No messages after sequence N (latest sequence: M)." - normalize both.
		private static (IReadOnlyList<ChannelMessage> Messages, ulong Latest) ParseChannelRead(string text)
		{
			if (string.IsNullOrEmpty(text))
				return ([], 0);

			if (text.StartsWith("No messages", StringComparison.Ordinal))
			{
				const string marker = "latest sequence: ";
				var at = text.IndexOf(marker, StringComparison.Ordinal);
				var end = text.IndexOf(')', at);
				return ([], ulong.Parse(text[(at + marker.Length)..end]));
			}

			var messages = JsonSerializer.Deserialize<List<ChannelMessage>>(text) ?? [];
			return (messages, messages.Count == 0
				? 0
				: messages.Max(m => m.Sequence));
		}

		private sealed class ChannelMessage
		{
			public ulong Sequence { get; set; }
			public uint SenderId { get; set; }
			public string SenderName { get; set; }
			public string Message { get; set; }
			public bool FromClaude { get; set; }
		}

		private sealed class ChannelEvent
		{
			[JsonPropertyName("content")]
			public string Content { get; set; }

			[JsonPropertyName("meta")]
			public Dictionary<string, string> Meta { get; set; }
		}
	}
}
