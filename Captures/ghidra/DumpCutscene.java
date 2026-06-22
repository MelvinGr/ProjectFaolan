// Dumps cutscene / camera-path strings and the functions that reference them, to find what
// gates the create-flow drowning cutscene (CC_Drowning_*) and the "invalid camera path" error.
// Byte-scans .rdata for each needle (mirrors DumpRpcTables), walks to the string start, then lists
// the functions that reference that string address.
//   /opt/ghidra/support/analyzeHeadless /home/melvin/Development/Ghidra/Aoc aoc -process AgeOfConan.exe \
//     -noanalysis -scriptPath Captures/ghidra -postScript DumpCutscene.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;
import java.util.*;

public class DumpCutscene extends GhidraScript {
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
        ReferenceManager rm = currentProgram.getReferenceManager();
        String[] needles = {
            "invalid camera path", "InitPath", "CinematicCamera", "CC_Drowning",
            "PlayOrSkip", "playorskip", "character_creation", "Slave Galley",
            "GetCameraPath", "CameraPath", "Drowning", "CutScene_", "cutscene"
        };
        Set<Long> seen = new HashSet<>();
        Set<String> hitFns = new LinkedHashSet<>();

        for (String needle : needles) {
            byte[] kb = needle.getBytes("US-ASCII");
            Address a = findNext(currentProgram.getMinAddress(), kb);
            int guard = 0;
            while (a != null && guard++ < 200) {
                Address start = stringStart(a);
                if (seen.add(start.getOffset())) {
                    String full = readStr(start, 160);
                    List<String> refFns = new ArrayList<>();
                    for (Reference r : rm.getReferencesTo(start)) {
                        Function f = getFunctionContaining(r.getFromAddress());
                        String fn = (f != null) ? (f.getName() + "@" + f.getEntryPoint()) : ("?@" + r.getFromAddress());
                        refFns.add(fn);
                        if (f != null) hitFns.add(f.getEntryPoint() + " " + f.getName());
                    }
                    println("STR " + start + "  \"" + full + "\"  <- " + refFns);
                }
                a = findNext(a.add(1), kb);
            }
        }

        println("\n==== referencing functions (" + hitFns.size() + ") ====");
        for (String f : hitFns) println("FN " + f);
    }
}
