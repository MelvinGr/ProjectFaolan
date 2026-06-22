// Dump the per-type MoveTask wire readers: vtable slot 10 (offset 0x28) of ListMoveTask / PathMoveTask /
// RotateMoveTask (the read method ReadSubClass calls as (*obj+0x28)()). #53.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;

public class DumpMoveTaskTypeReaders extends GhidraScript {
    static final String[] TARGETS = { "WaitMoveTask::vftable","RotateMoveTask::vftable","JumpMoveTask::vftable","SetPosMoveTask::vftable","GroundAlignMoveTask::vftable" };
    Address vtableAddr(String full) {
        for (Symbol s : currentProgram.getSymbolTable().getAllSymbols(false))
            if (s.getName(true).equals(full)) return s.getAddress();
        return null;
    }
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("/home/melvin/Development/Ghidra/Aoc/movetask_type_readers.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        for (String t : TARGETS) {
            Address vt = vtableAddr(t);
            out.println("###### " + t + " -> " + vt);
            if (vt == null) continue;
            long p = getInt(vt.add(14 * 4L)) & 0xffffffffL;  // slot 10 = offset 0x28
            Address fa = toAddr(p);
            Function f = getFunctionContaining(fa);
            out.println("  slot[14] -> " + fa + " " + (f != null ? f.getName() : "(none)"));
            if (f != null) {
                DecompileResults r = di.decompileFunction(f, 90, monitor);
                out.println(r != null && r.decompileCompleted() ? r.getDecompiledFunction().getC() : "<fail>");
            }
            out.println();
        }
        out.close(); println("done");
    }
}
