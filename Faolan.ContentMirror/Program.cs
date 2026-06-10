using System.Collections.Concurrent;
using System.Xml.Linq;

// Faolan.ContentMirror - a store-and-forward mirror of Age of Conan's UPM content CDN.
//
// AoC's patcher + client pull immutable, content-addressed blobs from three hosts, all under /upm:
//   aoc-eu-update.live      /upm/client/<md5>      client files   (HttpPatchAddr)        -> tag "update"
//   aoc-eu-update.live      /upm/rdb/pack/<md5>    bulk RDB packs  (HttpPatchAddr)        -> tag "update"
//   aoc-eu-gameupdate.live  /upm/rdb/res/<md5>     on-demand RDB   (ClientHttpPatchAddr)  -> tag "gameupdate"
//   aoc-eu-control.live     /upm/control/...       manifest        (ControlHttpPatchAddr) -> tag "control"
// The last path segment IS the md5 of the (compressed) payload, so the store is self-verifying and dedup-free.
//
// We redirect those three LocalConfig.xml keys at this server as http://127.0.0.1:<port>/<tag>/upm, so the request
// path carries the upstream tag. On a miss we fetch the real CDN once (HTTP identity, raw bytes), store it under
// store/<tag>/upm/<rest>, and serve it; on a hit we serve straight from disk - no upstream, no timeout. Running the
// real ConanPatcher through it captures the whole current revision into store/, a server-independent archive.

namespace Faolan.ContentMirror
{
    public static class Program
    {
        private static readonly IReadOnlyDictionary<string, string> Upstreams = new Dictionary<string, string>
        {
            ["update"] = "http://aoc-eu-update.live.ageofconan.com",
            ["gameupdate"] = "http://aoc-eu-gameupdate.live.ageofconan.com",
            ["control"] = "http://aoc-eu-control.live.ageofconan.com",
        };

        // LocalConfig.xml keys we rewrite, paired with the tag they map to.
        private static readonly (string Key, string Tag)[] ConfigKeys =
        [
            ("HttpPatchAddr", "update"),
            ("ClientHttpPatchAddr", "gameupdate"),
            ("ControlHttpPatchAddr", "control"),
        ];

        public static int Main(string[] args)
        {
            // Config-patch helpers run and exit; no web host.
            if (args.Length >= 1 && args[0] is "--patch" or "--restore")
                return PatchConfig(args);

            var port = GetOption(args, "--port") is { } p ? int.Parse(p) : 7080;
            var storeDir = Path.GetFullPath(GetOption(args, "--store") ?? "mirror-store");
            Directory.CreateDirectory(storeDir);

            var builder = WebApplication.CreateBuilder(args);
            builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
            // Don't let HttpClient transparently gunzip: we store and re-serve the exact payload bytes the client's
            // own decompressor expects. Requesting identity upstream keeps HTTP out of the picture entirely.
            builder.Services.AddHttpClient("cdn")
                .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AutomaticDecompression = System.Net.DecompressionMethods.None });

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
                var isConfig = tag == "control" && rest.EndsWith("LocalConfig.xml", StringComparison.OrdinalIgnoreCase);

                if (query.Length == 0 && File.Exists(localPath))
                {
                    Interlocked.Increment(ref hits);
                    return isConfig
                        ? Results.File(RewriteConfig(await File.ReadAllBytesAsync(localPath), port), "application/octet-stream")
                        : Results.File(localPath, "application/octet-stream");
                }

                // Single-flight per path so 5 patcher connections don't all fetch the same blob.
                var gate = inflight.GetOrAdd(localPath, _ => new SemaphoreSlim(1, 1));
                await gate.WaitAsync();
                try
                {
                    if (query.Length == 0 && File.Exists(localPath))
                    {
                        Interlocked.Increment(ref hits);
                        return isConfig
                            ? Results.File(RewriteConfig(await File.ReadAllBytesAsync(localPath), port), "application/octet-stream")
                            : Results.File(localPath, "application/octet-stream");
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
                        File.Move(tmp, localPath, overwrite: true);
                    }

                    var m = Interlocked.Increment(ref misses);
                    var b = Interlocked.Add(ref bytes, body.Length);
                    if (m % 100 == 0)
                        log.LogInformation("progress: {Misses} stored, {Hits} hits, {MB:F1} MB", m, Interlocked.Read(ref hits), b / 1048576.0);
                    log.LogInformation("MISS {Tag} {Rest} ({Size}b)", tag, rest, body.Length);
                    return Results.File(isConfig ? RewriteConfig(body, port) : body, "application/octet-stream");
                }
                finally
                {
                    gate.Release();
                }
            });

            app.MapGet("/_stats", () => Results.Ok(new { hits = Interlocked.Read(ref hits), stored = Interlocked.Read(ref misses), mb = Interlocked.Read(ref bytes) / 1048576.0, storeDir }));

            log.LogInformation("Faolan.ContentMirror on http://127.0.0.1:{Port}  store={Store}", port, storeDir);
            log.LogInformation("Patch a client: dotnet run --project Faolan.ContentMirror -- --patch \"<AoC dir>\" --port {Port}", port);
            app.Run();
            return 0;
        }

        // Rewrite (or restore) the three patch-address keys in <AoC>/LocalConfig.xml. The patcher/client read these
        // locally at startup and LocalConfig.xml is not in the file manifest, so the redirect survives a patch run.
        private static int PatchConfig(string[] args)
        {
            var restore = args[0] == "--restore";
            var aocDir = args.Length >= 2 && !args[1].StartsWith("--") ? args[1] : Directory.GetCurrentDirectory();
            var cfg = Path.Combine(aocDir, "LocalConfig.xml");
            var backup = cfg + ".faolan-bak";

            if (restore)
            {
                if (File.Exists(backup))
                {
                    File.Copy(backup, cfg, overwrite: true);
                    File.Delete(backup);
                    Console.WriteLine($"restored {cfg}");
                }
                else
                {
                    Console.WriteLine("no backup found; nothing to restore");
                }
                return 0;
            }

            if (!File.Exists(cfg))
            {
                Console.Error.WriteLine($"not found: {cfg}");
                return 1;
            }

            var port = GetOption(args, "--port") ?? "7080";
            if (!File.Exists(backup))
                File.Copy(cfg, backup);

            var doc = XDocument.Load(cfg);
            foreach (var (key, tag) in ConfigKeys)
            {
                var el = doc.Descendants(key).FirstOrDefault();
                if (el == null)
                {
                    Console.Error.WriteLine($"warning: <{key}> not found in config");
                    continue;
                }

                el.Value = $"http://127.0.0.1:{port}/{tag}/upm";
                Console.WriteLine($"{key} -> {el.Value}");
            }

            doc.Save(cfg);
            Console.WriteLine($"patched {cfg} (backup at {backup}); run --restore to revert");
            return 0;
        }

        // Replace the three real CDN /upm bases in a served LocalConfig.xml with this mirror, so a patcher that
        // overwrites its local config from the control server stays pointed at us.
        private static byte[] RewriteConfig(byte[] body, int port)
        {
            var text = System.Text.Encoding.UTF8.GetString(body);
            foreach (var (tag, host) in Upstreams)
                text = text.Replace($"{host}/upm", $"http://127.0.0.1:{port}/{tag}/upm");
            return System.Text.Encoding.UTF8.GetBytes(text);
        }

        private static string? GetOption(string[] args, string name)
        {
            var i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }
    }
}
