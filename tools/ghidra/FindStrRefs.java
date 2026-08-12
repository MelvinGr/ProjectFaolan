// NEEDLES=a,b,c : locate each exact ASCII string in memory and list what REFERENCES it.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.Reference;
import java.io.PrintWriter;
public class FindStrRefs extends GhidraScript {
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";
    public void run() throws Exception {
        PrintWriter out = new PrintWriter(OUT + "strrefs.txt");
        for (String n : System.getenv("NEEDLES").split(",")) {
            n = n.trim(); if (n.isEmpty()) continue;
            Address a = null; int found = 0;
            Address cur = currentProgram.getMinAddress();
            while ((cur = find(cur, n.getBytes())) != null) {
                found++;
                out.print(n + "\t@" + cur);
                var refs = currentProgram.getReferenceManager().getReferencesTo(cur);
                int k = 0;
                for (Reference r : refs) {
                    Function f = getFunctionContaining(r.getFromAddress());
                    out.print("\t<- " + r.getFromAddress() + (f == null ? "" : " " + f.getName()));
                    if (++k >= 6) break;
                }
                if (k == 0) out.print("\t<no xref>");
                out.println();
                cur = cur.add(1);
                if (found > 4) break;
            }
            if (found == 0) out.println(n + "\t<string NOT in the binary>");
        }
        out.close(); println("wrote " + OUT + "strrefs.txt");
    }
}
