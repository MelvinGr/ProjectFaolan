// Settles which protobuf message DeleteItemIIR_t (IIR opcode 0x72491DE9, "DropItem") actually carries.
//
// The captures cannot answer it: the whole 104-capture corpus contains ZERO DeleteItemIIR packets (the IIR label
// 0x72491de9 never appears), so nothing on the wire pins it. Meanwhile the client schema defines TWO adjacent
// messages in the same ContainerAddItemIIR.proto:
//
//   ProtoMoveInventoryItemIIR { ProtoID32 source_container_id=1; int32 source_item_pos=2;
//                               ProtoID32 destination_container_id=3; int32 destination_item_pos=4; }
//   ProtoDeleteItemIIR        { ProtoID32 container_id=1;        int32 item_pos=2;
//                               ProtoID32 template_id=3; }
//
// which are INDISTINGUISHABLE on the wire for fields 1-3 and differ only in whether field 4 exists. So parsing a
// delete as a move "works" (it reads only fields 1-2) while silently misfiling template_id as a destination
// container. MoveInventoryItemCodec asserted "despite the name it carries the same ProtoMoveInventoryItemIIR body" -
// an unverified claim that the client's own schema argues against.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpDeleteItemIir.java
//
// Output: $FAOLAN_GHIDRA_OUT/delete_item_iir.txt
//   PART A - every occurrence of the type-name strings ("DeleteItemIIR_t", "ProtoDeleteItemIIR",
//            "ProtoMoveInventoryItemIIR", "MoveInventoryItemIIR_t") and the functions referencing them.
//   PART B - every function whose code references the IIR opcode constant 0x72491DE9, decompiled.
//   PART C - the same for MoveInventoryItemIIR's 0x2A8A1B49, as the known-good control: whatever pattern binds
//            that opcode to ProtoMoveInventoryItemIIR is the pattern to look for in PART B.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.address.AddressSetView;
import ghidra.program.model.listing.*;
import ghidra.program.model.mem.MemoryBlock;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
import java.util.*;

public class DumpDeleteItemIir extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long DELETE_OPCODE = 0x72491DE9L;
    static final long MOVE_OPCODE = 0x2A8A1B49L;

    static final String[] NAMES = {
        "DeleteItemIIR_t", "ProtoDeleteItemIIR",
        "MoveInventoryItemIIR_t", "ProtoMoveInventoryItemIIR",
    };

    private DecompInterface decomp;
    private PrintWriter out;

    @Override
    public void run() throws Exception {
        java.io.File file = new java.io.File(getProjectRootFolder().getProjectLocator().getLocation(), "delete_item_iir.txt");
        out = new PrintWriter(file);
        decomp = new DecompInterface();
        decomp.openProgram(currentProgram);

        out.println("=== PART A: type-name strings and their referrers ===");
        for (String name : NAMES) {
            out.println("\n--- \"" + name + "\"");
            List<Address> hits = findStrings(name);
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
                }
            }
        }

        out.println("\n\n=== PART B: functions referencing the DeleteItemIIR opcode 0x"
            + Long.toHexString(DELETE_OPCODE) + " ===");
        dumpConstReferrers(DELETE_OPCODE);

        out.println("\n\n=== PART C: control - functions referencing the MoveInventoryItemIIR opcode 0x"
            + Long.toHexString(MOVE_OPCODE) + " ===");
        dumpConstReferrers(MOVE_OPCODE);

        out.close();
        decomp.dispose();
        println("wrote " + file);
    }

    // Every function containing an instruction with `value` as a scalar operand, decompiled once each.
    private void dumpConstReferrers(long value) {
        Set<Function> seen = new LinkedHashSet<>();
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
                if (!hasScalar(insn, value)) {
                    continue;
                }
                Function fn = getFunctionContaining(insn.getAddress());
                if (fn != null) {
                    seen.add(fn);
                } else {
                    out.println("\n-- scalar @ " + insn.getAddress() + " (outside any function): " + insn);
                }
            }
        }
        if (seen.isEmpty()) {
            out.println("(no function references this constant)");
            return;
        }
        for (Function fn : seen) {
            out.println("\n---- " + fn.getName() + " @ " + fn.getEntryPoint() + " ----");
            out.println(decompile(fn));
        }
    }

    private boolean hasScalar(Instruction insn, long value) {
        for (int i = 0; i < insn.getNumOperands(); i++) {
            for (Object obj : insn.getOpObjects(i)) {
                if (obj instanceof Scalar && ((Scalar) obj).getUnsignedValue() == value) {
                    return true;
                }
            }
        }
        return false;
    }

    private String decompile(Function fn) {
        DecompileResults res = decomp.decompileFunction(fn, 60, monitor);
        if (res == null || res.getDecompiledFunction() == null) {
            return "(decompilation failed)";
        }
        return res.getDecompiledFunction().getC();
    }

    private List<Address> findStrings(String text) {
        List<Address> hits = new ArrayList<>();
        DataIterator it = currentProgram.getListing().getDefinedData(true);
        while (it.hasNext() && !monitor.isCancelled()) {
            Data data = it.next();
            Object value = data.getValue();
            if (value instanceof String && ((String) value).contains(text)) {
                hits.add(data.getAddress());
            }
        }
        return hits;
    }
}
