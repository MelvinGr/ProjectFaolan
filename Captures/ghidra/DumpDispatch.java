// Decompile the s2c dispatcher + the Interface dispatch-table constructors.
// The dispatcher "<Class>InterfaceDispatch_t::DispatchMethod(" maps the incoming opcode to a handler;
// the Interface constructor registers the dispatch method NAMES in registration order (FUN_0040ec10 pattern).
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
import java.util.*;

public class DumpDispatch extends GhidraScript {
    Address findNext(Address start, byte[] needle) throws Exception {
        return currentProgram.getMemory().findBytes(start, needle, null, true, monitor);
    }
    Address stringStart(Address a) throws Exception {
        Address cur = a;
        for (int i = 0; i < 400; i++) {
            Address prev = cur.subtract(1);
            if ((getByte(prev) & 0xFF) == 0) return cur;
            cur = prev;
        }
        return a;
    }
    String readStr(Address a, int max) {
        StringBuilder sb = new StringBuilder();
        try { for (int i = 0; i < max; i++) { int b = getByte(a.add(i)) & 0xFF; if (b == 0) break;
            sb.append(b >= 0x20 && b < 0x7F ? (char) b : '.'); } } catch (Exception e) {}
        return sb.toString();
    }

    @Override
    public void run() throws Exception {
        PrintWriter out = new PrintWriter("/tmp/ghidra-aoc/dispatch.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        // Find every "...Dispatch_t::DispatchMethod(" string, locate the referencing function, decompile it.
        byte[] kb = "Dispatch_t::DispatchMethod(".getBytes("US-ASCII");
        Address a = findNext(currentProgram.getMinAddress(), kb);
        Set<Long> dumped = new HashSet<>();
        int guard = 0;
        while (a != null && guard++ < 80) {
            Address start = stringStart(a);
            String full = readStr(start, 80);
            byte[] ptr = new byte[] { (byte)(start.getOffset() & 0xFF), (byte)((start.getOffset()>>8)&0xFF),
                                      (byte)((start.getOffset()>>16)&0xFF), (byte)((start.getOffset()>>24)&0xFF) };
            Address p = findNext(currentProgram.getMinAddress(), ptr);
            int g2 = 0;
            while (p != null && g2++ < 20) {
                Function f = getFunctionContaining(p);
                if (f != null && dumped.add(f.getEntryPoint().getOffset())) {
                    out.println("######## DISPATCHER for \"" + full + "\"  FUNCTION " + f.getName()
                                + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses() + " ########");
                    try {
                        DecompileResults res = di.decompileFunction(f, 120, monitor);
                        out.println(res != null && res.decompileCompleted()
                                    ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                    } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
                    out.println();
                }
                p = findNext(p.add(1), ptr);
            }
            a = findNext(a.add(1), kb);
        }
        out.flush();
        out.close();
        println("WROTE /tmp/ghidra-aoc/dispatch.txt (" + dumped.size() + " dispatchers)");
    }
}
