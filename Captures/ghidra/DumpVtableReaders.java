// Recover the ReadSubClass wire-readers for IIR classes that lack the "bad stream" assert string
// (dynel/loot/movetask/gameactionlist/behaviorcontrol — #187 / #53 / #190), via VTABLE xref:
//   1. In the KNOWN SimpleCharFullUpdateIIR_t vtable, find the slot whose pointer == its reader
//      (FUN_01121c00) -> that's the ReadSubClass vtable slot index (same for every IIR subclass).
//   2. For each target <Class>::vftable, read that same slot -> the class's ReadSubClass -> decompile.
// Run:
//   /opt/ghidra/support/analyzeHeadless /tmp/ghidra-aoc/proj aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpVtableReaders.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpVtableReaders extends GhidraScript {
    static final long KNOWN_READER = 0x01121c00L;            // SimpleCharFullUpdateIIR_t::ReadSubClass (FUN_01121c00)
    static final String KNOWN_VTABLE = "SimpleCharFullUpdateIIR_t::vftable";
    static final String[] TARGETS = {
        "SimpleDynelFullUpdateIIR_c::vftable",
        "LootBagFullUpdateIIR_c::vftable",
        "MoveTaskIIR_c::vftable",
        "GameActionListIIR_t::vftable",
        "BehaviorControlCenterIIR_c::vftable",
    };

    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false)) {
            if (s.getName(true).equals(full)) return s.getAddress();
        }
        return null;
    }

    int findSlot(Address vt, long target) throws Exception {
        for (int i = 0; i < 200; i++) {
            long p = getInt(vt.add(i * 4L)) & 0xffffffffL;
            if (p == target) return i;
        }
        return -1;
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/vtable_readers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address knownVt = vtableAddr(KNOWN_VTABLE);
        out.println("KNOWN vtable " + KNOWN_VTABLE + " -> " + knownVt);
        int slot = knownVt == null ? -1 : findSlot(knownVt, KNOWN_READER);
        out.println("ReadSubClass slot index = " + slot + " (ptr 0x" + Long.toHexString(KNOWN_READER) + ")");
        out.println();
        if (slot < 0) { out.println("could not locate slot; abort"); out.close(); return; }

        for (String t : TARGETS) {
            Address vt = vtableAddr(t);
            out.println("################## " + t + " -> " + vt);
            if (vt == null) { out.println("  <no vtable symbol>"); out.println(); continue; }
            long p = getInt(vt.add(slot * 4L)) & 0xffffffffL;
            Address fa = toAddr(p);
            Function f = getFunctionContaining(fa);
            out.println("  slot[" + slot + "] -> 0x" + Long.toHexString(p) + " in " + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
            if (f == null) { out.println(); continue; }
            try {
                DecompileResults res = di.decompileFunction(f, 150, monitor);
                out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
            } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            out.println();
        }
        out.close();
        println("DumpVtableReaders done -> /tmp/ghidra-aoc/vtable_readers.txt");
    }
}
