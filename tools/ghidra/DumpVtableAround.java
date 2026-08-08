// Prints the vtable that CONTAINS a given slot address, by walking back to its start and forward to its end.
// Written for #714 - the MoveTask decode pinned the READER of a PathMoveTask but not what EXECUTES it, and the
// executor is the reader's sibling: both are virtual slots of the same class, so the vtable is the shortest path
// from "I know who parses this record" to "I know who acts on it".
//
// A vtable here is a run of consecutive pointers-to-function; the run ends at the first dword that is not one
// (RTTI, padding, or another class's data). That is the whole heuristic - deliberately simple, because a wrong
// boundary just prints a neighbouring class's slots rather than hiding one.
//
//   SLOT=01ff53a4 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpVtableAround.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DumpVtableAround extends GhidraScript {
	@Override
	public void run() throws Exception {
		var env = System.getenv("SLOT");
		if (env == null || env.isEmpty()) {
			println("set SLOT=<hex data address of a known vtable entry>");
			return;
		}

		var space = currentProgram.getAddressFactory().getDefaultAddressSpace();
		var slot = space.getAddress(Long.parseLong(env, 16));

		// Walk back while each dword still points at a function, so the print starts at the class's slot 0.
		var start = slot;
		while (true) {
			var prev = start.subtract(4);
			if (FunctionAt(prev) == null)
				break;
			start = prev;
		}

		println("vtable starting " + start + " (asked about " + slot + "):");
		for (var a = start; ; a = a.add(4)) {
			var f = FunctionAt(a);
			if (f == null)
				break;

			var mark = a.equals(slot) ? "   <-- the reader" : "";
			println(String.format("  [%2d] %s -> %s @%s%s",
				(int)(a.subtract(start) / 4), a, f.getName(), f.getEntryPoint(), mark));
		}
	}

	// The function a vtable slot points at, or null when this dword is not a function pointer (the run's end).
	private Function FunctionAt(Address slotAddress) throws Exception {
		try {
			var target = currentProgram.getAddressFactory().getDefaultAddressSpace()
				.getAddress(getInt(slotAddress) & 0xFFFFFFFFL);
			return getFunctionAt(target);
		}
		catch (Exception e) {
			return null;
		}
	}
}
