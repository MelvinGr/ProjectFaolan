using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace Faolan.Core.Cli
{
	/// <summary>
	///     Sets ONE key in faolan.user.json, in place, leaving the rest of the file - including its comments - exactly
	///     as it was.
	/// </summary>
	/// <remarks>
	///     <para>
	///         The file is edited as TEXT rather than round-tripped through a JSON serializer, and that is the whole
	///         point: <c>JsonNode</c> parses this file happily but drops every <c>//</c> comment on the way out, and
	///         those comments are why anyone can tell what the machine-specific overrides in it are FOR. A writer that
	///         silently strips them is worse than no writer.
	///     </para>
	///     <para>
	///         It is deliberately narrow - one key, one string value, no deletes, no arrays, no nesting past a
	///         section. The only caller is <c>--generate-login-key</c>, and a general JSON-with-comments editor is a
	///         much larger thing than the job needs.
	///     </para>
	/// </remarks>
	public static class UserConfigWriter
	{
		private const string EmptyFile = """
		                                 // Per-machine overrides, layered on top of faolan.json (Global.UserConfigPath). GITIGNORED - keep anything
		                                 // that is only true of this checkout here, so the committed faolan.json stays machine-agnostic.
		                                 {
		                                 }
		                                 """;

		/// <summary>
		///     Writes <paramref name="value" /> at <paramref name="key" /> (a catalog key, flat or
		///     <c>Section:Name</c>), creating the file, the section or the entry as needed.
		/// </summary>
		/// <param name="overwrite">
		///     Whether an existing value may be replaced. False - the default - throws instead, because the one thing
		///     this writer must never do is silently discard a key somebody set by hand.
		/// </param>
		/// <exception cref="InvalidOperationException">The key is already set and <paramref name="overwrite" /> is false.</exception>
		public static void Set(string path, string key, string value, bool overwrite = false)
		{
			FaolanSettings.Get(key); // a key nobody catalogued is a typo, and it would be written where nothing reads it

			var text = File.Exists(path) ? File.ReadAllText(path) : EmptyFile;
			var colon = key.IndexOf(':');
			var name = key[(colon + 1)..];

			// The span to look in and insert at: the section's braces, or the root object for a flat key. Creating a
			// missing section is part of resolving it, so from here on the span always exists.
			var (start, end) = colon < 0 ? (text.IndexOf('{') + 1, text.Length) : Section(ref text, key[..colon]);

			var existing = new Regex($"""^([ \t]*)"{Regex.Escape(name)}"[ \t]*:[ \t]*(.+?)(,?)[ \t]*$""", RegexOptions.Multiline)
				.Match(text, start, end - start);

			if (existing.Success)
			{
				if (!overwrite)
					throw new InvalidOperationException($"'{key}' is already set in {path} (to {existing.Groups[2].Value}). Pass --force to replace it.");

				var replaced = $"{existing.Groups[1].Value}\"{name}\": \"{value}\"{existing.Groups[3].Value}";
				Write(path, text[..existing.Index] + replaced + text[(existing.Index + existing.Length)..], key, value);

				return;
			}

			// A new entry goes FIRST in its object rather than last, because the last entry is the one whose trailing
			// comma has to be there or not - inserting at the front leaves that somebody else's line, untouched.
			Write(path, Insert(text, start, $"\"{name}\": \"{value}\"", colon < 0 ? "\t" : "\t\t", end), key, value);
		}

		// Nothing reaches disk until the configuration reader itself agrees with it: the edited text must still PARSE,
		// and reading `key` back out of it must give `value`.
		//
		// That check is what makes editing this file as text safe. The edit is textual because a JsonNode round-trip
		// would drop every comment (see above), but the VERIFICATION does not have to be - so the failure mode of the
		// regexes is a thrown exception over a file that is still intact, not a config nobody can load.
		private static void Write(string path, string text, string key, string value)
		{
			IConfiguration read;
			try
			{
				read = new ConfigurationBuilder().AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(text))).Build();
			}
			catch (Exception e)
			{
				throw new InvalidOperationException($"Refusing to write {path}: the edited file is not valid JSON ({e.Message}).", e);
			}

			if (read[key] != value)
				throw new InvalidOperationException($"Refusing to write {path}: '{key}' reads back as '{read[key] ?? "<absent>"}' rather than the value just set.");

			File.WriteAllText(path, text.EndsWith('\n') ? text : text + "\n");
		}

		// The inside of `"Section": { … }`, brace to closing brace, creating the section if it is not there.
		private static (int Start, int End) Section(ref string text, string section)
		{
			// $$ so a literal `{` stays literal - this pattern ends in one.
			var opening = new Regex($$"""^[ \t]*"{{Regex.Escape(section)}}"[ \t]*:[ \t]*\{""", RegexOptions.Multiline).Match(text);
			if (!opening.Success)
			{
				var root = text.IndexOf('{') + 1;
				text = Insert(text, root, "\"" + section + "\": {\n\t}", "\t", text.Length);

				return Section(ref text, section);
			}

			var close = text.IndexOf('}', opening.Index + opening.Length);
			if (close < 0)
				throw new InvalidOperationException($"The \"{section}\" section in the user config is not closed.");

			return (opening.Index + opening.Length, close);
		}

		// Puts `entry` at the front of the object whose body starts at `at`, with the comma it needs there and not
		// otherwise: a `//` line is not an entry, which is what a naive "is the next character a }" check gets wrong
		// on a file whose whole style is commented keys.
		private static string Insert(string text, int at, string entry, string indent, int end)
		{
			var follows = false;
			foreach (var line in text[at..end].Split('\n'))
			{
				var trimmed = line.Trim();
				if (trimmed.Length == 0 || trimmed.StartsWith("//"))
					continue;

				follows = trimmed[0] != '}';

				break;
			}

			return text[..at] + $"\n{indent}{entry}{(follows ? "," : "")}\n" + text[at..].TrimStart('\n');
		}
	}
}
