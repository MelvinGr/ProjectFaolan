// Recovers the formula that turns a spell's SCALING ATTRIBUTE (RDB 1040005 property field 847 - 716 records
// carrying 808 Intelligence, 810 Wisdom or 804 Strength, #592) into damage.
//
// The RDB says WHICH attribute a spell scales off; it does not say BY HOW MUCH. The coefficient is not in the
// spell record - it is a client tweak (RDB 1000516), and the tweak table's own names are the lead:
//
//     TKW_MagicalDpsAbilityModifier            0.6     (sic - the client misspells TWK_)
//     TWK_PhysicalDpsAbilityModifierStrength   3
//     TWK_PhysicalDpsAbilityModifierDexterity  3
//     TWK_AttributeAdjustment                  0.4
//     TWK_ArmorAbilityModifier                 2
//     TWK_ProtectionAbilityModifier            0.5
//
// A tweak is looked up BY NAME once and cached in a global, so the string's referrer is the loader, not the
// consumer. The chain that matters is therefore two hops: string -> the function that registers it -> the global
// it writes -> everything that reads that global. PART B walks it.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpSpellScaling.java
//
// Output: $FAOLAN_GHIDRA_OUT/spell_scaling.txt
//   PART A - the tweak-name strings, their referrers, and each referrer decompiled (the registration site).
//   PART B - for every global written near a tweak-name reference, every OTHER function that reads it.
//   PART C - the stat-id constants 808/810/804 and the property-field id 847, with containing functions, so the
//            record-side reader can be told apart from the character-sheet side.
//   PART D - strings that name the derived stats themselves ("MagicDamage", "DpsAbility", ...), which is how the
//            client's own vocabulary gets attached to the wire stat ids.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import ghidra.program.model.symbol.RefType;
import java.io.PrintWriter;
import java.util.*;

public class DumpSpellScaling extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final String[] TWEAKS = {
        "TKW_MagicalDpsAbilityModifier",
        "TWK_PhysicalDpsAbilityModifierStrength",
        "TWK_PhysicalDpsAbilityModifierDexterity",
        "TWK_AttributeAdjustment",
        "TWK_ArmorAbilityModifier",
        "TWK_ProtectionAbilityModifier",
        "TWK_BonusDamageCap",
    };

    // The stat ids field 847 takes, plus the field id itself. 808/810/804 are wire stat ids (Intelligence /
    // Wisdom / Strength); 847 is the RDB property-field id that names one of them.
    static final long[] CONSTS = { 808L, 810L, 804L, 847L };

    // The client's own name for the derived quantities, so a decompiled global can be labelled rather than guessed.
    static final String[] VOCAB = {
        "MagicDamage", "DpsAbility", "SpellDamage", "DamageRating", "MagicalDps", "PhysicalDps",
    };

    private DecompInterface decomp;
    private PrintWriter out;

    @Override
    public void run() throws Exception {
        java.io.File file = new java.io.File(OUT, "spell_scaling.txt");
        out = new PrintWriter(file);
        decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        Set<Address> globals = new LinkedHashSet<>();

        out.println("=== PART A: tweak-name strings, referrers, registration sites ===");
        for (String name : TWEAKS) {
            out.println("\n--- \"" + name + "\"");
            List<Address> hits = findStrings(name, true);
            if (hits.isEmpty()) {
                out.println("    (string not present in the binary)");
                continue;
            }
            for (Address at : hits) {
                out.println("    string @ " + at);
                for (Reference ref : currentProgram.getReferenceManager().getReferencesTo(at)) {
                    Function fn = getFunctionContaining(ref.getFromAddress());
                    out.println("        xref from " + ref.getFromAddress()
                        + (fn == null ? "  (no function)" : "  in " + fn.getName() + " @ " + fn.getEntryPoint()));
                    globals.addAll(dataRefsNear(ref.getFromAddress(), 0x60));
                    if (fn != null) {
                        out.println(indent(decompile(fn)));
                    }
                }
            }
        }

        out.println("\n\n=== PART B: readers of the globals written around those registration sites ===");
        for (Address global : globals) {
            List<Function> readers = readersOf(global);
            if (readers.isEmpty()) {
                continue;
            }
            out.println("\n--- global " + global + "  (" + readers.size() + " reader(s))");
            for (Function fn : readers) {
                out.println("\n---- " + fn.getName() + " @ " + fn.getEntryPoint() + " ----");
                out.println(decompile(fn));
            }
        }

        out.println("\n\n=== PART C: stat-id / property-field constants ===");
        for (long value : CONSTS) {
            out.println("\n--- " + value + " (0x" + Long.toHexString(value) + ")");
            int shown = 0;
            for (Instruction insn : instructionsWithScalar(value)) {
                Function fn = getFunctionContaining(insn.getAddress());
                out.println("    " + insn.getAddress() + ": " + insn
                    + "   [" + (fn == null ? "?" : fn.getName() + "@" + fn.getEntryPoint()) + "]");
                shown++;
            }
            out.println("    (" + shown + " site(s))");
        }

        out.println("\n\n=== PART D: derived-stat vocabulary strings ===");
        for (String name : VOCAB) {
            out.println("\n--- \"" + name + "\"");
            for (Address at : findStrings(name, false)) {
                Data data = currentProgram.getListing().getDataAt(at);
                String text = data == null ? "?" : String.valueOf(data.getValue());
                out.println("    " + at + "  " + text);
                for (Reference ref : currentProgram.getReferenceManager().getReferencesTo(at)) {
                    Function fn = getFunctionContaining(ref.getFromAddress());
                    out.println("        xref from " + ref.getFromAddress()
                        + (fn == null ? "  (no function)" : "  in " + fn.getName() + " @ " + fn.getEntryPoint()));
                }
            }
        }

        out.close();
        decomp.dispose();
        println("wrote " + file);
    }

    // Data addresses referenced within `span` bytes after `from` - the tweak's cached global is written next to the
    // name it was looked up under, so this is what turns a registration site into a set of candidate globals.
    private List<Address> dataRefsNear(Address from, int span) {
        List<Address> found = new ArrayList<>();
        Listing listing = currentProgram.getListing();
        Instruction insn = listing.getInstructionAt(from);
        for (int i = 0; insn != null && i < 40; i++) {
            if (insn.getAddress().subtract(from) > span) {
                break;
            }
            for (Reference ref : insn.getReferencesFrom()) {
                RefType type = ref.getReferenceType();
                // Memory only - a data ref can also point at a stack slot or a register, which has no xref index.
                if (type.isData() && ref.getToAddress().isMemoryAddress()
                    && listing.getInstructionAt(ref.getToAddress()) == null) {
                    found.add(ref.getToAddress());
                }
            }
            insn = insn.getNext();
        }
        return found;
    }

    private List<Function> readersOf(Address global) {
        Set<Function> seen = new LinkedHashSet<>();
        for (Reference ref : currentProgram.getReferenceManager().getReferencesTo(global)) {
            Function fn = getFunctionContaining(ref.getFromAddress());
            if (fn != null) {
                seen.add(fn);
            }
        }
        return new ArrayList<>(seen);
    }

    private List<Instruction> instructionsWithScalar(long value) {
        List<Instruction> hits = new ArrayList<>();
        for (MemoryBlock block : currentProgram.getMemory().getBlocks()) {
            if (!block.isExecute()) {
                continue;
            }
            InstructionIterator it = currentProgram.getListing().getInstructions(block.getStart(), true);
            while (it.hasNext() && !monitor.isCancelled()) {
                Instruction insn = it.next();
                if (insn.getAddress().compareTo(block.getEnd()) > 0) {
                    break;
                }
                for (int i = 0; i < insn.getNumOperands(); i++) {
                    for (Object obj : insn.getOpObjects(i)) {
                        if (obj instanceof Scalar && ((Scalar) obj).getUnsignedValue() == value) {
                            hits.add(insn);
                            i = insn.getNumOperands();
                            break;
                        }
                    }
                }
            }
        }
        return hits;
    }

    private String decompile(Function fn) {
        DecompileResults res = decomp.decompileFunction(fn, 60, monitor);
        if (res == null || res.getDecompiledFunction() == null) {
            return "(decompilation failed)";
        }
        return res.getDecompiledFunction().getC();
    }

    private String indent(String text) {
        StringBuilder sb = new StringBuilder();
        for (String line : text.split("\n")) {
            sb.append("            ").append(line).append('\n');
        }
        return sb.toString();
    }

    private List<Address> findStrings(String text, boolean exact) {
        List<Address> hits = new ArrayList<>();
        DataIterator it = currentProgram.getListing().getDefinedData(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Data data = it.next();
            Object value = data.getValue();
            if (!(value instanceof String)) {
                continue;
            }
            String str = (String) value;
            if (exact ? str.equals(text) : str.contains(text)) {
                hits.add(data.getAddress());
            }
        }
        return hits;
    }
}
