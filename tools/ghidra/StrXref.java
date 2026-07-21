import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.MemoryAccessException;
import java.util.regex.Pattern;
// REGEX=... : list defined strings matching (case-insensitive), with functions that xref them.
public class StrXref extends GhidraScript {
  public void run() throws Exception {
    Pattern p = Pattern.compile(System.getenv("REGEX"), Pattern.CASE_INSENSITIVE);
    DataIterator di = currentProgram.getListing().getDefinedData(true);
    ReferenceManager rm = currentProgram.getReferenceManager();
    int n=0;
    while (di.hasNext() && n<400) {
      Data d = di.next();
      Object v = d.getValue();
      if (!(v instanceof String)) continue;
      String s = (String) v;
      if (!p.matcher(s).find()) continue;
      StringBuilder fns = new StringBuilder();
      ReferenceIterator ri = rm.getReferencesTo(d.getAddress());
      int refs=0;
      while (ri.hasNext() && refs<8) {
        Reference r = ri.next();
        Function f = getFunctionContaining(r.getFromAddress());
        fns.append(" <- ").append(r.getFromAddress()).append(f!=null?("/"+f.getName()+"@"+f.getEntryPoint()):"/?");
        refs++;
      }
      if (refs>0) { println(d.getAddress()+"  \""+s.replaceAll("\\s+"," ").trim()+"\""+fns); n++; }
    }
    println("=== matched "+n+" referenced strings ===");
  }
}
