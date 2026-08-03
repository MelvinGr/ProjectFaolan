// Finds the functions that CONSUME the FA015807 pre-IIR scaffold - the 53 u32 the spawn reader
// `SimpleCharFullUpdateIIR_t::ReadSubClass` (FUN_01121c00) stores to the non-contiguous struct offsets
// 0x184..0x264. The scaffold's grammar is fully recovered and byte-exact, but only ~6 of its 60 slots are
// ATTRIBUTED (docs/re/fa015807-spawn-reader.md); the rest are known to be zero on a fresh spawn and nothing more.
// Whatever READS those offsets is what names them.
//
// One pass over every instruction, bucketed by which scaffold offset it references. A single offset is worthless
// as a signal - 0x184 is a field on plenty of unrelated classes - so a function is only reported when it touches
// at least MINHITS DISTINCT offsets from the set, which is a shape no unrelated class shares. The reader itself
// shows up (it writes all 53), and that is the control: if it does not, the scan is looking at the wrong thing.
//
//   FAOLAN_GHIDRA_OUT=<dir> [MINHITS=4] /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpScaffoldConsumers.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpScaffoldConsumers extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	// The struct offset each of the 53 u32 is stored to, in WIRE order - the same table
	// SpawnPreIir.WordStructOffsets carries, so a slot named here maps straight back to a wire index.
	static final int[] OFFSETS = {
			0x184, 0x188, 0x18c, 0x190, 0x194, 0x198, 0x19c, 0x1a8, 0x1b0, 0x1c0, 0x1c8, 0x1d0, 0x1d8, 0x1e0, 0x1e8, 0x1f0, 0x1b8, 0x1fc, 0x200, 0x204, 0x208, 0x210, 0x214, 0x218, 0x21c, 0x220,
			0x1a0, 0x1a4, 0x1ac, 0x1b4, 0x1c4, 0x1cc, 0x1d4, 0x1dc, 0x1e4, 0x1ec, 0x1f4, 0x1bc, 0x224, 0x230, 0x228, 0x234, 0x238, 0x23c, 0x240, 0x244, 0x248, 0x24c, 0x254, 0x258, 0x25c, 0x260,
			0x264
	};

	public void run() throws Exception {
		Map<Integer, Integer> wireIndexOf = new HashMap<>();
		Map<String, Integer> needles = new HashMap<>();
		for (int i = 0; i < OFFSETS.length; i++) {
			wireIndexOf.put(OFFSETS[i], i);
			needles.put("0x" + Integer.toHexString(OFFSETS[i]) + "]", OFFSETS[i]);
		}

		int minHits = Integer.parseInt(System.getenv().getOrDefault("MINHITS", "4"));

		// fn entry -> the distinct scaffold offsets it touches, and one sample instruction each.
		Map<String, TreeMap<Integer, String>> hits = new LinkedHashMap<>();
		InstructionIterator it = currentProgram.getListing().getInstructions(true);
		while (it.hasNext()) {
			Instruction ins = it.next();
			for (int op = 0; op < ins.getNumOperands(); op++) {
				String rep = ins.getDefaultOperandRepresentation(op);
				if (rep.indexOf('[') < 0)
					continue;

				for (Map.Entry<String, Integer> e : needles.entrySet()) {
					if (!rep.contains(e.getKey()))
						continue;

					Function f = getFunctionContaining(ins.getAddress());
					String key = f != null ? f.getName() + "@" + f.getEntryPoint() : "?@" + ins.getAddress();
					hits.computeIfAbsent(key, k -> new TreeMap<>()).putIfAbsent(e.getValue(), ins.getAddress() + ":  " + ins);
				}
			}
		}

		List<Map.Entry<String, TreeMap<Integer, String>>> ranked = new ArrayList<>(hits.entrySet());
		ranked.removeIf(e -> e.getValue().size() < minHits);
		ranked.sort((a, b) -> b.getValue().size() - a.getValue().size());

		try (PrintWriter w = new PrintWriter(OUT + "scaffold-consumers.txt")) {
			w.println("Functions touching >= " + minHits + " of the 53 FA015807 scaffold struct offsets (0x184..0x264).");
			w.println("wire index in brackets - the slot's position in SpawnPreIir.Words.");
			w.println();
			for (Map.Entry<String, TreeMap<Integer, String>> e : ranked) {
				w.println(e.getKey() + "   " + e.getValue().size() + " distinct offsets");
				for (Map.Entry<Integer, String> o : e.getValue().entrySet())
					w.println(String.format("    +0x%03x [wi%02d]  %s", o.getKey(), wireIndexOf.get(o.getKey()), o.getValue()));
				w.println();
			}
		}

		println("functions touching >= " + minHits + " scaffold offsets: " + ranked.size());
		for (int i = 0; i < Math.min(20, ranked.size()); i++)
			println("  " + ranked.get(i).getKey() + "  " + ranked.get(i).getValue().size());
		println("wrote " + OUT + "scaffold-consumers.txt");
	}
}
