import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.mem.Memory;
// ADDR=hex vtable address : print first 16 slots as function addrs.
public class VtableAt extends GhidraScript {
  public void run() throws Exception {
    long a=Long.parseLong(System.getenv("ADDR"),16);
    Address base=toAddr(a); Memory mem=currentProgram.getMemory();
    for(int i=0;i<16;i++){ long fn=mem.getInt(base.add(i*4L))&0xFFFFFFFFL;
      println("  slot "+i+" -> "+Long.toHexString(fn)); }
  }
}
