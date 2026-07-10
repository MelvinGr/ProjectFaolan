// For each "PlayerAgentProxy_t::<Method>(" log string, find the proxy method function that references it and
// decompile it. Each c2s proxy send bakes in the 16-bit wire opcode (0x20xx) and the 32-bit method hash it puts
// on the wire (the header-extra). Dumping the decompiled C lets us read (method -> opcode, hash) for the whole
// PlayerAgent interface - the pre-auth login table #320 needs. Output: playeragent_proxy.txt.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import java.io.PrintWriter;
import java.util.*;

public class DumpPlayerAgentProxy extends GhidraScript {
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
        PrintWriter out = new PrintWriter("Captures/ghidra/output/playeragent_proxy.txt");
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        byte[] kb = "PlayerAgentProxy_t::".getBytes("US-ASCII");
        Address a = findNext(currentProgram.getMinAddress(), kb);
        Set<Long> dumped = new HashSet<>();
        int guard = 0;
        while (a != null && guard++ < 200) {
            Address start = stringStart(a);
            String full = readStr(start, 160);
            // Only method log strings (skip the "min allowed" base check + any noise).
            if (full.startsWith("PlayerAgentProxy_t::") && full.contains("(")) {
                byte[] ptr = new byte[] { (byte)(start.getOffset() & 0xFF), (byte)((start.getOffset()>>8)&0xFF),
                                          (byte)((start.getOffset()>>16)&0xFF), (byte)((start.getOffset()>>24)&0xFF) };
                Address p = findNext(currentProgram.getMinAddress(), ptr);
                int g2 = 0;
                while (p != null && g2++ < 20) {
                    Function f = getFunctionContaining(p);
                    if (f != null && dumped.add(f.getEntryPoint().getOffset())) {
                        String method = full.substring("PlayerAgentProxy_t::".length());
                        int paren = method.indexOf('(');
                        if (paren > 0) method = method.substring(0, paren);
                        out.println("######## " + method + "  FUN " + f.getName() + " @" + f.getEntryPoint() + " ########");
                        try {
                            DecompileResults res = di.decompileFunction(f, 90, monitor);
                            out.println(res != null && res.decompileCompleted()
                                        ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                        } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
                        out.println();
                    }
                    p = findNext(p.add(1), ptr);
                }
            }
            a = findNext(a.add(1), kb);
        }
        out.flush();
        out.close();
        println("WROTE Captures/ghidra/output/playeragent_proxy.txt (" + dumped.size() + " methods)");
    }
}
