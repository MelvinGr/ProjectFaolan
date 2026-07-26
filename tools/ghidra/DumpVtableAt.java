// Dump N pointer slots of a vtable at VT (hex), naming each target function.
//   VT=1fc9168 SLOTS=24 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript DumpVtableAt.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class DumpVtableAt extends GhidraScript {
    @Override
    public void run() throws Exception {
        long base = Long.parseLong(System.getenv("VT").trim(), 16);
        int slots = Integer.parseInt(System.getenv().getOrDefault("SLOTS", "24"));
        for (int i = 0; i < slots; i++) {
            Address at = toAddr(base + i * 4L);
            long p;
            try { p = getInt(at) & 0xffffffffL; } catch (Exception e) { println("slot " + i + " unreadable"); continue; }
            Function f = p != 0 ? getFunctionAt(toAddr(p)) : null;
            println(String.format("slot %2d @%s -> %08x %s", i, at, p, f != null ? f.getName() : ""));
        }
    }
}
