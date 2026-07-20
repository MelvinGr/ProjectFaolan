// #271: the GameActionData descriptor table (DAT_025077c8) + stat-key table are runtime-built (zero in the exe).
// Find the code that POPULATES the descriptors (writes the per-type keyLists) and the SetSpellRecastTime type
// registration, so the recast type id + its keyList indices can be read from the init immediates.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;

public class DumpDescriptorInit extends GhidraScript {
    void refsTo(long addr, String label) throws Exception {
        Address t = toAddr(addr);
        ReferenceManager rm = currentProgram.getReferenceManager();
        println("== refs to " + label + " @0x" + Long.toHexString(addr) + " ==");
        int n = 0;
        for (Reference r : rm.getReferencesTo(t)) {
            Function f = getFunctionContaining(r.getFromAddress());
            println("  " + r.getReferenceType() + " from " + r.getFromAddress() + "  in " + (f != null ? f.getName() + "@" + f.getEntryPoint() : "(none)"));
            if (++n > 40) { println("  ...more"); break; }
        }
        if (n == 0) println("  (none)");
    }

    public void run() throws Exception {
        refsTo(0x025077c8L, "descriptorTable DAT_025077c8");
        refsTo(0x01fd7aacL, "SetSpellRecastTime vtable");
        // the descriptor lookup itself - who calls FUN_008c3910 (besides the deserializer)?
        Function lookup = getFunctionAt(toAddr(0x008c3910L));
        if (lookup != null) {
            println("\n== callers of FUN_008c3910 (descriptor lookup) ==");
            for (Reference r : currentProgram.getReferenceManager().getReferencesTo(lookup.getEntryPoint())) {
                Function f = getFunctionContaining(r.getFromAddress());
                println("  from " + r.getFromAddress() + " in " + (f != null ? f.getName() + "@" + f.getEntryPoint() : "(none)"));
            }
        }
        println("\n== done ==");
    }
}
