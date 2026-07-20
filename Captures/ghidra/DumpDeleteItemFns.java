// Follow-up to DumpDeleteItemIir.java: decompiles the two IIR constructors that PART A's string xrefs pointed at,
// which is what actually settles which protobuf message DeleteItemIIR_t carries.
//
//   ln -sfn "$PWD/Captures/ghidra/output" /tmp/aoc_gh          # Ghidra rejects the repo path's '#'
//   mkdir -p /tmp/aoc_ghscripts && cp Captures/ghidra/*.java /tmp/aoc_ghscripts/
//   /opt/ghidra/support/analyzeHeadless /tmp/aoc_gh aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath /tmp/aoc_ghscripts -postScript DumpDeleteItemFns.java
//
// Output: Captures/ghidra/output/delete_item_fns.txt. The answer:
//   FUN_008d62f0 "DeleteItemIIR_t"        -> unaff_ESI[7] = ProtoDeleteItemIIR::vftable,        3 fields
//   FUN_008d6090 "MoveInventoryItemIIR_t" -> unaff_ESI[7] = ProtoMoveInventoryItemIIR::vftable, 4 fields
// so the drop carries ProtoDeleteItemIIR - see Faolan.Core.Network/Codecs/Inventory/InventoryDeleteCodec.cs.
import ghidra.app.decompiler.*;
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;

public class DumpDeleteItemFns extends GhidraScript {
    static final String[] FNS = { "008d62f0", "008d6090", "004e4c20" };
    static final String[] DESC = { "0220f80c", "0220f7e4" }; // ProtoDeleteItemIIR / ProtoMoveInventoryItemIIR

    @Override
    public void run() throws Exception {
        java.io.File file = new java.io.File(getProjectRootFolder().getProjectLocator().getLocation(), "delete_item_fns.txt");
        PrintWriter out = new PrintWriter(file);
        DecompInterface d = new DecompInterface();
        d.openProgram(currentProgram);

        for (String s : FNS) {
            Address a = currentProgram.getAddressFactory().getAddress(s);
            Function fn = getFunctionContaining(a);
            out.println("\n==================== FUN_" + s + " ====================");
            if (fn == null) { out.println("(no function)"); continue; }
            DecompileResults r = d.decompileFunction(fn, 120, monitor);
            out.println(r != null && r.getDecompiledFunction() != null ? r.getDecompiledFunction().getC() : "(decompile failed)");
        }

        out.println("\n\n=== xrefs to the protobuf descriptor name strings ===");
        for (String s : DESC) {
            Address a = currentProgram.getAddressFactory().getAddress(s);
            out.println("\n--- " + s + " (" + currentProgram.getListing().getDataAt(a) + ")");
            for (Reference ref : currentProgram.getReferenceManager().getReferencesTo(a)) {
                Function fn = getFunctionContaining(ref.getFromAddress());
                out.println("    from " + ref.getFromAddress() + (fn == null ? "" : "  in " + fn.getName() + " @ " + fn.getEntryPoint()));
            }
        }
        out.close();
        d.dispose();
        println("wrote " + file);
    }
}
