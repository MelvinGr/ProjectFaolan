// Names the bits of the guild rank PERMISSION mask - the `permissions` word RDB 1000518's `GuildRankDataTable`
// carries per rung, whose meaning `docs/rdb/client-data-tables.md` recorded as "not decoded" (Gitea #799).
//
// The mask is what decides who may withdraw from a guild bank, and the corpus cannot answer it: the one captured
// banking session is that guild's own LEADER banking, which is consistent with every possible threshold. The
// client's data holds the answer and the client's CODE names the bits.
//
//   /opt/ghidra/support/analyzeHeadless <data-repo>/Ghidra aoc -process AgeOfConan.exe -noanalysis \
//     -scriptPath tools/ghidra -postScript DumpGuildRankPermissions.java
//
// Output: $FAOLAN_GHIDRA_OUT/guild_rank_permissions.txt.
//
// ## Where the names come from
//
// The client's UI is Lua bound to C++ through named `com.GameInterface.*` registries, and the guild one declares
// `com.GameInterface.Guild.GuildRank` with the members `m_rankNr` and `m_access` (the mask) plus
// `com.GameInterface.Guild.GuildPermission` with `m_permissionId` / `m_permissionText`. `FUN_01474450` builds the
// permission LIST the management view renders, one block per right, and each block is the whole identity:
//
//     MOV ECX, <token>            ; the LDB token that labels the right
//     ...elf28 hash of it into ESI...
//     PUSH <token> / PUSH ESI / PUSH 0x64   ; LDB category 100 lookup -> the label text
//     CALL FUN_0060F190
//     PUSH EAX                    ; the text
//     PUSH <bit>                  ; <- m_permissionId, and it IS the bit
//     CALL FUN_01476180           ; GuildPermission(id, text)
//
// So the constant pushed immediately before the ctor call names the token loaded at the top of the same block.
// The script walks the blocks in address order and prints the pairing, which is the whole proof.
//
// ## What it yields (and what it settles)
//
//     0x001 CanDisbandGuild                  0x080 ..._CanChangeGoverningForm
//     0x002 ..._CanInviteOthers              0x100 ..._CanChangeMotd
//     0x004 ..._CanKickOthers                0x200 ..._CanChangeName
//     0x008 ..._CanPromote                   0x400 ..._CanChangeRecruitmentMode
//     0x010 ..._CanDemote                    0x800 ..._CanChangeVulnerabilityWindow
//     0x040 ..._CanWithdrawMoney
//
// ⚠️ 0x020 is SKIPPED - the shipped masks set it on the top rung, so it is a right the management view does not
// render, not an unused bit. The same goes for everything above 0x800 inside the leader's `0x7FFFFFFF`.
//
// Crossed against the shipped table (`--report rdb-dump --report-arg 1000518`), whose six distinct masks are
// `0 ⊂ 30 ⊂ 286 ⊂ 6430 ⊂ 7518 ⊂ 0x7FFFFFFF`, `WithdrawMoney` (0x40) is in 7518 and 0x7FFFFFFF and in neither
// 6430 nor below. That is: **the top two rungs may withdraw on six governing forms, and on Stygian the leader
// alone** (its second rung is 6430). Transcribed into `Faolan.Core.Constants.GuildRankLadder`.
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.Address;
import ghidra.program.model.listing.*;
import ghidra.program.model.scalar.Scalar;

import java.io.PrintWriter;

public class DumpGuildRankPermissions extends GhidraScript {
    // Output dir: $FAOLAN_GHIDRA_OUT if set, else the system temp dir (no machine/repo path in the repo).
    static final String OUT = System.getenv().getOrDefault("FAOLAN_GHIDRA_OUT", System.getProperty("java.io.tmpdir")) + "/";

    static final long PERMISSION_LIST_BUILDER = 0x01474450L; // the management view's permission list
    static final long PERMISSION_CTOR = 0x01476180L;         // GuildPermission(id, text)
    static final long FROM = 0x01474c60L, TO = 0x01475160L;

    public void run() throws Exception {
        PrintWriter w = new PrintWriter(OUT + "guild_rank_permissions.txt");
        Function f = getFunctionContaining(toAddr(PERMISSION_LIST_BUILDER));
        w.println("---- " + (f == null ? "(no function)" : f.getName()) + " @ " + Long.toHexString(PERMISSION_LIST_BUILDER) + " ----");
        w.println("  bit    token");

        Listing listing = currentProgram.getListing();
        String pendingToken = null; // the token string of the block being walked
        long pendingBit = -1;       // the last plain immediate pushed - the ctor's id argument
        int found = 0;

        for (Address a = toAddr(FROM); a.getOffset() <= TO; ) {
            Instruction in = listing.getInstructionAt(a);
            if (in == null) { a = a.add(1); continue; }
            a = in.getMaxAddress().add(1);

            // A block opens with the token address being loaded for hashing (MOV ECX, <ptr>) - or, for the first
            // block, pushed directly. Take whichever string operand shows up; the last one before the ctor wins.
            for (int i = 0; i < in.getNumOperands(); i++)
                for (Object o : in.getOpObjects(i))
                    if (o instanceof Scalar sc) {
                        String s = stringAt(sc.getUnsignedValue());
                        if (s != null && (s.startsWith("Can") || s.startsWith("Guild_")))
                            pendingToken = s;
                        else if (in.getMnemonicString().equals("PUSH") && sc.getUnsignedValue() != 0x64)
                            pendingBit = sc.getUnsignedValue();
                    }

            if (in.getFlows().length == 1 && in.getFlows()[0].getOffset() == PERMISSION_CTOR && pendingToken != null) {
                w.printf("  0x%03X  %s%n", pendingBit, pendingToken);
                found++;
                pendingToken = null;
            }
        }

        w.printf("%n  %d permission bit(s). ⚠️ 0x020 is absent: the shipped masks set it, so it is a right this%n", found);
        w.println("  window does not render - as is everything above 0x800 inside the leader's 0x7FFFFFFF.");
        w.close();
        println("wrote " + OUT + "guild_rank_permissions.txt");
    }

    // The ASCII string at a VA, whether or not Ghidra defined data there - the token operands are bare pointers
    // into a string blob and most have no Data object.
    String stringAt(long va) {
        Address a = toAddr(va);
        if (!currentProgram.getMemory().contains(a)) return null;
        StringBuilder sb = new StringBuilder();
        try {
            for (int i = 0; i < 64; i++) {
                byte b = currentProgram.getMemory().getByte(a.add(i));
                if (b == 0) break;
                if (b < 0x20 || b > 0x7e) return null;
                sb.append((char) b);
            }
        } catch (Exception e) { return null; }
        return sb.length() >= 3 ? sb.toString() : null;
    }
}
