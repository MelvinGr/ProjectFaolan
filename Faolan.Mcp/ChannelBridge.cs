using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Faolan.Core.Cli;
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

		private static McpClient? _gameServer;
		private static TimeSpan _backlogGrace;
		private static TimeSpan _pollInterval;

		// Set once from config in Main (McpUrl = the game-server MCP endpoint this bridge connects to). _backlogGrace:
		// on its first read after (re)connecting the bridge skips pre-existing posts as "backlog", but only those
		// OLDER than this window - posts newer than it are delivered, so a bridge that comes up just after a player
		// speaks still surfaces that post instead of dropping it forever (#205). The bridge is respawned on every
		// /mcp reconnect / session restart, so a fresh process must catch the immediately-preceding chatter.
		private static string _mcpUrl = "";

		// The backlog floor for the bridge's first successful read: the newest message OLDER than the grace
		// window (relative to <paramref name="nowUtc" />). Messages newer than the window have Sequence greater
		// than this floor, so the poll loop forwards them rather than skipping them as backlog (#205). Returns 0
		// when every message is within the window (forward them all) or the log is empty.
		public static ulong InitialCursor(IEnumerable<(ulong Sequence, DateTime TimestampUtc)> messages, DateTime nowUtc, TimeSpan grace)
		{
			var cutoff = nowUtc - grace;
			return messages.Where(m => m.TimestampUtc < cutoff).Select(m => m.Sequence).DefaultIfEmpty(0UL).Max();
		}

		public static async Task Main(string[] args)
		{
			// Config: env > json, through the shared builder (FaolanCommandLine) - parity with the server / importer.
			// No command-line layer: Claude Code spawns this bridge as a stdio MCP server with no arguments at all,
			// so its settings (ChatBridge:Url / :PollMs / :BacklogGraceMs) come from faolan.json or the environment.
			var config = FaolanCommandLine.Build();
			_mcpUrl = config.ChatBridgeUrl;
			_backlogGrace = TimeSpan.FromMilliseconds(config.ChatBridgeBacklogGraceMs);
			_pollInterval = TimeSpan.FromMilliseconds(config.ChatBridgePollMs);

			var options = new McpServerOptions
			{
				ServerInfo = new Implementation
				{
					Name = "faolan-chat",
					Version = "0.1.0"
				},
				Capabilities = new ServerCapabilities
				{
					// This key is what makes the server a CHANNEL: Claude Code registers a notification
					// listener for it and injects our events into the running session. The value must be a
					// JsonElement - the SDK's source-generated serializer cannot serialize a plain
					// Dictionary<string, object> boxed as object (initialize fails with a -32603).
					Experimental = new Dictionary<string, object>
					{
						["claude/channel"] = JsonSerializer.Deserialize<JsonElement>("{}")
					}
				},
				ServerInstructions = "In-game Age of Conan chat from the Faolan server's \"Claude\" channel arrives as " +
									 "<channel source=\"faolan-chat\" sender=\"...\" sender_id=\"...\" mode=\"whisper|channel\">. These are " +
									 "players talking to you in-game. Reply with the faolan-chat reply tool (plain text, chat-length) in the " +
									 "SAME mode you were addressed in: when mode=\"whisper\" the player told you privately, so pass the event's " +
									 "sender_id as whisper_to to whisper back; when mode=\"channel\" they posted on the public channel, so OMIT " +
									 "whisper_to to broadcast your reply on the channel. The same log is also readable via the faolan MCP " +
									 "server's read_claude_channel tool (its ViaWhisper field is the same mode flag)."
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

		private static async Task<McpClient> GameServer(CancellationToken cancellationToken)
		{
			if (_gameServer != null)
				return _gameServer;

			var transport = new HttpClientTransport(new HttpClientTransportOptions
			{
				Endpoint = new Uri(_mcpUrl),
				Name = "faolan"
			});
			_gameServer = await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
			return _gameServer;
		}

		// read_claude_channel returns either a JSON array of messages or
		// "No messages after sequence N (latest sequence: M)." - normalize both.
		private static (IReadOnlyList<ChannelMessage> Messages, ulong Latest) ParseChannelRead(string? text)
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

		private static async Task PollLoop(McpServer server, CancellationToken cancellationToken)
		{
			ulong? cursor = null;
			using var timer = new PeriodicTimer(_pollInterval);
			while (await timer.WaitForNextTickAsync(cancellationToken))
			{
				try
				{
					var client = await GameServer(cancellationToken);
					var read = ParseChannelRead(ResultText(await client.CallToolAsync("read_claude_channel", new Dictionary<string, object?>
					{
						["afterSequence"] = cursor ?? 0
					}, cancellationToken: cancellationToken)));

					if (read.Latest < (cursor ?? 0))
					{
						// The game server restarted: its channel log is in-memory, so sequences reset below our
						// cursor and new posts became invisible to the old afterSequence filter. Rewind so
						// everything posted since the restart gets delivered on the next poll.
						await Console.Error.WriteLineAsync($"[faolan-chat] sequence rewind (latest {read.Latest} < cursor {cursor}); game server restarted - rewinding");
						cursor = 0;
						continue;
					}

					if (cursor == null)
					{
						// First successful read after (re)connecting. Don't replay the whole in-memory backlog, but
						// DO deliver anything posted within the grace window: a fresh bridge process (every /mcp
						// reconnect / session restart spawns one) whose first read lands just AFTER a player spoke
						// would otherwise skip that post as "backlog" and never surface it (#205). The floor is the
						// newest post older than the window; newer posts (Sequence > floor) fall through and forward.
						cursor = InitialCursor(read.Messages.Select(m => (m.Sequence, m.TimestampUtc)), DateTime.UtcNow, _backlogGrace);
						await Console.Error.WriteLineAsync($"[faolan-chat] connected; backlog floor sequence {cursor}, forwarding posts newer than {_backlogGrace.TotalSeconds:0}s");
					}

					foreach (var message in read.Messages.Where(m => !m.FromClaude && m.Sequence > cursor.Value))
					{
						await server.SendNotificationAsync(ChannelNotificationMethod, new ChannelEvent
						{
							Content = message.Message ?? "",
							Meta = new Dictionary<string, string>
							{
								["sender"] = message.SenderName ?? "",
								["sender_id"] = message.SenderId.ToString(),
								["sequence"] = message.Sequence.ToString(),
								// The inbound mode, so Claude replies in kind: "whisper" (a private /tell to Claude)
								// or "channel" (a public post). #97.
								["mode"] = message.ViaWhisper
									? "whisper"
									: "channel"
							}
						}, cancellationToken: cancellationToken);
					}

					cursor = Math.Max(cursor.Value, read.Latest);
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

		[Description("Reply in-game as \"Claude\": a private whisper to one player (pass whisper_to = the channel event's sender_id) or a broadcast on the Claude channel (omit whisper_to).")]
		private static async Task<string> Reply([Description("The chat message to send in-game.")] string text,
			[Description("Character id to whisper privately (the sender_id from the channel event). Omit to broadcast on the channel.")] string? whisper_to = null)
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

		private static string? ResultText(CallToolResult result)
		{
			return result?.Content?.OfType<TextContentBlock>().FirstOrDefault()?.Text;
		}

		private static async Task<string> SendInGame(string text, string? whisperTo)
		{
			var client = await GameServer(CancellationToken.None);
			var result = uint.TryParse(whisperTo, out var characterId)
				? await client.CallToolAsync("send_claude_whisper", new Dictionary<string, object?>
				{
					["characterId"] = characterId,
					["message"] = text
				})
				: await client.CallToolAsync("send_claude_channel_message", new Dictionary<string, object?>
				{
					["message"] = text
				});
			return ResultText(result) ?? "sent";
		}

		private sealed class ChannelMessage
		{
			public uint SenderId { get; set; }
			public DateTime TimestampUtc { get; set; }
			public bool FromClaude { get; set; }
			public bool ViaWhisper { get; set; }
			public string Message { get; } = null!;
			public string SenderName { get; } = null!;
			public ulong Sequence { get; set; }
		}

		private sealed class ChannelEvent
		{
			[JsonPropertyName("meta")]
			public Dictionary<string, string> Meta { get; set; } = null!;

			[JsonPropertyName("content")]
			public string Content { get; set; } = null!;
		}
	}
}
