using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using Faolan.Core.Cli;
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
// <mirror>/<tag>/upm, so the request path carries the upstream tag. On a miss we fetch the real CDN
// once (HTTP identity, raw bytes), store it under store/<tag>/upm/<rest>, and serve it; on a hit we serve straight
// from disk - no upstream, no timeout. Running the real ConanPatcher through it captures the whole current revision
// into store/, a server-independent archive.

namespace Faolan.Web.Content
{
	public static class Program
	{
		private static readonly ImmutableDictionary<string, string> Upstreams = new Dictionary<string, string>
		{
			["update"] = "http://aoc-eu-update.live.ageofconan.com",
			["gameupdate"] = "http://aoc-eu-gameupdate.live.ageofconan.com",
			["control"] = "http://aoc-eu-control.live.ageofconan.com"
		}.ToImmutableDictionary();

		public static int Main(string[] args)
		{
			var builder = WebApplication.CreateBuilder(args);

			// Config precedence (highest wins): environment variables > command line > faolan.json, through the shared
			// builder so it matches the server and the importer exactly. faolan.json is OPTIONAL - the mirror runs on
			// its built-in defaults with no file. The command-line key IS the config path (`--CdnMirror:Store <dir>`),
			// declared in MirrorCommand. Sources are cleared first so the order is exactly this (no appsettings.json).
			var overrides = MirrorCommand.Overrides(args, out var parseExit);
			if (overrides == null)
				return parseExit;

			builder.Configuration.Sources.Clear();
			builder.Configuration.AddConfiguration(FaolanCommandLine.Build(overrides));

			var listenAddress = builder.Configuration.CdnMirrorListenAddress;
			var port = builder.Configuration.CdnMirrorPort;
			var storeDir = Path.GetFullPath(builder.Configuration.CdnMirrorStore);
			Directory.CreateDirectory(storeDir);

			// The address the CLIENT dials, from configuration and nowhere else. It used to fall back to the inbound
			// request's Host header, which reads as "by definition what the client dialled" and is a GUESS: it is
			// whatever the last hop put there, so a proxy, a container port mapping or a plain `curl -H Host:` writes
			// that value into the LocalConfig.xml a patch run then pins itself to - a wrong patch address that looks
			// like it worked. Only the operator knows the answer, so an unset key is an error, not a derivation.
			var publicBase = builder.Configuration.CdnMirrorPublicAddress;
			if (string.IsNullOrWhiteSpace(publicBase))
			{
				Console.Error.WriteLine(
					"Missing required configuration 'CdnMirror:PublicAddress' - the address a CLIENT reaches this mirror on " +
					$"(e.g. http://192.168.1.5:{port}), written into the patch addresses of every LocalConfig.xml served. " +
					"Set it in faolan.json / faolan.user.json, in the environment as CdnMirror__PublicAddress, or on the " +
					"command line as --CdnMirror:PublicAddress.");
				return 1;
			}

			publicBase = PublicBase(publicBase);

			builder.WebHost.UseUrls($"http://{listenAddress}:{port}");
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

			// What the store already HOLDS per upstream tag - blob count and total size. Walked ONCE here (a fully
			// mirrored store is ~100k files, so /_stats must not re-walk it per request) and then kept live by the
			// store path below - every miss that lands a blob adds it to its tag's tally.
			var store = new ConcurrentDictionary<string, StoreTally>(StringComparer.Ordinal);
			foreach (var tag in Upstreams.Keys)
				store[tag] = ScanStore(Path.Combine(storeDir, tag, "upm"));

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
					return Results.File(RewriteConfig(await File.ReadAllBytesAsync(localPath), publicBase), "application/octet-stream");

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
						// The blob is new to the store unconditionally: the only way past the two hit checks with no
						// query string is File.Exists having said no, under the single-flight gate for this exact path.
						// (It cost a third File.Exists to ask, and the answer was always the same.)
						store[tag].Add(body.Length);
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

			// `stored`/`mb` count what THIS run fetched; `files`/`storeMb` are the whole store, startup scan included.
			app.MapGet("/_stats", () =>
			{
				var byTag = new SortedDictionary<string, object>(StringComparer.Ordinal);
				foreach (var (tag, tally) in store)
					byTag[tag] = new { files = tally.Files, mb = tally.Bytes / 1024f / 1024f };

				return Results.Ok(new
				{
					hits = Interlocked.Read(ref hits),
					stored = Interlocked.Read(ref misses),
					mb = Interlocked.Read(ref bytes) / 1024f / 1024f,
					storeDir,
					files = store.Values.Sum(t => t.Files),
					storeMb = store.Values.Sum(t => t.Bytes) / 1024f / 1024f,
					storeByTag = byTag
				});
			});

			//app.MapGet("launchernotes2.php", () => Results.Ok(Global.StartupBanner));

			log.LogInformation("Faolan.Web.Content on http://{ListenAddress}:{Port} store={Store} ({Files} files, {MB:F1} MB: {ByTag}) patch-addr={PatchAddr}",
				listenAddress, port, storeDir, store.Values.Sum(t => t.Files), store.Values.Sum(t => t.Bytes) / 1024f / 1024f,
				string.Join(", ", store.OrderBy(s => s.Key, StringComparer.Ordinal).Select(s => $"{s.Key}={s.Value.Files}/{s.Value.Bytes / 1024f / 1024f:F1}MB")),
				publicBase);
			app.Run();
			return 0;
		}

		// Blobs stored under store/<tag>/upm/<rest>: how many, and how many bytes they occupy. A `<name>.tmp<threadid>`
		// is a write still in flight (or one a crash left behind), not a stored blob - a real name is the payload's
		// md5, so it never carries ".tmp". Enumerated as FileInfo rather than paths + a File.Length per hit: the
		// length comes off the directory entry the walk already read, so the size costs no extra syscall.
		internal static StoreTally ScanStore(string dir)
		{
			var tally = new StoreTally();
			if (!Directory.Exists(dir))
				return tally;

			foreach (var file in new DirectoryInfo(dir).EnumerateFiles("*", SearchOption.AllDirectories))
				if (!file.Name.AsSpan().Contains(".tmp", StringComparison.Ordinal))
					tally.Add(file.Length);

			return tally;
		}

		// Replace the three real CDN /upm bases in a served LocalConfig.xml with this mirror, so a patcher that
		// overwrites its local config from the control server stays pointed at us.
		// Rewrite the three upstream patch addresses to point back at this mirror. `publicBase` is the address the
		// CLIENT will dial - see PublicBase.
		internal static byte[] RewriteConfig(byte[] body, string publicBase)
		{
			var text = Encoding.UTF8.GetString(body);
			foreach (var (tag, host) in Upstreams)
				text = text.Replace($"{host}/upm", $"{publicBase}/{tag}/upm");

			// TODO: Also replace UniverseAddr with Config.ServerIpAddress:Config.UniverseAgentPort

			return Encoding.UTF8.GetBytes(text);
		}

		// The configured CdnMirror:PublicAddress, normalized to a base URL to concatenate onto. It cannot be derived
		// from the bind address (in a container CdnMirror:ListenAddress is 0.0.0.0 and a client on the Docker host dials a
		// published port) nor from the request (see the caller), so this only tidies what the operator wrote: a bare
		// `host` or `host:port` gets the scheme it omitted, and a trailing slash comes off because a path follows.
		internal static string PublicBase(string configured)
		{
			var value = configured.Trim().TrimEnd('/');
			return value.Contains("://", StringComparison.Ordinal) ? value : $"http://{value}";
		}
	}
}
