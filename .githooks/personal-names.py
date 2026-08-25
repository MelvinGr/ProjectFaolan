#!/usr/bin/env python3
"""Blocks the maintainer's retail identities from entering the repo.

The repo is meant to go public, so the account and character names used to record the retail captures must
not ride along in source, docs or the reviewable seed. The pre-commit hook runs it over the STAGED diff, and
`--tree` audits every tracked file at once.

⭐ THE IDENTITIES ARE NOT IN THIS FILE. They live in an untracked config, because a check that blocks a set
of strings has to spell those strings out and spelling them out in a public repo is the thing it exists to
prevent. What is tracked is everything else - the matching rules, the exemptions, the audit mode and the
guidance - so the guard is reviewable, maintained in one place and survives a re-clone, instead of existing
on exactly one machine. Before this split the whole implementation was untracked and one disk failure from
gone (Gitea #465).

The config is JSON, and the first of these that exists wins:

    $FAOLAN_PERSONAL_NAMES        an explicit path
    .git/hooks/personal-names.json
    .git/personal-names.json

    {
      "names": {"<blocked>": "<the pseudonym to use instead>", ...},
      "capture_ref": "<regex>",            optional - see CAPTURE REFERENCES below
      "capture_example": "<text>"          optional - shown in the failure message
    }

⚠️ With no config the guard SKIPS, loudly. That is the same behaviour a clone without the old script had, and
it is stated rather than silent because "no output" is exactly how an inert guard looks.

Two exemptions carry real information and are therefore deliberate, not loopholes:

  * CAPTURE REFERENCES. A capture is a file in the private data repo and its name is its identity - it is how
    a `[V2: ...]` citation is resolved back to the traffic that proves the claim. Rewriting a dump's file name
    would turn every citation into a dangling pointer, so a token shaped like a capture name is allowed
    anywhere. The shape is narrow on purpose: a C# identifier that merely STARTS with a name is not a capture
    name and is still blocked.
  * RETAIL BYTES. A handful of gates assert what a captured blob physically contains, and the name is inside
    those bytes. Mark that one line with `personal-name-ok` and say why.

    ⚠️ The reason once given here - "a length change shifts every length prefix and breaks the byte-exact
    round-trip" - is not the real constraint, and it made the residue look harder to remove than it is. The
    names in the fixture archive are PROTOBUF length-delimited fields (`--report fixture-names`), so their
    length is a single varint byte and an equal-length substitution ripples nowhere. The constraint that
    actually holds is that those bytes are retail evidence, and rewriting evidence is a decision, not a chore.

Everywhere the name is FREE INPUT - a fixture, a variable, a test method, prose - use the pseudonym the config
gives for it.
"""

import json
import os
import re
import subprocess
import sys

# The line-level escape hatch, for the gates whose expectation IS the captured bytes.
ALLOW_MARKER = "personal-name-ok"

# Paths the rule does not reach.
EXEMPT_PREFIXES = (
	# fixtures.bin holds retail packet bodies verbatim; the names are inside the wire bytes.
	"Faolan.Tests/Assets/",
	# The maintainer's standing call: the characters this imports keep their retail names in the Characters
	# table. Those rows never reach faolan.sql (SqlSeedWriter excludes Characters and everything that FKs to
	# it), so the reviewable seed stays clean while the local play state stays usable.
	"Faolan.Importer/Captures/CaptureCharacterImporter.cs",
	# Frozen historical bundles - a 2015 Google Code SVN export, the Assembla dumps, the retired ClientHook.
	# They are not part of the solution and rewriting one would falsify the artifact. They DO carry the names,
	# so if the repo goes public they are a separate decision: repack or drop them, do not quietly rewrite.
	"Archive/",
	# This file: it describes the rule and must be able to name the marker and the mechanism.
	".githooks/personal-names.py",
)

CONFIG_CANDIDATES = (
	os.environ.get("FAOLAN_PERSONAL_NAMES"),
	os.path.join(".git", "hooks", "personal-names.json"),
	os.path.join(".git", "personal-names.json"),
)


def load_config():
	for path in CONFIG_CANDIDATES:
		if path and os.path.isfile(path):
			with open(path, encoding="utf-8") as fh:
				return json.load(fh), path
	return None, None


def exempt_path(path):
	return any(path.startswith(p) for p in EXEMPT_PREFIXES)


def violations_in(line, name_re, capture_re):
	"""Every blocked occurrence in one line, as (column, matched text)."""
	if ALLOW_MARKER in line:
		return []
	allowed = [m.span() for m in capture_re.finditer(line)] if capture_re else []
	out = []
	for m in name_re.finditer(line):
		if any(a <= m.start() and m.end() <= b for a, b in allowed):
			continue
		out.append((m.start() + 1, m.group(0)))
	return out


def git(*args):
	return subprocess.run(["git", *args], capture_output=True, text=True, errors="replace").stdout


def staged_hits(name_re, capture_re):
	"""Blocked names on lines the commit ADDS. Pre-existing content elsewhere in a touched file is not the
	commit's business; binary files never produce +lines, so faolan.db is skipped by construction."""
	hits = []
	path = None
	for line in git("diff", "--cached", "-U0", "--no-color", "--diff-filter=ACMR").splitlines():
		if line.startswith("+++ b/"):
			path = line[6:]
		elif line.startswith("+") and not line.startswith("+++") and path and not exempt_path(path):
			for col, text in violations_in(line[1:], name_re, capture_re):
				hits.append((path, "+", col, text, line[1:].strip()))
	return hits


def tree_hits(name_re, capture_re):
	"""Every blocked name in every tracked text file - the audit the one-off sweep runs."""
	hits = []
	for path in git("ls-files").splitlines():
		if exempt_path(path):
			continue
		try:
			with open(path, encoding="utf-8", errors="strict") as fh:
				lines = fh.readlines()
		except (OSError, UnicodeDecodeError):
			continue  # binary or unreadable: faolan.db and the fixture archive land here
		for n, line in enumerate(lines, 1):
			for col, text in violations_in(line, name_re, capture_re):
				hits.append((path, str(n), col, text, line.strip()))
	return hits


def main():
	config, config_path = load_config()
	if config is None:
		print("personal-names: no identity config found - the guard is NOT running in this clone.", file=sys.stderr)
		print("  Expected one of: $FAOLAN_PERSONAL_NAMES, .git/hooks/personal-names.json, .git/personal-names.json", file=sys.stderr)
		print('  Shape: {"names": {"<blocked>": "<pseudonym>"}, "capture_ref": "<regex>", "capture_example": "<text>"}', file=sys.stderr)
		return 0

	names = config.get("names") or {}
	if not names:
		print(f"personal-names: {config_path} carries no names; nothing to block.", file=sys.stderr)
		return 0

	# Matched case-insensitively and as SUBSTRINGS, so a half-scrubbed identifier is caught too - a pseudonym
	# is cheap and `<name>Id` is exactly what a substring rule is for. Longest first so the message names the
	# most specific identity when two of them overlap.
	ordered = sorted(names, key=len, reverse=True)
	name_re = re.compile("|".join(re.escape(n) for n in ordered), re.IGNORECASE)
	capture_re = re.compile(config["capture_ref"], re.IGNORECASE) if config.get("capture_ref") else None

	tree = "--tree" in sys.argv[1:]
	hits = tree_hits(name_re, capture_re) if tree else staged_hits(name_re, capture_re)
	if not hits:
		return 0

	where = "tracked files" if tree else "the staged changes"
	print(f"personal-names: blocked name(s) in {where}:", file=sys.stderr)
	print("", file=sys.stderr)
	for path, loc, col, text, line in hits:
		print(f"  {path}:{loc}:{col}: {text}", file=sys.stderr)
		print(f"      {line[:140]}", file=sys.stderr)
	print("", file=sys.stderr)
	print("These are the maintainer's retail account/character names; the repo is meant to go public.", file=sys.stderr)
	print("Use the pseudonym the repo already uses:", file=sys.stderr)
	for name in ordered:
		print(f"  {name:9s} -> {names[name]}", file=sys.stderr)
	print("", file=sys.stderr)
	if capture_re:
		print("A capture file name is exempt and needs no change - cite it in full, not as a bare name.", file=sys.stderr)
		if config.get("capture_example"):
			print(f"  e.g. {config['capture_example']}", file=sys.stderr)
	print(f"If the name is inside captured BYTES a gate asserts on, add `{ALLOW_MARKER}` to that line with a reason.", file=sys.stderr)
	print("Audit the whole tree with: .githooks/personal-names.py --tree", file=sys.stderr)
	return 1


if __name__ == "__main__":
	sys.exit(main())
