// Every instruction inside an ADDRESS RANGE whose operands mention a struct offset - the range-scoped
// FindOffAccess. A small offset like +0x50 has thousands of hits image-wide and is useless as a lead, but the
// class that owns it usually lives in one module, and inside that module the same offset is a handful of sites.
//
//   OFF=50 FROM=00898000 TO=008d0000 /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc \
//     -process AgeOfConan.exe -noanalysis -scriptPath tools/ghidra -postScript FindOffInRange.java
//
// Written for #790: the GameActionData criteria vector is at +0x50 (the reader does `ADD ECX,0x50; CALL
// <vector reserve>`), and the question was which OTHER function reaches it - i.e. who EVALUATES the conditions.
// Matches both `[reg + 0x50]` and the `ADD reg,0x50` / `LEA reg,[reg + 0x50]` forms a vector-by-pointer call
// site uses, which is why it looks at the raw operand text rather than at scalar operands.
// Output: $FAOLAN_GHIDRA_OUT/off_in_range.txt, grouped by containing function.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import java.io.PrintWriter;
import java.util.*;

public class FindOffInRange extends GhidraScript {
	static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

	public void run() throws Exception {
		var off = System.getenv().getOrDefault("OFF", "50").trim().toLowerCase();
		var needle = "0x" + off;
		Address from = toAddr(Long.parseLong(System.getenv().getOrDefault("FROM", "0").trim(), 16));
		Address to = toAddr(Long.parseLong(System.getenv().getOrDefault("TO", "ffffffff").trim(), 16));

		Map<String, List<String>> byFunction = new LinkedHashMap<>();
		InstructionIterator it = currentProgram.getListing().getInstructions(from, true);
		while (it.hasNext()) {
			Instruction ins = it.next();
			if (ins.getAddress().compareTo(to) > 0)
				break;

			var text = ins.toString();
			// `0x50` also matches `0x500`, so require the offset to end the token.
			var at = text.indexOf(needle);
			if (at < 0)
				continue;

			var after = at + needle.length();
			if (after < text.length() && Character.digit(text.charAt(after), 16) >= 0)
				continue;

			Function f = getFunctionContaining(ins.getAddress());
			var key = f != null ? f.getName() + "@" + f.getEntryPoint() : "?";
			byFunction.computeIfAbsent(key, k -> new ArrayList<>()).add(ins.getAddress() + ":  " + text);
		}

		try (PrintWriter w = new PrintWriter(OUT + "off_in_range.txt")) {
			for (var e : byFunction.entrySet()) {
				w.println(e.getKey() + "   " + e.getValue().size() + " site(s)");
				for (var line : e.getValue())
					w.println("    " + line);
			}
		}

		println("functions: " + byFunction.size() + " -> " + OUT + "off_in_range.txt");
	}
}
