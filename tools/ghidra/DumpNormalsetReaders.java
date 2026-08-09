// What does a `normalset` VALUE mean? (#508 follow-up.)
//
// `FUN_0163eec0` reads `GetStat(0xCD, 2)` - the client's `normalset`, stat 205 - and shows the two nameplate
// skulls for 8, 14, 15, 19, 20, 22, 23, 24, 26, 46-51. That pins WHICH values are special and leaves the values
// themselves opaque: the set is obviously an enum and we do not know what its members are CALLED.
//
// The oracle is whoever ELSE reads the stat. A visibility toggle can only ever say yes/no, but a sibling reader
// that indexes a name table, builds a tooltip line or picks between the two skull icons has to NAME the value -
// the same trick as the modifier-slot and applier-switch digs (`client_applier_switch_names_the_enum`).
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpNormalsetReaders.java
//
// ⚠️ There is no call graph to walk here: `GetStat` is a VIRTUAL call - `(**(code **)(*(int *)(x + 0x10c) + 8))
// (0xcd,2)` - so the stat getter has no static references and "callers of the getter" is not a question Ghidra
// can answer. The constant IS the anchor, so PART B scans every instruction in the program for `PUSH 0xCD`.
//
// Output: $FAOLAN_GHIDRA_OUT/normalset_readers.txt
//   PART A - the skull toggle itself, decompiled, with the indirect call that reads the stat.
//   PART B - every function in the exe that pushes 0xCD, decompiled, with the strings it references. A reader
//            with strings attached is the one that names the enum.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataType;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;

public class DumpNormalsetReaders extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	static final long NORMALSET = 0xCDL; // stat 205
	static final long SKULL_FN = 0x0163eec0L;

	// How far back from a CALL to look for the constant being set up. Long enough to clear a few argument moves,
	// short enough that an unrelated 205 elsewhere in the block does not get credited to this call.
	static final int LOOKBACK = 12;

	private static boolean holdsNormalset(Instruction i) {
		for (int op = 0; op < i.getNumOperands(); op++)
			for (Object o : i.getOpObjects(op))
				if (o instanceof Scalar && ((Scalar)o).getUnsignedValue() == NORMALSET)
					return true;

		return false;
	}

	@Override
	public void run() throws Exception {
		PrintWriter w = new PrintWriter(OUT + "normalset_readers.txt");
		DecompInterface d = new DecompInterface();
		d.openProgram(currentProgram);

		Function skull = getFunctionAt(toAddr(SKULL_FN));
		w.println("=== PART A - the skull toggle at " + Long.toHexString(SKULL_FN) + ", and the getter 0xCD is passed to");
		if (skull == null) {
			w.println("  !! no function there - was the exe analysed?");
			w.close();
			d.dispose();
			return;
		}

		// The getter is whatever the CONSTANT is handed to, not whatever is called most. Picking by caller count
		// found a 13,620-caller runtime helper; anchoring on 0xCD is the whole point of the dig.
		Function getter = null;
		for (Instruction i = getInstructionAt(skull.getEntryPoint()); i != null && skull.getBody().contains(i.getAddress()); i = i.getNext()) {
			if (!holdsNormalset(i))
				continue;

			w.println("  0x" + Long.toHexString(NORMALSET) + " set up at " + i.getAddress() + ":  " + i);
			for (Instruction j = i; j != null && j.getAddress().subtract(i.getAddress()) < LOOKBACK * 8; j = j.getNext()) {
				if (!j.getFlowType().isCall())
					continue;

				for (Reference r : j.getReferencesFrom()) {
					Function f = getFunctionAt(r.getToAddress());
					if (f == null)
						continue;

					w.println("    -> " + f.getName() + " @" + f.getEntryPoint() + " (" + f.getCallingFunctions(monitor).size() + " callers)");
					if (getter == null)
						getter = f;
				}
				break;
			}
		}

		DecompileResults skullRes = d.decompileFunction(skull, 90, monitor);
		w.println(skullRes.decompileCompleted() ? skullRes.getDecompiledFunction().getC() : "  !! decompile failed");

		w.println();
		w.println("=== PART B - every function that pushes 0x" + Long.toHexString(NORMALSET) + " (" + NORMALSET + ")");

		// Program-wide, because the virtual getter cannot be xref'd. A bare `PUSH 0xCD` is not proof of a stat read
		// - 205 is also a plausible size or colour byte - so every hit is printed with its function and left for
		// the reader to judge, rather than filtered on a guess about what the call site should look like.
		Set<Function> readers = new LinkedHashSet<>();
		for (Instruction i : currentProgram.getListing().getInstructions(true)) {
			if (!i.getMnemonicString().equalsIgnoreCase("PUSH") || !holdsNormalset(i))
				continue;

			Function f = getFunctionContaining(i.getAddress());
			if (f != null && !f.getEntryPoint().equals(toAddr(SKULL_FN)))
				readers.add(f);
		}

		w.println("  " + readers.size() + " function(s):");
		for (Function f : readers)
			w.println("    " + f.getName() + " @" + f.getEntryPoint());

		for (Function f : readers) {
			w.println();
			w.println("---- " + f.getName() + " @" + f.getEntryPoint());

			// The strings a reader touches are the payload: a name table lookup, an LDB token, a tooltip format.
			List<String> strings = new ArrayList<>();
			for (Instruction i : currentProgram.getListing().getInstructions(f.getBody(), true))
				for (Reference r : i.getReferencesFrom()) {
					Data data = getDataAt(r.getToAddress());
					if (data != null && data.getDataType() instanceof StringDataType)
						strings.add(String.valueOf(data.getValue()));
				}

			if (!strings.isEmpty())
				w.println("  strings: " + strings);

			DecompileResults res = d.decompileFunction(f, 90, monitor);
			w.println(res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  !! decompile failed");
		}

		w.close();
		d.dispose();
		println("<DumpNormalsetReaders.java> wrote " + OUT + "normalset_readers.txt");
	}
}
