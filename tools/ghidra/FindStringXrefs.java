import ghidra.app.script.GhidraScript;
import ghidra.program.model.data.StringDataInstance;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.Reference;
// NEEDLE=substring : print every defined string containing NEEDLE and the functions referencing it.
public class FindStringXrefs extends GhidraScript {
  public void run() throws Exception {
    String needle = System.getenv("NEEDLE");
    DataIterator it = currentProgram.getListing().getDefinedData(true);
    while (it.hasNext()) {
      Data d = it.next();
      StringDataInstance s = StringDataInstance.getStringDataInstance(d);
      String v = s == null ? null : s.getStringValue();
      if (v == null || !v.contains(needle)) continue;
      println(d.getAddress() + "  \"" + v + "\"");
      for (Reference r : currentProgram.getReferenceManager().getReferencesTo(d.getAddress())) {
        Function f = getFunctionContaining(r.getFromAddress());
        println("      <- " + r.getFromAddress() + (f != null ? "  " + f.getName() + "@" + f.getEntryPoint() : ""));
      }
    }
  }
}
