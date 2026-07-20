"""Where the capture corpus lives.

The retail traces are NOT in this repo - they sit in the private ProjectFaolan-Data repo (see
CLAUDE.md "Capture data"), because they are 349 MB and this repo stays small. The C# test suite
does not need them at all: it reads the committed corpus extract. These dissectors do.

Resolution order, first hit wins:

  1. FAOLAN_CAPTURES        environment variable, for a clone anywhere
  2. <repo>/Captures        a symlink, if you keep one

Nothing is guessed. If neither exists we raise and say what to set - a script that silently
globbed an empty directory would just print "0 packets" and look like a decode failure.

Usage:

    from captures import capture_files, capture

    for path in capture_files("*.tshark.json"):   # sorted, absolute
        ...
    path = capture("NecroL80_..._login_summon_pet.tshark.json")
"""

import os
import sys
from pathlib import Path

# this file is at tools/capture/, so the repo root is two levels up
ROOT = Path(__file__).resolve().parent.parent.parent

ENV_VAR = "FAOLAN_CAPTURES"


def captures_dir() -> Path:
    """The capture corpus directory, or raise with instructions."""
    from_env = os.environ.get(ENV_VAR)
    if from_env:
        path = Path(from_env).expanduser()
        if not path.is_dir():
            raise SystemExit(f"{ENV_VAR}={from_env} is not a directory")
        return path

    local = ROOT / "Captures"
    if local.is_dir():
        return local

    raise SystemExit(
        "Capture corpus not found.\n"
        "  The traces are NOT in this repo - they live in the private ProjectFaolan-Data repo.\n"
        f"  Point at them with:  export {ENV_VAR}=<data-repo>/Captures\n"
        f"  (or symlink {local} at it)"
    )


def capture_files(pattern: str = "*.tshark.json"):
    """Every capture matching `pattern`, sorted, as absolute path STRINGS.

    Strings, not Path, deliberately: these replaced `glob.glob(...)` calls and the dissectors do
    string things with the result (`f.split("/")[-1]`, `.endswith`, string formatting). Returning
    Path here breaks them in ways that only show up when you run each script.
    """
    return [str(p) for p in sorted(captures_dir().glob(pattern))]


def capture(name: str) -> str:
    """One named capture, as an absolute path string. Raises if missing."""
    path = captures_dir() / name
    if not path.exists():
        raise SystemExit(f"capture not found: {path}")
    return str(path)


def add_to_path() -> None:
    """Let sibling scripts `import captures` regardless of the caller's CWD."""
    here = str(Path(__file__).resolve().parent)
    if here not in sys.path:
        sys.path.insert(0, here)
