import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.scalar.Scalar;
// TARGETS=addr,addr CONST=14 : for each target fn, find call sites; report those with CONST as an immediate in the preceding ~10 instrs.
public class CallsWithConst extends GhidraScript {
  public void run() throws Exception {
    long c=Long.parseLong(System.getenv("CONST"),16)&0xFFFFFFFFL;
    ReferenceManager rm=currentProgram.getReferenceManager();
    Listing lst=currentProgram.getListing();
    for(String h:System.getenv("TARGETS").split(",")){
      Address t=toAddr(Long.parseLong(h.trim(),16));
      for(Reference r:rm.getReferencesTo(t)){
        if(!r.getReferenceType().isCall()) continue;
        Address call=r.getFromAddress();
        Instruction ins=lst.getInstructionAt(call);
        boolean hit=false;
        for(int i=0;i<26 && ins!=null;i++){
          for(int op=0;op<ins.getNumOperands();op++)
            for(Object o:ins.getOpObjects(op))
              if(o instanceof Scalar && (((Scalar)o).getUnsignedValue()&0xFFFFFFFFL)==c) hit=true;
          ins=ins.getPrevious();
        }
        if(hit){ Function f=getFunctionContaining(call); println("FIRE-SITE call@"+call+" -> "+h+"  in ["+(f!=null?f.getName()+"@"+f.getEntryPoint():"?")+"]"); }
      }
    }
  }
}
