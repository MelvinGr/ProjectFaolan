// Names an unknown virtual slot by looking at WHO OVERRIDES IT. A decompiled `(**(code **)(*obj + 0x60))()`
// gives you a slot number and nothing else; the slot's MEANING is in the set of classes that override it and
// what their overrides return. A base-class getter plus a derived-class `return 1` is a type predicate, and the
// derived class' name IS the answer.
//
// Enumerates every MSVC vtable in the image by walking RTTI CompleteObjectLocators (a COL sits at vtable-4 and
// names its TypeDescriptor), reads SLOT out of each, and groups the classes by the function they land on. Also
// decompiles each distinct target so a trivial `return <const>` / `return this->field` is visible inline.
//
//   SLOT=0x60 [MINSIZE=0x60] /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpVirtualSlotAcrossClasses.java
//
// SLOT is the BYTE offset the decompiler prints. MINSIZE skips the short adjustor-thunk vtables a
// multiple-inheritance class also emits, which do not reach the slot at all.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpVirtualSlotAcrossClasses extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	public void run() throws Exception {
		int slot = Integer.decode(System.getenv().getOrDefault("SLOT", "0x60"));
		int minSize = Integer.decode(System.getenv().getOrDefault("MINSIZE", "0"));
		if (minSize < slot + 4)
			minSize = slot + 4;

		// target function -> the classes whose vtable has it in SLOT
		Map<Long, TreeSet<String>> byTarget = new HashMap<>();
		int vtables = 0;

		for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(true)) {
			if (!s.getName().startsWith("RTTI_Type_Descriptor"))
				continue;
			Namespace ns = s.getParentNamespace();
			String cls = ns != null ? ns.getName() : "?";
			Address td = s.getAddress();

			for (Reference r : getReferencesTo(td)) {
				Address site = r.getFromAddress();
				if (getFunctionContaining(site) != null)
					continue;
				Address col = site.subtract(0xc);
				// COL shape check - a BaseClassDescriptor points at the same descriptor from its +0x0, and
				// treating one as a COL invents vtables that overlap the real ones at a 3-slot shift.
				try {
					long sig = getInt(col) & 0xffffffffL;
					if ((sig != 0 && sig != 1) || (getInt(col.add(0xc)) & 0xffffffffL) != td.getOffset())
						continue;
				} catch (Exception e) {
					continue;
				}

				for (Reference vr : getReferencesTo(col)) {
					Address at = vr.getFromAddress();
					if (getFunctionContaining(at) != null)
						continue;
					Address vt = at.add(4);
					try {
						if (getFunctionAt(toAddr(getInt(vt) & 0xffffffffL)) == null)
							continue;
						// reject a table too short to reach SLOT: every slot up to it must be a function.
						boolean ok = true;
						for (int off = 0; off < minSize && ok; off += 4)
							ok = getFunctionAt(toAddr(getInt(vt.add(off)) & 0xffffffffL)) != null;
						if (!ok)
							continue;
						vtables++;
						long fn = getInt(vt.add(slot)) & 0xffffffffL;
						byTarget.computeIfAbsent(fn, k -> new TreeSet<>()).add(cls);
					} catch (Exception ignored) {
					}
				}
			}
		}

		DecompInterface di = new DecompInterface();
		di.openProgram(currentProgram);

		List<Map.Entry<Long, TreeSet<String>>> ranked = new ArrayList<>(byTarget.entrySet());
		ranked.sort((a, b) -> b.getValue().size() - a.getValue().size());

		try (PrintWriter w = new PrintWriter(OUT + String.format("vslot-%02x.txt", slot))) {
			w.println(String.format("virtual slot +0x%x (index %d) over %d vtables, %d distinct targets",
					slot, slot / 4, vtables, ranked.size()));
			for (Map.Entry<Long, TreeSet<String>> e : ranked) {
				Function f = getFunctionAt(toAddr(e.getKey()));
				w.println();
				w.println(String.format("%08x %s   %d classes", e.getKey(), f != null ? f.getName() : "?", e.getValue().size()));
				if (f != null) {
					DecompileResults dr = di.decompileFunction(f, 30, monitor);
					if (dr != null && dr.decompileCompleted())
						for (String line : dr.getDecompiledFunction().getC().split("\n"))
							w.println("      " + line);
				}
				for (String c : e.getValue())
					w.println("    " + c);
			}
		}
		println("vtables scanned: " + vtables + ", distinct targets: " + ranked.size());
		println("wrote " + OUT + String.format("vslot-%02x.txt", slot));
	}
}
