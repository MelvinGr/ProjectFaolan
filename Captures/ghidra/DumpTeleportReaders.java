// Decode the change-map teleport bodies (#219): dump the two IIR readers + the PlayfieldProxy sub-reader so the
// remaining "undecoded" fields (n3TeleportIIR's destDescriptor 0x14/0xBE, the constant words 0x9C50/0x18704) can
// be named from the binary instead of carried as capture constants.
//   FUN_005bfce0  IntendedTeleportIIR_c::ReadSubClass (0x10D27BC0, "begin teleport")
//   FUN_005d1ae0  n3TeleportIIR_t::ReadSubClass       (0x5A32F0D7, "destination announce")
//   FUN_00a6a600  PlayfieldProxy stream reader
// Run:
//   /opt/ghidra/support/analyzeHeadless /tmp/ghidra-aoc/proj aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath Captures/ghidra -postScript DumpTeleportReaders.java
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DumpTeleportReaders extends GhidraScript {
    static final long[] FNS = { 0x005bfce0L, 0x005d1ae0L, 0x00a6a600L };

    DecompInterface di;

    String decomp(Function f) {
        DecompileResults res = di.decompileFunction(f, 200, monitor);
        return res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<decompile fail>";
    }

    @Override public void run() throws Exception {
        java.io.PrintWriter out = new java.io.PrintWriter("/tmp/ghidra-aoc/teleport_readers.txt");
        di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);

        for (long p : FNS) {
            Function f = getFunctionContaining(toAddr(p));
            out.println("################## 0x" + Long.toHexString(p) + " -> "
                + (f == null ? "null" : f.getName() + " @" + f.getEntryPoint()));
            if (f != null) out.println(decomp(f));
            out.println();
        }
        out.close();
        println("DumpTeleportReaders done -> /tmp/ghidra-aoc/teleport_readers.txt");
    }
}
