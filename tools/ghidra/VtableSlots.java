import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.*;
// ADDR=<reader fn>: find the vtable slot(s) holding it, then print the surrounding vtable pointers + the fn each points to.
public class VtableSlots extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("ADDR"),16);
    Address t=toAddr(a);
    ReferenceManager rm=currentProgram.getReferenceManager();
    Memory mem=currentProgram.getMemory();
    for (Reference r: rm.getReferencesTo(t)){
      Address slot=r.getFromAddress();
      println("=== vtable slot @"+slot+" holds reader; dumping slots -8..+16 ===");
      for(int i=-8;i<=16;i++){
        Address s=slot.add((long)i*4);
        try{
          long p=mem.getInt(s)&0xFFFFFFFFL;
          Address fp=toAddr(p);
          Function f=getFunctionContaining(fp);
          println(String.format("  [%+3d] @%s -> %08x %s", i, s, p, f!=null?f.getName():"?"));
        }catch(Exception e){ println("  ["+i+"] (unreadable)"); }
      }
    }
  }
}
