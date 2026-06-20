import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
// Find MOV writes whose destination is [reg + 0xA4] (the dead-flag field player+0xA4).
public class FindFieldWriters extends GhidraScript {
  public void run() throws Exception {
    InstructionIterator it = currentProgram.getListing().getInstructions(true);
    int n=0;
    while (it.hasNext()){
      Instruction ins = it.next();
      String s = ins.toString();
      String m = ins.getMnemonicString();
      if (!m.startsWith("MOV")) continue;
      // destination = first operand; write to [reg + 0xa4]
      String op0 = ins.getDefaultOperandRepresentation(0);
      if (op0.contains("0xa4]") && op0.contains("[")){
        Function f = getFunctionContaining(ins.getAddress());
        println(ins.getAddress()+"  "+s+"   ["+(f!=null?f.getName()+"@"+f.getEntryPoint():"?")+"]");
        n++;
      }
    }
    println("TOTAL writes to +0xa4: "+n);
  }
}
