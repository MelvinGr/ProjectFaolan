import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
// CONST=2745 : list every instruction whose immediate == CONST, with containing fn.
public class ConstScan extends GhidraScript {
  public void run() throws Exception {
    long c=Long.parseLong(System.getenv("CONST"),16)&0xFFFFFFFFL;
    InstructionIterator it=currentProgram.getListing().getInstructions(true);
    while(it.hasNext()){
      Instruction in=it.next();
      boolean hit=false;
      for(int op=0;op<in.getNumOperands();op++)
        for(Object o:in.getOpObjects(op))
          if(o instanceof Scalar && (((Scalar)o).getUnsignedValue()&0xFFFFFFFFL)==c) hit=true;
      if(hit){ Function f=getFunctionContaining(in.getAddress());
        println(in.getAddress()+": "+in+"   ["+(f!=null?f.getName()+"@"+f.getEntryPoint():"?")+"]"); }
    }
  }
}
