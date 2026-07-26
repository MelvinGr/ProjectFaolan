// For each tweakable NAME (TWEAKS env, comma-separated): find its string, walk the static-registration stub that
// binds it to a global slot, and list every reference to that slot AND to slot+8 - so you can tell whether the
// CLIENT actually consumes the tweak or merely registers it.
//
// ⚠️ The slot holds a POINTER to the tweak object; the VALUE lives at slot+8, and that is the address consuming code
// names. Reporting only the slot's own references produces false "never read" verdicts - it did for
// TWK_OOCHealthRegen{Con,Str}Modifier, which the client reads at 0x02D244A0/B0 = slot+8 (#442). Hence both are
// counted, and "never read" now means neither was referenced.
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

            // Both the slot (the object pointer) and slot+8 (the VALUE the consumer reads) - see the header note.
            int reads = 0, writes = 0, other = 0;
            StringBuilder readers = new StringBuilder();
            Address value = slot.add(8);
            for (Address at : new Address[] { slot, value }) {
                for (Reference r : rm.getReferencesTo(at)) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    String where = r.getFromAddress() + (f != null ? " (" + f.getName() + ")" : "")
                            + (at.equals(value) ? " [slot+8 = the value]" : "");
                    if (r.getReferenceType().isWrite()) writes++;
                    else if (r.getReferenceType().isRead()) { reads++; readers.append("\n      READ  ").append(where); }
                    else other++;
                }
            }

            println(String.format("%-44s string@%s slot@%s value@%s  reads=%d writes=%d other=%d%s%s",
                    name, s, slot, value, reads, writes, other,
                    reads == 0 ? "   <-- no reference to EITHER address (check the disassembly before concluding)" : "", readers));
        }
    }
}
