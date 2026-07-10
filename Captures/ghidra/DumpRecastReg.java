// #271: FUN_008bde20 is the GameActionData descriptor registrar (desc+8 = typeId, stashes desc ptr in DAT_0297d418).
// The recast type's registration calls it then appends keyList indices 0x27 (recast) + 0x58 (spell). Find the
// callers of the registrar + whoever writes the "current descriptor" global DAT_0297d418 (the keyList appender),
// so the recast registration site can be located.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;

public class DumpRecastReg extends GhidraScript {
    void callers(long addr, String label) throws Exception {
        Function f = getFunctionContaining(toAddr(addr));
        println("== callers of " + label + " @0x" + Long.toHexString(addr) + (f != null ? " (" + f.getName() + ")" : "") + " ==");
        int n = 0;
        for (Reference r : currentProgram.getReferenceManager().getReferencesTo(f != null ? f.getEntryPoint() : toAddr(addr))) {
            Function c = getFunctionContaining(r.getFromAddress());
            println("  from " + r.getFromAddress() + " in " + (c != null ? c.getName() + "@" + c.getEntryPoint() : "(none)"));
            if (++n > 60) { println("  ...more"); break; }
        }
        if (n == 0) println("  (none)");
    }

    void refs(long addr, String label) throws Exception {
        println("\n== refs to " + label + " @0x" + Long.toHexString(addr) + " ==");
        int n = 0;
        for (Reference r : currentProgram.getReferenceManager().getReferencesTo(toAddr(addr))) {
            Function c = getFunctionContaining(r.getFromAddress());
            println("  " + r.getReferenceType() + " from " + r.getFromAddress() + " in " + (c != null ? c.getName() + "@" + c.getEntryPoint() : "(none)"));
            if (++n > 60) { println("  ...more"); break; }
        }
        if (n == 0) println("  (none)");
    }

    public void run() throws Exception {
        callers(0x008bde20L, "registrar FUN_008bde20");
        refs(0x0297d418L, "current-descriptor global DAT_0297d418");
        println("\n== done ==");
    }
}
