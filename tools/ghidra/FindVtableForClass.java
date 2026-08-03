// Resolves a C++ class NAME to its vtable(s) and dumps a slot window, by walking MSVC RTTI the way the
// linker laid it out: TypeDescriptor <- CompleteObjectLocator <- (vtable-4). Ghidra names the descriptor
// `<Class>::RTTI_Type_Descriptor`, so the class name is the NAMESPACE, not the symbol name - which is why a
// plain symbol-name search for the class comes back with nothing but string literals.
//
// The point is the reverse direction of WhatClass.java (vtable -> class): here you have a virtual call
// `(**(code **)(*obj + 0x60))()` on a known static type and need to know WHICH function that is.
//
//   CLASS=SimpleChar_t [FROM=0x50] [TO=0x70] /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindVtableForClass.java
//
// FROM/TO are BYTE offsets into the vtable (what the decompiler prints), inclusive/exclusive, default the
// whole table up to the first non-code pointer.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class FindVtableForClass extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	public void run() throws Exception {
		String cls = System.getenv("CLASS");
		if (cls == null || cls.isEmpty()) { println("set CLASS=<C++ class name>"); return; }
		int from = Integer.decode(System.getenv().getOrDefault("FROM", "0"));
		int to = Integer.decode(System.getenv().getOrDefault("TO", "0x400"));

		// 1. the type descriptor: symbol RTTI_Type_Descriptor inside namespace <cls>
		List<Address> descriptors = new ArrayList<>();
		for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(true)) {
			if (!s.getName().startsWith("RTTI_Type_Descriptor"))
				continue;
			Namespace ns = s.getParentNamespace();
			if (ns != null && cls.equals(ns.getName()))
				descriptors.add(s.getAddress());
		}
		println("type descriptors for " + cls + ": " + descriptors);
		if (descriptors.isEmpty()) return;

		// 2. every COL that names one of them (+0xc is the TypeDescriptor pointer).
		// ⚠️ A BaseClassDescriptor points at the SAME descriptor from its +0x0, so a bare `site - 0xc` invents a
		// COL for every base class the type participates in - which is what produced overlapping, 3-slot-shifted
		// "vtables" the first time. Validate the shape: signature at +0 is 0 (32-bit) or 1, and +0xc really is
		// the descriptor.
		Set<Address> cols = new LinkedHashSet<>();
		for (Address td : descriptors)
			for (Reference r : getReferencesTo(td)) {
				Address site = r.getFromAddress();
				if (getFunctionContaining(site) != null)
					continue;
				Address col = site.subtract(0xc);
				try {
					long sig = getInt(col) & 0xffffffffL;
					long pTd = getInt(col.add(0xc)) & 0xffffffffL;
					if ((sig == 0 || sig == 1) && pTd == td.getOffset())
						cols.add(col);
				} catch (Exception ignored) {
				}
			}

		try (PrintWriter w = new PrintWriter(OUT + "vtable-" + cls + ".txt")) {
			w.println("class " + cls + "  vtable slots [0x" + Integer.toHexString(from) + ", 0x" + Integer.toHexString(to) + ")");
			for (Address col : cols) {
				// 3. the vtable is whatever holds a pointer to the COL, one slot BEFORE its first method.
				for (Reference r : getReferencesTo(col)) {
					Address at = r.getFromAddress();
					if (getFunctionContaining(at) != null)
						continue;
					Address vt = at.add(4);
					// a vtable's first slot is always a real function; a COL referenced from anywhere else is not one.
					if (getFunctionAt(toAddr(getInt(vt) & 0xffffffffL)) == null)
						continue;
					w.println();
					w.println("COL @" + col + "  vtable @" + vt);
					println("COL @" + col + "  vtable @" + vt);
					for (int off = from; off < to; off += 4) {
						long p;
						try { p = getInt(vt.add(off)) & 0xffffffffL; } catch (Exception e) { break; }
						Function f = p != 0 ? getFunctionAt(toAddr(p)) : null;
						if (f == null && p == 0) break;
						String line = String.format("  +0x%03x [slot %3d] -> %08x %s", off, off / 4, p, f != null ? f.getName() : "(not a function)");
						w.println(line);
						println(line);
					}
				}
			}
		}
		println("wrote " + OUT + "vtable-" + cls + ".txt");
	}
}
