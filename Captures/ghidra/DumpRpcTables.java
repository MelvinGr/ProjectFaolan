// Headless post-script: recover the per-interface RPC method registration ORDER + base opcode.
// Strategy: the client logs each proxy/dispatch method by a C-string "<Class>Proxy_t::<Method>(<sig>) ..."
// or "<Class>InterfaceDispatch_t::DispatchMethod ...". These string literals sit in .rdata in source/
// registration order. We scan for the marker substrings, walk back to each C-string start (to recover the
// <Class> prefix), and emit (address, fullString) sorted by address -> the registration order per interface.
// We also dump the "called with a too low id! min allowed " marker functions so we can read the base id.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import java.io.PrintWriter;
import java.util.*;

public class DumpRpcTables extends GhidraScript {
    Memory mem;

    Address findNext(Address start, byte[] needle) throws Exception {
        return mem.findBytes(start, needle, null, true, monitor);
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
        try {
            for (int i = 0; i < max; i++) {
                int b = getByte(a.add(i)) & 0xFF;
                if (b == 0) break;
                sb.append(b >= 0x20 && b < 0x7F ? (char) b : '.');
            }
        } catch (Exception e) {}
        return sb.toString();
    }

    @Override
    public void run() throws Exception {
        mem = currentProgram.getMemory();
        PrintWriter out = new PrintWriter("Captures/ghidra/output/rpc_tables.txt");

        // 1) Collect every proxy/dispatch method string, sorted by address (= registration order).
        String[] markers = { "Proxy_t::", "InterfaceDispatch_t::DispatchMethod", "Dispatch_t::DispatchMethod" };
        TreeMap<Long,String> byAddr = new TreeMap<>();
        for (String marker : markers) {
            byte[] kb = marker.getBytes("US-ASCII");
            Address a = findNext(currentProgram.getMinAddress(), kb);
            int guard = 0;
            while (a != null && guard++ < 4000) {
                Address start = stringStart(a);
                String full = readStr(start, 160);
                byAddr.put(start.getOffset(), full);
                a = findNext(a.add(1), kb);
            }
        }
        out.println("=== ALL PROXY/DISPATCH STRINGS IN ADDRESS ORDER (" + byAddr.size() + ") ===");
        for (Map.Entry<Long,String> e : byAddr.entrySet()) {
            out.println(String.format("@%08x  %s", e.getKey(), e.getValue()));
        }
        out.println();

        // 2) Decompile the functions that reference the "too low id! min allowed " marker -> reveals base id.
        DecompInterface di = new DecompInterface();
        di.setOptions(new DecompileOptions());
        di.openProgram(currentProgram);

        out.println("=== 'too low id' / SendMethodInvocation REFERENCING FUNCTIONS ===");
        String[] baseMarkers = { "called with a too low id! min allowed ", "SendMethodInvocation() RPC: " };
        Set<Long> dumped = new HashSet<>();
        for (String bm : baseMarkers) {
            byte[] kb = bm.getBytes("US-ASCII");
            Address a = findNext(currentProgram.getMinAddress(), kb);
            int guard = 0;
            while (a != null && guard++ < 60) {
                Address start = stringStart(a);
                out.println("--- marker C-string @" + start + ": \"" + readStr(start, 80) + "\" ---");
                byte[] ptr = new byte[] { (byte)(start.getOffset() & 0xFF), (byte)((start.getOffset()>>8)&0xFF),
                                          (byte)((start.getOffset()>>16)&0xFF), (byte)((start.getOffset()>>24)&0xFF) };
                Address p = findNext(currentProgram.getMinAddress(), ptr);
                int g2 = 0;
                while (p != null && g2++ < 20) {
                    Function f = getFunctionContaining(p);
                    if (f != null && dumped.add(f.getEntryPoint().getOffset())) {
                        out.println("######## FUNCTION " + f.getName() + " @" + f.getEntryPoint()
                                    + " size=" + f.getBody().getNumAddresses() + " ########");
                        try {
                            DecompileResults res = di.decompileFunction(f, 90, monitor);
                            out.println(res != null && res.decompileCompleted()
                                        ? res.getDecompiledFunction().getC() : "  <decompile failed>");
                        } catch (Exception ex) { out.println("  <exception: " + ex + ">"); }
                    }
                    p = findNext(p.add(1), ptr);
                }
                a = findNext(a.add(1), kb);
            }
        }
        out.flush();
        out.close();
        println("WROTE Captures/ghidra/output/rpc_tables.txt (" + byAddr.size() + " strings)");
    }
}
