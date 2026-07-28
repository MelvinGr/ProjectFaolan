// #438 follow-up to DumpMagicBuffReader: name the two fields the buff payload's @26/@42 words land in.
// MagicBuffIIR_c::ReadSubClass (FUN_007a12a0) puts payload@42 straight onto the IIR at +0x38, and hands
// payload@14..41 to FUN_00897b60 - a SHARED seven-u32 struct reader whose other callers name the struct.
// This dumps:
//   1. every slot of MagicBuffIIR_c::vftable (decompiled) - the consumers that read those offsets back,
//   2. every caller of the shared struct reader, so the struct can be identified from its other uses.
// Run:
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpMagicBuffConsumers.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpMagicBuffConsumers extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long STRUCT_READER = 0x00897b60L; // the seven-u32 sub-struct reader ReadSubClass calls
    static final int VTABLE_SLOTS = 40;

    DecompInterface di;

    Address symbolAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }

    String decomp(Function f) {
        try {
            DecompileResults r = di.decompileFunction(f, 200, monitor);
            return r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "<fail>";
        } catch (Exception ex) { return "<exception: " + ex + ">"; }
    }

    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "magicbuff_consumers.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        Address vt = symbolAddr("MagicBuffIIR_c::vftable");
        out.println("######## MagicBuffIIR_c::vftable @ " + vt + " ########");
        Set<Function> seen = new LinkedHashSet<>();
        if (vt != null) {
            for (int i = 0; i < VTABLE_SLOTS; i++) {
                long fp = getInt(vt.add(i * 4L)) & 0xffffffffL;
                Function f = getFunctionContaining(toAddr(fp));
                out.println(String.format("  slot %2d -> %08x  %s", i, fp, f == null ? "<none>" : f.getName()));
                if (f != null) seen.add(f);
            }
            for (Function f : seen) {
                out.println("\n===== vtable fn " + f.getName() + " @" + f.getEntryPoint() + " =====");
                out.println(decomp(f));
            }
        }

        // Callers of the shared seven-u32 struct reader: their class context names the struct.
        Address sr = toAddr(STRUCT_READER);
        out.println("\n\n######## callers of the shared struct reader " + sr + " ########");
        Set<Function> callers = new LinkedHashSet<>();
        for (Reference r : getReferencesTo(sr)) {
            Function f = getFunctionContaining(r.getFromAddress());
            out.println("  from " + r.getFromAddress() + " (" + r.getReferenceType() + ")"
                + (f != null ? " in " + f.getName() + " @" + f.getEntryPoint() : " <no function>"));
            if (f != null) callers.add(f);
        }
        for (Function f : callers) {
            if (f.getEntryPoint().getOffset() == 0x007a12a0L) continue; // ReadSubClass itself, already dumped
            out.println("\n===== caller " + f.getName() + " @" + f.getEntryPoint() + " =====");
            out.println(decomp(f));
        }

        out.close();
        println("DumpMagicBuffConsumers done -> " + OUT + "magicbuff_consumers.txt");
    }
}
