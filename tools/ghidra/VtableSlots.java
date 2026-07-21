import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.*;
// SYM=DamageResultIIR_c::vftable : print the first 16 vtable slot function addresses.
public class VtableSlots extends GhidraScript {
  public void run() throws Exception {
    String want=System.getenv("SYM");
    SymbolTable st=currentProgram.getSymbolTable();
    SymbolIterator it=st.getSymbolIterator(want,true);
    if(!it.hasNext()){ println("no symbol "+want); return; }
    Symbol s=it.next(); Address a=s.getAddress();
    println("vftable "+want+" @ "+a);
    Memory mem=currentProgram.getMemory();
    for(int i=0;i<16;i++){
      Address slot=a.add(i*4L);
      long fn=mem.getInt(slot)&0xFFFFFFFFL;
      println("  slot "+i+" -> "+Long.toHexString(fn));
    }
  }
}
