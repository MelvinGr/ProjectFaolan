// List B record sub-readers (FUN_00922c90's callees) — to pin the @609 region of the snapshot tails
// (a QuestFullUpdate-style objective detail block: instance/questId/format-ids + a 50304 <remoteformat>).
// #186. FUN_00798c70 is the 0x3F1-framed List B driver; FUN_00922c90 the per-record reader.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;

public class DumpListBHelpers extends GhidraScript {
    static final long[] FNS = {
        0x00799040L, // List B prologue (FUN_00798c70 calls before the loop)
        0x005d7190L, // called inside FUN_00922c90 between the 3 strings and the trailing u32/count
        0x004d55b0L, 0x0079b760L, 0x00922eb0L, // the per-entry inner-loop constructors/readers
    };
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/listb_helpers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (long a : FNS) {
            Function f = getFunctionContaining(toAddr(a));
            out.println("################## FUN_" + Long.toHexString(a)
                + (f == null ? "  <no fn>" : "  @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses()));
            if (f != null) {
                try {
                    DecompileResults res = di.decompileFunction(f, 200, monitor);
                    out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "  <fail>");
                } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
            }
            out.println();
        }
        out.close();
        println("DumpListBHelpers done -> Captures/ghidra/output/listb_helpers.txt");
    }
}
