// The CALL-form GameAction registrations: `PUSH <e_Type_ string>; MOV EDX,<short name>; MOV ECX,<enumId>;
// CALL FUN_008bde20`. Recover (name, enumId) for each call site.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;
import ghidra.program.model.symbol.*;
import java.io.*;
import java.util.*;

public class DumpGameActionTypeIdsCallForm extends GhidraScript {
	static final String OUT = (System.getenv("FAOLAN_GHIDRA_OUT") != null
			? System.getenv("FAOLAN_GHIDRA_OUT") : System.getProperty("java.io.tmpdir")) + "/";

	String strAt(long a) {
		try { Data d = getDataAt(toAddr(a)); if (d != null && d.getValue() instanceof String) return (String) d.getValue(); } catch (Exception e) { }
		return null;
	}

	public void run() throws Exception {
		PrintWriter w = new PrintWriter(new FileWriter(OUT + "ga_call.tsv"));
		w.println("name\tenumId\tcallSite");
		Address target = toAddr(0x008bde20L);
		ReferenceManager rm = currentProgram.getReferenceManager();
		Listing lst = currentProgram.getListing();

		for (Reference r : rm.getReferencesTo(target)) {
			if (!r.getReferenceType().isCall()) continue;
			Instruction ins = lst.getInstructionAt(r.getFromAddress());
			String name = null; Long enumId = null;
			// walk backwards a short window collecting the string push and the ECX immediate
			Instruction cur = ins;
			for (int i = 0; i < 10 && cur != null; i++) {
				cur = cur.getPrevious();
				if (cur == null) break;
				String mn = cur.getMnemonicString().toUpperCase();
				for (int op = 0; op < cur.getNumOperands(); op++) {
					for (Object o : cur.getOpObjects(op)) {
						if (!(o instanceof Scalar)) continue;
						long v = ((Scalar) o).getUnsignedValue();
						String s = strAt(v);
						if (s != null && s.startsWith("e_Type_") && name == null) name = s;
						else if (enumId == null && v >= 0xCF00 && v <= 0xD100 && mn.startsWith("MOV")) enumId = v;
					}
				}
				if (name != null && enumId != null) break;
			}
			if (name != null && enumId != null)
				w.println(name + "\t" + enumId + "\t" + r.getFromAddress());
		}
		w.close();
		println("wrote " + OUT + "ga_call.tsv");
	}
}
