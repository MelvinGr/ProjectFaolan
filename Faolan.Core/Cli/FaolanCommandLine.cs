using System.Collections.Generic;
using System.CommandLine;
using System.Linq;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	public static class FaolanCommandLine
	{
		public static IConfiguration Build()
		{
			return Layered(null);
		}

		public static IConfiguration Build(ParseResult result, params Option[] options)
		{
			return Layered(Overrides(result, options));
		}

		public static ParseResult? Parse(Command command, string[] args, out int exitCode)
		{
			var result = command.Parse(args);
			if (result.Errors.Count > 0 || args.Any(a => a is "--help" or "-h" or "-?"))
			{
				exitCode = result.Invoke();
				return null;
			}

			exitCode = 0;
			return result;
		}

		internal static Dictionary<string, string?> Overrides(ParseResult result, params Option[] options)
		{
			var overrides = new Dictionary<string, string?>();
			foreach (var option in options)
			{
				if (result.GetResult(option) is not { Implicit: false } given)
					continue;

				if (FaolanSettings.Text(given.GetValueOrDefault<object>()) is { Length: > 0 } text)
					overrides[option.Key()] = text;
			}

			return overrides;
		}

		private static IConfiguration Layered(IEnumerable<KeyValuePair<string, string?>>? commandLine)
		{
			return new ConfigurationBuilder()
				.AddInMemoryCollection(FaolanSettings.Defaults)
				.AddJsonFile(Global.ConfigPath, true)
				.AddJsonFile(Global.UserConfigPath, true)
				.AddInMemoryCollection(commandLine ?? [])
				.AddEnvironmentVariables()
				.Build();
		}
	}
}
