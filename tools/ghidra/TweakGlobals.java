// For each tweakable NAME (TWEAKS env, comma-separated): find its string, walk the static-registration stub that
// binds it to a global slot, and list every reference to that slot - so you can tell whether the CLIENT actually
// consumes the tweak or merely registers it (a registered-but-never-read tweak is server-side).
//
// The stubs sit in a table of identical 5-instruction thunks:
//     PUSH EDI / MOV EDX,<name string> / MOV EDI,<global slot> / CALL <register> / PUSH <init fn> ...
// so the slot is the operand of the MOV EDI two instructions after the string reference.
//
//   TWEAKS="TWK_TargetPerLevelArmor,TWK_Selection_Radius" /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra \
//     aoc -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript TweakGlobals.java
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.listing.Instruction;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.ReferenceManager;

public class TweakGlobals extends GhidraScript {
    @Override
    public void run() throws Exception {
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (String raw : System.getenv("TWEAKS").split(",")) {
            String name = raw.trim();
            if (name.isEmpty()) continue;
            Address s = currentProgram.getMemory().findBytes(currentProgram.getMinAddress(), name.getBytes("US-ASCII"), null, true, monitor);
            if (s == null) { println(name + ": string NOT FOUND"); continue; }

            Address slot = null;
            Address refFrom = null;
            for (Reference r : rm.getReferencesTo(s)) {
                refFrom = r.getFromAddress();
                // the MOV EDI,<slot> that follows the MOV EDX,<string>
                Instruction ins = getInstructionAt(refFrom);
                for (int i = 0; ins != null && i < 3; i++) {
                    ins = ins.getNext();
                    if (ins == null) break;
                    if (!"MOV".equals(ins.getMnemonicString())) continue;
                    Object[] ops = ins.getOpObjects(1);
                    if (ops.length == 1 && ops[0] instanceof Scalar sc) { slot = toAddr(sc.getUnsignedValue()); break; }
                }
                if (slot != null) break;
            }

            if (slot == null) { println(name + ": string @" + s + " but no registration stub found"); continue; }

            int reads = 0, writes = 0, other = 0;
            StringBuilder readers = new StringBuilder();
            for (Reference r : rm.getReferencesTo(slot)) {
                Function f = getFunctionContaining(r.getFromAddress());
                String where = r.getFromAddress() + (f != null ? " (" + f.getName() + ")" : "");
                if (r.getReferenceType().isWrite()) writes++;
                else if (r.getReferenceType().isRead()) { reads++; readers.append("\n      READ  ").append(where); }
                else other++;
            }

            println(String.format("%-44s string@%s slot@%s  reads=%d writes=%d other=%d%s%s",
                    name, s, slot, reads, writes, other,
                    reads == 0 ? "   <-- CLIENT NEVER READS IT" : "", readers));
        }
    }
}
