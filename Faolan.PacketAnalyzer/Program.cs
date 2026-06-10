using System;
using System.IO;
using Avalonia;

namespace Faolan.PacketAnalyzer
{
	public static class Program
	{
		// Avalonia configuration, don't remove; also used by visual designer.
		public static AppBuilder BuildAvaloniaApp()
		{
			return AppBuilder
				.Configure<App>()
				.UsePlatformDetect()
				.WithInterFont()
				.LogToTrace();
		}

		// Initialization code. Don't use any Avalonia, third-party APIs or any
		// SynchronizationContext-reliant code before AppMain is called: things aren't initialized
		// yet and stuff might break.
		[STAThread]
		public static int Main(string[] args)
		{
			//args = ["--dump", "/home/melvin/Development/C#/Project Faolan/Captures/account-stricken997-character-Strie-create.pcapng"];

			if (TryGetDumpPath(args, out var dumpPath))
				return DumpCapture(dumpPath);

			BuildAvaloniaApp()
				.StartWithClassicDesktopLifetime(args);
			return 0;
		}

		private static int DumpCapture(string path)
		{
			if (string.IsNullOrWhiteSpace(path))
			{
				Console.Error.WriteLine("Usage: Faolan.PacketAnalyzer --dump <capture-file>");
				return 1;
			}

			if (!File.Exists(path))
			{
				Console.Error.WriteLine($"Capture file not found: {path}");
				return 1;
			}

			try
			{
				var analysisService = new PacketAnalysisService();
				var dumpFileService = new PacketDumpFileService();
				var analysisResult = analysisService.AnalyzeCapture(path);
				Console.Out.WriteLine(dumpFileService.Serialize(Path.GetFileName(path), analysisResult.PortSettings, analysisResult.Packets));
				return 0;
			}
			catch (Exception e)
			{
				Console.Error.WriteLine(e.Message);
				Console.Error.WriteLine(e.StackTrace);
				return 1;
			}
		}

		private static bool TryGetDumpPath(string[] args, out string path)
		{
			path = string.Empty;

			if (args.Length == 0)
				return false;

			for (var i = 0; i < args.Length; i++)
			{
				var arg = args[i];
				if (!string.Equals(arg, "--dump", StringComparison.OrdinalIgnoreCase))
				{
					if (!arg.StartsWith("--dump=", StringComparison.OrdinalIgnoreCase))
						continue;

					path = arg["--dump=".Length..];
					return true;
				}

				if (i + 1 >= args.Length)
					return true;

				path = args[i + 1];
				return true;
			}

			return false;
		}
	}
}
