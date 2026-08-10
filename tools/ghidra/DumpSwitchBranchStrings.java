// Names the CASES of a dense MSVC switch by the STRINGS each branch logs. `DumpSwitchTable` composes the byte
// index table and the dword target table into "value -> branch address", which is the half that says WHICH
// values share a branch; this is the other half - what the branch is FOR - and it works whenever the switch is
// over a hand-written enum, because the developer's own log/assert text carries the enum member's name.
//
//   BYTES=6e67d0 COUNT=c9 TARGETS=6e6630 BASE=0 \
//     /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpSwitchBranchStrings.java
//
// Same four inputs as `DumpSwitchTable` (all hex, no 0x): COUNT is the switch's own bound, inclusive, so pass
// n+1; BASE is what the compare subtracts.
//
// A branch's extent is taken as [target, next target in ADDRESS order) - the branches of one switch are laid out
// consecutively, so that is exact for a case that falls straight through and generous for one that jumps into
// shared tail code. Generous is the right failure: a name that belongs to a neighbour is visible as a duplicate,
// where a missed one just looks unnamed.
//
// Written for #790, where it read `CriteriaItem_t::CheckCriteria`'s 201-value operator switch: the branches log
// `Operator_HasWornItem`, `Operator_Time_Less`, `e_Operator_GC_HasQuestJournalSpace` and so on by name.
// Output: $FAOLAN_GHIDRA_OUT/switch_branch_strings.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.PrintWriter;
import java.util.*;

public class DumpSwitchBranchStrings extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	public void run() throws Exception {
		long bytes = Long.parseLong(System.getenv("BYTES").trim(), 16);
		int count = Integer.parseInt(System.getenv("COUNT").trim(), 16);
		long targets = Long.parseLong(System.getenv("TARGETS").trim(), 16);
		int base = Integer.parseInt(System.getenv().getOrDefault("BASE", "0").trim(), 16);

		// value -> branch, and the inverse, so a branch shared by several values prints once with all of them.
		Map<Long, TreeSet<Integer>> valuesByTarget = new TreeMap<>();
		for (var i = 0; i < count; i++) {
			var idx = getByte(toAddr(bytes + i)) & 0xff;
			var target = getInt(toAddr(targets + 4L * idx)) & 0xffffffffL;
			valuesByTarget.computeIfAbsent(target, k -> new TreeSet<>()).add(base + i);
		}

		var boundaries = new ArrayList<>(valuesByTarget.keySet());
		Collections.sort(boundaries);

		try (PrintWriter w = new PrintWriter(OUT + "switch_branch_strings.txt")) {
			w.println(String.format("switch over %d values, %d distinct branches", count, boundaries.size()));
			for (var i = 0; i < boundaries.size(); i++) {
				long start = boundaries.get(i);
				long end = i + 1 < boundaries.size() ? boundaries.get(i + 1) : start + 0x400;
				var strings = new LinkedHashSet<String>();

				InstructionIterator it = currentProgram.getListing().getInstructions(toAddr(start), true);
				while (it.hasNext()) {
					Instruction ins = it.next();
					if (ins.getAddress().getOffset() >= end)
						break;

					for (var op = 0; op < ins.getNumOperands(); op++) {
						for (Object o : ins.getOpObjects(op)) {
							Address at = o instanceof Scalar s ? toAddr(s.getUnsignedValue())
									: o instanceof Address a ? a : null;
							if (at == null)
								continue;

							var text = stringAt(at);
							if (text != null)
								strings.add(text);
						}
					}
				}

				w.println();
				w.println(String.format("%08x   values: %s", start, valuesByTarget.get(start)));
				for (var s : strings)
					w.println("      \"" + s + "\"");
			}
		}

		println("branches: " + boundaries.size() + " -> " + OUT + "switch_branch_strings.txt");
	}

	// The literal at an address, if there is one there. Both a defined string and a raw run of printable bytes
	// count - the image has plenty of the latter, and a branch's name is just as often in an undefined run.
	private String stringAt(Address at) {
		try {
			Data d = getDataAt(at);
			if (d != null && d.getValue() instanceof String s && s.length() >= 3)
				return s;

			var sb = new StringBuilder();
			for (var i = 0; i < 160; i++) {
				var b = getByte(at.add(i));
				if (b == 0)
					break;
				if (b < 0x20 || b > 0x7e)
					return null;
				sb.append((char) b);
			}

			return sb.length() >= 6 ? sb.toString() : null;
		} catch (Exception e) {
			return null;
		}
	}
}
