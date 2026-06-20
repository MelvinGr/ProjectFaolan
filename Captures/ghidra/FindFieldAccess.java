import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
// Find instructions whose ANY operand references [reg + OFF] (OFF env, hex like "abc").
public class FindFieldAccess extends GhidraScript {
  public void run() throws Exception {
    String off = System.getenv("OFF").trim().toLowerCase();
    String needle = "0x" + off + "]";
    InstructionIterator it = currentProgram.getListing().getInstructions(true);
    int n=0;
    while (it.hasNext()){
      Instruction ins = it.next();
      boolean hit=false;
      for (int op=0; op<ins.getNumOperands(); op++){
        String r = ins.getDefaultOperandRepresentation(op);
        if (r.contains(needle) && r.contains("[")) { hit=true; break; }
      }
      if (hit){
        Function f = getFunctionContaining(ins.getAddress());
        println(ins.getAddress()+"  "+ins+"   ["+(f!=null?f.getName()+"@"+f.getEntryPoint():"?")+"]");
        n++;
      }
    }
    println("TOTAL accesses to +0x"+off+": "+n);
  }
}
