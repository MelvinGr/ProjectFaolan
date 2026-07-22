using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Faolan.Core;
using Faolan.Core.Extensions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

// Faolan.Web.Content - a store-and-forward mirror of Age of Conan's UPM content CDN.
//
// AoC's patcher + client pull immutable, content-addressed blobs from three hosts, all under /upm:
//   aoc-eu-update.live      /upm/client/<md5>      client files   (HttpPatchAddr)        -> tag "update"
//   aoc-eu-update.live      /upm/rdb/pack/<md5>    bulk RDB packs  (HttpPatchAddr)        -> tag "update"
//   aoc-eu-gameupdate.live  /upm/rdb/res/<md5>     on-demand RDB   (ClientHttpPatchAddr)  -> tag "gameupdate"
//   aoc-eu-control.live     /upm/control/...       manifest        (ControlHttpPatchAddr) -> tag "control"
// The last path segment IS the md5 of the (compressed) payload, so the store is self-verifying and dedup-free.
//
// Faolan.ClientLauncher --mirror redirects those three LocalConfig.xml keys at this server as
// http://127.0.0.1:<port>/<tag>/upm, so the request path carries the upstream tag. On a miss we fetch the real CDN
// once (HTTP identity, raw bytes), store it under store/<tag>/upm/<rest>, and serve it; on a hit we serve straight
// from disk - no upstream, no timeout. Running the real ConanPatcher through it captures the whole current revision
// into store/, a server-independent archive.

namespace Faolan.Web.Content
{
	public static class Program
	{
		private static readonly ImmutableDictionary<string, string> Upstreams = new Dictionary<string, string>
		{
			["update"] = AocContentCdn.Update,
			["gameupdate"] = AocContentCdn.GameUpdate,
			["control"] = AocContentCdn.Control
		}.ToImmutableDictionary();

		public static int Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			// Config precedence (highest wins): environment variables > command line > faolan.json. faolan.json is
			// OPTIONAL - the mirror runs on the built-in defaults with no file (parity with the server / Faolan.Web).
			// The --store/--ip/--port switches are mapped onto the CdnMirror* keys so the existing launch scripts keep
			// working (as the command-line layer, overriding faolan.json but below env vars). Sources are cleared first
			// so the order is exactly this (no appsettings.json in the mix).
			builder.Configuration.Sources.Clear();
			builder.Configuration.AddJsonFile(RepoPaths.Combine("faolan.json"), optional: true); // repo-root-relative (CWD-independent), like Faolan.Web
			builder.Configuration.AddCommandLine(args, new Dictionary<string, string>
			{
				["--store"] = "CdnMirrorStore",
				["--ip"] = "CdnMirrorIp",
				["--port"] = "CdnMirrorPort"
			});
			builder.Configuration.AddEnvironmentVariables();

			var ip = builder.Configuration.CdnMirrorIp;
			var port = builder.Configuration.CdnMirrorPort;
			var storeDir = Path.GetFullPath(builder.Configuration.CdnMirrorStore);
			Directory.CreateDirectory(storeDir);

			builder.WebHost.UseUrls($"http://{ip}:{port}");
			// Don't let HttpClient transparently gunzip: we store and re-serve the exact payload bytes the client's
			// own decompressor expects. Requesting identity upstream keeps HTTP out of the picture entirely.
			builder
				.Services.AddHttpClient("cdn")
				.ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
				{
					AutomaticDecompression = DecompressionMethods.None
				});

			var app = builder.Build();
			var log = app.Logger;
			long hits = 0, misses = 0, bytes = 0;
			var inflight = new ConcurrentDictionary<string, SemaphoreSlim>();

			app.MapGet("/{tag}/upm/{**rest}", async (string tag, string rest, HttpContext ctx, IHttpClientFactory http) =>
			{
				if (!Upstreams.TryGetValue(tag, out var upstreamBase))
					return Results.NotFound($"unknown tag '{tag}'");

				var query = ctx.Request.QueryString.Value ?? "";
				var localPath = Path.Combine(storeDir, tag, "upm", rest.Replace('/', Path.DirectorySeparatorChar));

				// The patcher fetches <ControlHttpPatchAddr>/<folder>/LocalConfig.xml and OVERWRITES the local copy
				// with it - which would revert our redirect to the real CDN. So for that file we archive the real
				// bytes but serve a copy whose three patch-addr keys point back at this mirror, keeping the redirect
				// sticky across a patch run.
				if (tag == "control" && rest.EndsWith("LocalConfig.xml", StringComparison.OrdinalIgnoreCase))
					return Results.File(RewriteConfig(await File.ReadAllBytesAsync(localPath), port), "application/octet-stream");

				if (query.Length == 0 && File.Exists(localPath))
				{
					Interlocked.Increment(ref hits);
					return Results.File(localPath, "application/octet-stream");
				}

				// Single-flight per path so 5 patcher connections don't all fetch the same blob.
				var gate = inflight.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
				await gate.WaitAsync();

				try
				{
					if (query.Length == 0 && File.Exists(localPath))
					{
						Interlocked.Increment(ref hits);
						return Results.File(localPath, "application/octet-stream");
					}

					var url = $"{upstreamBase}/upm/{rest}{query}";

					var client = http.CreateClient("cdn");
					client.DefaultRequestHeaders.AcceptEncoding.ParseAdd("identity");

					var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
					if (!resp.IsSuccessStatusCode)
					{
						log.LogWarning("UPSTREAM {Status} {Url}", (int)resp.StatusCode, url);
						return Results.StatusCode((int)resp.StatusCode);
					}

					var body = await resp.Content.ReadAsByteArrayAsync();
					if (query.Length == 0) // never cache query responses (manifest probes etc.)
					{
						Directory.CreateDirectory(Path.GetDirectoryName(localPath)!);
						var tmp = localPath + ".tmp" + Environment.CurrentManagedThreadId;
						await File.WriteAllBytesAsync(tmp, body);
						File.Move(tmp, localPath, true);
					}

					var m = Interlocked.Increment(ref misses);
					var b = Interlocked.Add(ref bytes, body.Length);
					if (m % 100 == 0)
						log.LogInformation("progress: {Misses} stored, {Hits} hits, {MB:F1} MB", m, Interlocked.Read(ref hits), b / 1024f / 1024f);

					log.LogInformation("MISS {Tag} {Rest} ({Size}b)", tag, rest, body.Length);
					return Results.File(body, "application/octet-stream");
				}
				finally
				{
					gate.Release();
				}
			});

			app.MapGet("/_stats", () => Results.Ok(new
			{
				hits = Interlocked.Read(ref hits),
				stored = Interlocked.Read(ref misses),
				mb = Interlocked.Read(ref bytes) / 1024f / 1024f,
				storeDir
			}));

			//app.MapGet("launchernotes2.php", () => Results.Ok(Global.StartupBanner));

			log.LogInformation("Faolan.Web.Content on http://{ip}:{Port} store={Store}", ip, port, storeDir);
			app.Run();
			return 0;
		}

		// Replace the three real CDN /upm bases in a served LocalConfig.xml with this mirror, so a patcher that
		// overwrites its local config from the control server stays pointed at us.
		private static byte[] RewriteConfig(byte[] body, int port)
		{
			var text = Encoding.UTF8.GetString(body);
			foreach (var (tag, host) in Upstreams)
				text = text.Replace($"{host}/upm", $"http://127.0.0.1:{port}/{tag}/upm"); // TODO: External IP
			return Encoding.UTF8.GetBytes(text);
		}
	}
}
