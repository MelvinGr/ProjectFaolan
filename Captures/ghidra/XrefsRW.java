import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.Function;
import ghidra.program.model.symbol.*;
// ADDR=<data addr>: list refs with R/W type + containing fn.
public class XrefsRW extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("ADDR"),16);
    Address t=toAddr(a);
    ReferenceManager rm=currentProgram.getReferenceManager();
    for (Reference r: rm.getReferencesTo(t)){
      Function f=getFunctionContaining(r.getFromAddress());
      println("  "+r.getFromAddress()+"  "+r.getReferenceType()+"  ["+(f!=null?(f.getName()+"@"+f.getEntryPoint()):"?")+"]");
    }
  }
}
