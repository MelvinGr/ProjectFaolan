// Recover the FullCharacterIIR (0x737A6DF9) ReadSubClass wire-reader to pin the snapshot-tails grammar,
// specifically the still-opaque UnknownBeforeAccessWindowTable progression region (#91 / .bin removal).
// Method = the proven vtable-xref ([[ghidra_vtable_xref_readers]]): ReadSubClass is vtable slot 8.
//   1. confirm the slot index from the known SimpleChar reader (FUN_01121c00),
//   2. enumerate every <Class>FullUpdateIIR*::vftable / *Character*::vftable symbol,
//   3. for the FullCharacter one, read slot[8] -> its ReadSubClass -> decompile (and list its callees).
// Run:
//   /opt/ghidra/support/analyzeHeadless /tmp/ghidra-aoc/proj aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpFullCharReader.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpFullCharReader extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;            // SimpleCharFullUpdateIIR_t::ReadSubClass
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";

    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }

    void dump(DecompInterface di, PrintWriter out, Function f, Set<String> seen) {
        if (!seen.add(f.getEntryPoint().toString())) return;
        out.println("---- " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " ----");
        try {
            DecompileResults res = di.decompileFunction(f, 200, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <decompile fail>");
        } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
    }

    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 200; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            if (p == target) return i;
        }
        return -1;
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/fullchar_reader.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address knownVt = vtableAddr(KNOWN_VTABLE);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot + " (known vtable " + knownVt + ")");
        out.println();
        if (slot < 0) { out.println("could not locate slot; abort"); out.close(); return; }

        // 1. Enumerate candidate vtable symbols.
        out.println("===== candidate vtables (FullUpdateIIR / Character) =====");
        List<String> hits = new ArrayList<>();
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            String n = s.getName(true);
            if (!n.endsWith("::vftable")) continue;
            String low = n.toLowerCase();
            if (low.contains("fullcharacter") || low.contains("fullchar") ||
                (low.contains("character") && low.contains("iir"))) {
                out.println("  " + n + " @" + s.getAddress());
                hits.add(n);
            }
        }
        out.println();

        // 2. Dump the FullPlayerCharacterIIR reader (0x737A6DF9 == FullPlayerCharacterIIR, confirmed) and its
        //    Core* base readers; decompile each + its direct callees (the sub-record readers that consume the
        //    UnknownBefore progression region).
        String[] targets = {
            "FullPlayerCharacterIIR::vftable",
            "CoreFullPlayerCharacterIIR::vftable",
            "CoreCharacterFullUpdateIIR::vftable",
        };
        Set<String> decompiled = new HashSet<>();
        for (String t : targets) {
            Address vt = vtableAddr(t);
            if (vt == null) { out.println("################## " + t + "  <no symbol>"); out.println(); continue; }
            long p = getInt(vt.add(slot * 4L)) & 0xffffffffL;
            Function f = getFunctionContaining(toAddr(p));
            out.println("################## " + t + "  slot[" + slot + "] -> 0x" + Long.toHexString(p)
                + " in " + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint()));
            if (f == null) { out.println(); continue; }
            dump(di, out, f, decompiled);
            // and its direct callees (one level), to reach the per-record sub-readers
            for (Function c : f.getCalledFunctions(monitor)) {
                long sz = c.getBody().getNumAddresses();
                if (sz < 24 || sz > 4000) continue; // skip trivial thunks + huge unrelated fns
                dump(di, out, c, decompiled);
            }
            out.println();
        }
        out.close();
        println("DumpFullCharReader done -> /tmp/ghidra-aoc/fullchar_reader.txt");
    }
}
