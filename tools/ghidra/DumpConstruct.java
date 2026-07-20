// Decompile n3InfoItemRemote::Construct (the caller that frames identity + length before ReadSubClass) by
// anchoring on its error string "ReadSubClass for IIR". #363.
import ghidra.app.script.GhidraScript;
import ghidra.app.decompiler.*;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.util.*;
import ghidra.program.model.mem.*;
import java.io.PrintWriter;
import java.util.*;

public class DumpConstruct extends GhidraScript {
    @Override public void run() throws Exception {
        PrintWriter out = new PrintWriter("Captures/ghidra/output/construct.txt");
        DecompInterface di = new DecompInterface(); di.setOptions(new DecompileOptions()); di.openProgram(currentProgram);
        String needle = "ReadSubClass for IIR";
        Set<Function> seen = new LinkedHashSet<>();
        // scan defined strings for the needle, then xref -> containing function
        DataIterator it = currentProgram.getListing().getDefinedData(true);
        while (it.hasNext()) {
            Data d = it.next();
            Object v = d.getValue();
            if (!(v instanceof String)) continue;
            if (!((String) v).contains(needle)) continue;
            out.println("STRING @" + d.getAddress() + " : " + v);
            ReferenceIterator ri = currentProgram.getReferenceManager().getReferencesTo(d.getAddress());
            while (ri.hasNext()) {
                Reference r = ri.next();
                Function f = getFunctionContaining(r.getFromAddress());
                if (f != null) seen.add(f);
            }
        }
        for (Function f : seen) {
            out.println("\n################## " + f.getName() + " @" + f.getEntryPoint() + " size=" + f.getBody().getNumAddresses());
            DecompileResults res = di.decompileFunction(f, 200, monitor);
            out.println(res != null && res.decompileCompleted() ? res.getDecompiledFunction().getC() : "<fail>");
        }
        out.close();
        println("done -> Captures/ghidra/output/construct.txt");
    }
}
