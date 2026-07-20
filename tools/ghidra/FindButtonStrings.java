// Locate the "Random Body" char-create button + body-morph action handlers by their label/command strings.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.mem.Memory;
import ghidra.program.model.symbol.*;

public class FindButtonStrings extends GhidraScript {
    public void run() throws Exception {
        Memory mem = currentProgram.getMemory();
        ReferenceManager rm = currentProgram.getReferenceManager();
        String[] needles = {
            "Random Body", "RandomBody", "random body", "randombody", "Randomize Body",
            "RandomizeBody", "randomizebody", "randomize_body", "RandomBodyButton",
            "random_body", "BodyRandom", "ApplyBody", "ApplyMorph", "SetMorph", "set_morph",
            "BodyMorph", "bodymorph", "RandomLooks", "randomlooks", "randomize"
        };
        for (String needle : needles) {
            byte[] kb = needle.getBytes("US-ASCII");
            Address a = mem.findBytes(currentProgram.getMinAddress(), kb, null, true, monitor);
            int g = 0;
            while (a != null && g++ < 10) {
                Address cur = a;
                for (int i = 0; i < 600; i++) { if ((getByte(cur.subtract(1)) & 0xFF) == 0) break; cur = cur.subtract(1); }
                java.util.LinkedHashSet<String> fns = new java.util.LinkedHashSet<>();
                for (Reference r : rm.getReferencesTo(cur)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    fns.add(f != null ? (f.getName() + "@" + f.getEntryPoint()) : ("xref@" + r.getFromAddress()));
                }
                println("STR[" + needle + "] @" + cur + " refs=" + fns);
                a = mem.findBytes(a.add(1), kb, null, true, monitor);
            }
        }
        println("=== done ===");
    }
}
