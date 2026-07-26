// Locate an ASCII string (STR env) and dump the pointer slots that follow it - the layout the
// AoC IIR classes use (class-name / log-channel strings immediately precede the vftable).
//   STR="StatUpdateIIR_c" SLOTS=16 analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindStrAndVtable.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;

public class FindStrAndVtable extends GhidraScript {
    @Override
    public void run() throws Exception {
        String needle = System.getenv("STR");
        int slots = Integer.parseInt(System.getenv().getOrDefault("SLOTS", "16"));
        Address at = currentProgram.getMinAddress();
        for (int hit = 0; hit < 6; hit++) {
            Address s = currentProgram.getMemory().findBytes(at, needle.getBytes("US-ASCII"), null, true, monitor);
            if (s == null) break;
            println("hit " + hit + " @" + s);
            for (int i = 0; i < slots; i++) {
                Address p = s.add(needle.length() + 1 + i * 4L);
                try {
                    long v = getInt(p) & 0xffffffffL;
                    Function f = v != 0 ? getFunctionAt(toAddr(v)) : null;
                    if (f != null) println(String.format("   +%3d @%s -> %08x %s", i * 4, p, v, f.getName()));
                } catch (Exception e) { /* unreadable */ }
            }
            at = s.add(1);
        }
    }
}
