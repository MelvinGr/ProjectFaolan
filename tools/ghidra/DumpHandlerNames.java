// For each handler function address in Captures/ghidra/output/handler_addrs.txt, scan its instructions for
// data pointers into string memory and print the "<Class>Dispatch_t::<Method>" name string it references.
// Output: one "FUN_xxxxxxxx <tab> <best name string>" line per handler -> resolves s2c case -> method name.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import java.io.*;
import java.util.*;

public class DumpHandlerNames extends GhidraScript {
    String readStr(Address a, int max) {
        StringBuilder sb = new StringBuilder();
        try { for (int i = 0; i < max; i++) { int b = getByte(a.add(i)) & 0xFF; if (b == 0) break;
            sb.append(b >= 0x20 && b < 0x7F ? (char) b : '.'); } } catch (Exception e) {}
        return sb.toString();
    }
    boolean printable(Address a) {
        try { int b = getByte(a) & 0xFF; return b >= 0x20 && b < 0x7F; } catch (Exception e) { return false; }
    }
    @Override
    public void run() throws Exception {
        List<Long> addrs = new ArrayList<>();
        BufferedReader br = new BufferedReader(new FileReader("Captures/ghidra/output/handler_addrs.txt"));
        String ln;
        while ((ln = br.readLine()) != null) {
            ln = ln.trim().replace("L,", "").replace(",", "").replace("L", "");
            if (ln.startsWith("0x")) addrs.add(Long.parseLong(ln.substring(2), 16));
        }
        br.close();
        PrintWriter out = new PrintWriter("Captures/ghidra/output/handler_names.txt");
        Listing lst = currentProgram.getListing();
        for (long off : addrs) {
            Function f = getFunctionAt(toAddr(off));
            String best = "";
            if (f != null) {
                InstructionIterator it = lst.getInstructions(f.getBody(), true);
                while (it.hasNext()) {
                    Instruction ins = it.next();
                    for (int oi = 0; oi < ins.getNumOperands(); oi++) {
                        for (Object o : ins.getOpObjects(oi)) {
                            if (o instanceof Scalar) {
                                long v = ((Scalar)o).getUnsignedValue();
                                if (v > 0x01000000L && v < 0x02400000L) {
                                    Address sa = toAddr(v);
                                    if (printable(sa)) {
                                        String s = readStr(sa, 120);
                                        int p = s.indexOf("Dispatch_t::");
                                        if (p >= 0) {
                                            String nm = s.substring(p + "Dispatch_t::".length());
                                            int paren = nm.indexOf('(');
                                            if (paren > 0) nm = nm.substring(0, paren);
                                            // prefer the class-qualified full token
                                            String full = s.substring(0, p) + "::" + nm;
                                            if (best.isEmpty()) best = full;
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
            out.println(String.format("FUN_%08x\t%s", off, best));
        }
        out.flush(); out.close();
        println("WROTE Captures/ghidra/output/handler_names.txt (" + addrs.size() + " handlers)");
    }
}
