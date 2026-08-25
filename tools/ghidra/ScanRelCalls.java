// PTR=<hex addr>[,<hex>...] : scan EXECUTABLE bytes for E8 rel32 CALLs whose computed target is one of PTR,
// including bytes Ghidra never disassembled. Complements ScanPtrData (absolute pointers in data).
// ALL=1 scans every INITIALIZED block (a code band the importer left without the execute flag), and E9 rel32 JMPs
// are reported too (a tail-call is how a function with zero CALL sites can still be reached).
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;

public class ScanRelCalls extends GhidraScript {
    @Override
    public void run() throws Exception {
        java.util.Set<Long> targets = new java.util.HashSet<>();
        for (String s : System.getenv("PTR").split(",")) targets.add(Long.parseLong(s.trim(), 16));
        int n = 0;
        long scanned = 0;
        for (MemoryBlock b : currentProgram.getMemory().getBlocks()) {
            if (!b.isInitialized() || (!b.isExecute() && !"1".equals(System.getenv("ALL")))) continue;
            scanned += b.getSize();
            byte[] buf = new byte[(int) b.getSize()];
            b.getBytes(b.getStart(), buf);
            long base = b.getStart().getOffset();
            for (int i = 0; i + 4 < buf.length; i++) {
                if (buf[i] != (byte) 0xE8 && buf[i] != (byte) 0xE9) continue;
                long rel = (buf[i + 1] & 0xFFL) | (buf[i + 2] & 0xFFL) << 8 | (buf[i + 3] & 0xFFL) << 16 | ((long) buf[i + 4]) << 24;
                long target = base + i + 5 + rel;
                if (targets.contains(target)) {
                    println((buf[i] == (byte) 0xE8 ? "CALL at " : "JMP at ") + b.getStart().add(i) + " -> " + Long.toHexString(target) + " in " + getFunctionContaining(b.getStart().add(i)));
                    n++;
                }
            }
        }
        println("rel-call hits: " + n + " (scanned " + scanned + " executable bytes)");
    }
}
