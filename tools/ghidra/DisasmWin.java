import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
// ADDR=hex, N=count : print N instructions starting a bit before ADDR.
public class DisasmWin extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("ADDR"),16);
    int n=System.getenv("N")!=null?Integer.parseInt(System.getenv("N")):40;
    Address addr=toAddr(a).subtract(60);
    Listing l=currentProgram.getListing();
    InstructionIterator it=l.getInstructions(addr,true);
    int c=0;
    while(it.hasNext()&&c<n){ Instruction in=it.next(); println(in.getAddress()+": "+in); c++; }
  }
}
