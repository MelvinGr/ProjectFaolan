import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
public class StrXref extends GhidraScript {
    public void run() throws Exception {
        String needle = System.getenv("STR");
        byte[] pat = needle.getBytes("ASCII");
        Address a = currentProgram.getMinAddress();
        ReferenceManager rm = currentProgram.getReferenceManager();
        while (true) {
            a = find(a.add(1), pat);
            if (a == null) break;
            // ensure null-terminated exact-ish match start
            println("string @" + a);
            for (Reference r : rm.getReferencesTo(a)) {
                Function f = getFunctionContaining(r.getFromAddress());
                println("   <- " + r.getFromAddress() + " in " + (f != null ? f.getName()+"@"+f.getEntryPoint() : "?"));
            }
        }
    }
}
