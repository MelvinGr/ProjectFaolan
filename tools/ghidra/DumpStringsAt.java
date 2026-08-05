import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
// ADDRS=1f6df88,1f6df94 : print the C string at each address (a general one-off helper).
public class DumpStringsAt extends GhidraScript {
  public void run() throws Exception {
    for (String s : System.getenv("ADDRS").split(",")) {
      Address a = toAddr(Long.parseLong(s.trim(), 16));
      StringBuilder sb = new StringBuilder();
      for (int i = 0; i < 200; i++) {
        byte b = getByte(a.add(i));
        if (b == 0) break;
        sb.append((char) (b & 0xFF));
      }
      println(s.trim() + " = \"" + sb + "\"");
    }
  }
}
