// START=hex END=hex : every ASCII string referenced FROM code in [START,END), with the referencing function.
// The reverse of StrXrefs - use it to NAME an anonymous subsystem from the log/assert strings its own
// function band references (written for #464's snapshot tail lists D/E).
// Output: $FAOLAN_GHIDRA_OUT/strings_in_range.txt
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.PrintWriter;

public class StringsInRange extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    String asciiAt(Address a) throws Exception {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < 200; i++) {
            int b;
            try { b = getByte(a.add(i)) & 0xFF; } catch (Exception e) { break; }
            if (b == 0) break;
            if (b < 0x20 && b != '\n' && b != '\t') return null;
            if (b > 0x7E) return null;
            sb.append((char) b);
        }
        return sb.length() >= 4 ? sb.toString() : null;
    }

    @Override
    public void run() throws Exception {
        long start = Long.parseLong(System.getenv("START"), 16);
        long end = Long.parseLong(System.getenv("END"), 16);
        try (PrintWriter w = new PrintWriter(OUT + "strings_in_range.txt")) {
            InstructionIterator it = currentProgram.getListing().getInstructions(toAddr(start), true);
            while (it.hasNext()) {
                Instruction ins = it.next();
                if (ins.getAddress().getOffset() >= end) break;
                for (Reference r : ins.getReferencesFrom()) {
                    if (!r.getReferenceType().isData()) continue;
                    String s = asciiAt(r.getToAddress());
                    if (s != null) {
                        Function f = getFunctionContaining(ins.getAddress());
                        w.println((f != null ? f.getName() : "?") + " " + ins.getAddress() + "  \"" + s.replace("\n", "\\n") + "\"");
                    }
                }
            }
        }
        println("wrote " + OUT + "strings_in_range.txt");
    }
}
