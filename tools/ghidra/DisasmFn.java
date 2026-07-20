import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
// FN=<entry hex>: print the function's instructions (addr: mnemonic operands).
public class DisasmFn extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("FN").trim(),16);
    Function f=getFunctionContaining(toAddr(a));
    if(f==null){println("no fn");return;}
    Listing l=currentProgram.getListing();
    InstructionIterator it=l.getInstructions(f.getBody(),true);
    while(it.hasNext()){ Instruction in=it.next(); println(in.getAddress()+":  "+in); }
  }
}
