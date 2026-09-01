using System.Collections.Generic;
using Faolan.Core.Enums;

namespace Faolan.GameServer
{
	// Every verb in the retail client's command registry that this server does not BACK with an implementation yet
	// (#914). They are still registered, with retail's own mask and retail's own recovered usage line, and answering
	// one prints what it would take to make it work.
	//
	// ⭐ Why register a command we cannot perform. The client's hidden GM affordances only ever emit verbs from its
	// own registry, and an unregistered verb comes back as "no such command" - which reads as "this build is broken"
	// rather than "that subsystem does not exist here". A GM who presses one of these gets the grammar, the
	// privilege it needs, and the reason, which is the difference between a dead end and a pointer. It also means
	// `.help` lists the WHOLE client surface, so nothing is discoverable only by reading a TSV.
	//
	// ⚠️ These are NOT stubs that pretend to work: nothing is toggled, nothing is written, and the reply says so.
	// A verb graduates OUT of this table the moment it gets a real handler - the table and the handler table are
	// joined at startup and a verb in both is a build failure, so it cannot be silently half-implemented.
	//
	// The usage lines are recovered, not invented - docs/re/gm-command-args.tsv, produced by
	// tools/ghidra/DumpCommandArgs.java. An empty usage means the class declares no parameters.
	//
	// ⛔ SOME OF THESE STRINGS LOOK WRONG AND ARE RETAIL'S OWN. Do not "correct" them - they were re-derived from
	// AgeOfConan.exe directly (not from the TSV, which shares the dumper's blind spot), and the client shows the
	// same text. Each was settled by counting how many code sites PUSH the string: one class's constructor pushes
	// its shortdesc and longdesc ~0x33 apart, so a string pushed at two sites is used by two different classes.
	//
	//   * Three verbs carry a description copy-pasted from a DIFFERENT command, because Funcom cloned the class:
	//       jumpspeed     <- jumpheight's ("Command for overriding the jump height.", param "height")
	//       gametweaks    <- pet's        ("Commands for pet control.")
	//       consumememory <- grantress's  ("Gives the target a temporary ress point.")
	//     Each pair is two DISTINCT classes (FanaticJumpSpeedCommand_c vs FanaticJumpHeightCommand_c, ...), and the
	//     shared string has one push site per class. There is no "jump speed" description anywhere in the binary.
	//     ⚠️ Contrast the verbs that share a description because they ARE one class under several names -
	//     tp/teleport/teleportself/teleporttarget, set/setlocal, reload/lazyreload - one vftable, ONE push site.
	//
	//   * Eight usage lines have unbalanced brackets. That is retail's grammar, not a truncated recovery: the
	//     append stream for each is a clean multiple of four, and the count of append() call sites in the class
	//     equals the count recovered (spawn 44/44, token 27/27). Two of them are a Funcom TYPO - the closing
	//     bracket is inside the HELP string, which is NUL-terminated right after it:
	//       "Literal: Clears 1 death effect]"      -> /respawn   renders `clear[`
	//       "Integer: Normalset id to change to]"  -> /normalset renders `change[normalset`
	//     The others (pet, space, token, spawn, dataset, resurrectionpoint) simply never append a closer.
	//     ⛔ So do NOT add a "usage brackets balance" test - it would fail on a FAITHFUL recovery. The gate that
	//     belongs here is the transcription one: CataloguedVerbs_MatchTheRecoveredGrammar.
	//
	//   * /lua's Description is its LONGdesc: FanaticLuaCommand declares no shortdesc at all. It is the only entry
	//     where that fallback applies, and the gate encodes it rather than leaving it as a one-off.
	internal static class RetailCommandCatalog
	{
		internal sealed record Unbacked(string Verb, GmPrivileges Mask, string Usage, string Description, string Needs);

		internal static readonly IReadOnlyList<Unbacked> Verbs =
		[
			new("addbugcc", GmPrivileges.Developer, "", "Add a username to be CCed on the next server crash.", "a crash-report CC list; this server has no crash reporter"),
			new("ai", GmPrivileges.Developer, "loadbrain brainid | disablebrain | disableall | resetpriorities | setemotions | resetemotions | resethatelist | addhate", "Manipulates NPC brains", "an NPC brain model - we have no loadable brains (#912/#803 track BCC behaviour)"),
			new("andersdebug", GmPrivileges.Developer, "", "just for anders", "Funcom's own build scaffolding - its long description is \"if this goes live shoot me\""),
			new("banish", GmPrivileges.Developer, "", "", "the mount subsystem to expose a dismiss-and-store path"),
			new("bccaction", GmPrivileges.Developer, "eventname | -c", "Makes your target execute a BCC event", "a BCC event executor on the target (#912)"),
			new("bccself", GmPrivileges.Developer, "eventname", "Executes an event", "a BCC event executor (#912)"),
			new("brtest", GmPrivileges.Developer, "", "Test for bug report memory leak", "Funcom's own bug-report leak test - not a game mechanic"),
			new("cars", GmPrivileges.Developer, "", "Cars Debug command", "Funcom's own debug command; no corresponding subsystem here"),
			new("chaincast", GmPrivileges.Developer, "", "test for chaincast", "chain-cast spells, which the cast pipeline does not model"),
			new("clearcooldown", GmPrivileges.Developer, "target", "Clears cooldowns on a target/self", "server-side cast cooldowns - nothing gates a cast yet (#271)"),
			new("cloak", GmPrivileges.CanCloak, "on | off |", "Turns on/off cloaking", "visual cloaking: hiding an entity from other clients\u0027 interest streams, which the broadcaster has no path for"),
			new("combo", GmPrivileges.Developer, "run <id> [fatality] targetrun <id> [fatality] enablerunning disablerunning", "Combo runner", "a combo runner that can execute a combo by id on demand (#623)"),
			new("commandqueue", GmPrivileges.Developer, "list | listalloc | setinfatality | clearfatality | listblockers | clear", "Commandqueue debug", "the client-side command queue, which is a client structure we do not mirror"),
			new("consumememory", GmPrivileges.Developer, "this", "Gives the target a temporary ress point.", "a server memory-pressure test; deliberately not built"),
			new("criteriatester", GmPrivileges.Developer, "operator stat value", "Runs a single criteria and returns the result", "a criterion evaluator - nothing evaluates a criterion run yet (#791)"),
			new("datamining", GmPrivileges.Developer, "status | status <type> | status all | count | debug <on|off> | reload | set global <number> | set <type> <number> |", "Manages the datamining settings for this server.", "the data-mining pipeline retail shipped; we have no equivalent"),
			new("dataset", GmPrivileges.Developer, "client|server|[setdataset]", "Give info about datasets", "retail\u0027s dataset concept, which our content pipeline does not have"),
			new("dbdebug", GmPrivileges.Developer, "info | set debug | level debuglevel", "Get information about or change debug levels", "runtime log-level control, which our logging does not expose to chat"),
			new("disablenpcautoreset", GmPrivileges.Developer, "on | off |", "Disables autoresetting the stuck/erroneous NPCs", "an NPC stuck/auto-reset watchdog, which the simulation does not have"),
			new("fakeevent", GmPrivileges.Developer, "event [t target] sender trigger", "Fake an event", "the event bus retail fakes into; ours has no injection point"),
			new("formation", GmPrivileges.Developer, "help | create formationname | enter | leave | select | changetofreemode | changetofollowmode | disband | reload | mountselected |", "Formation command", "formations - no formation model exists (#908)"),
			new("fraglimit", GmPrivileges.Developer, "", "Fraglimit debugging command", "minigame frag limits (#146)"),
			new("gametweaks", GmPrivileges.Developer, "set [name value] | get [name] | reload | list", "Commands for pet control.", "the tuning sheet (RDB 1000516) exposed as live settings"),
			new("gasubscribe", GmPrivileges.CanCloak, "off | on id | name", "Subscribes to all game action run on target", "a per-target GameAction trace, which the dispatcher does not emit"),
			new("getscriptgroup", GmPrivileges.GetStats, "", "Gets the scriptgroup currently rinnong on the target", "script groups on a live NPC (#356 - our Lua VM was removed)"),
			new("grantress", GmPrivileges.TeleportDynels, "this", "Gives the target a temporary ress point.", "temporary resurrection points (#519)"),
			new("hugomtest", GmPrivileges.Developer, "id", "Hugo's test command", "Funcom's own test command - not a game mechanic"),
			new("jumpheight", GmPrivileges.SetStats, "height", "Command for overriding the jump height.", "a jump-height override the client honours"),
			new("jumpspeed", GmPrivileges.Developer, "height", "Command for overriding the jump height.", "a jump-speed override the client honours"),
			new("lazyreload", GmPrivileges.Developer, "all | npcs | items | quests | character_template | questtables | questactionselection | questtext | questtextmastertable | questtextlist |", "Reload some or all of the tables", "live content reload (the deferred form)"),
			new("localcache", GmPrivileges.Developer, "[update | toggle]", "Controls for the local cache", "the client-side local cache, which is a client structure"),
			new("locktemplate", GmPrivileges.Developer, "", "Lock a template", "template locking (#764) - and its class declares no parameters, so the argument form needs decompiling"),
			new("lua", GmPrivileges.Developer, "", "Run Lua script on both client and server.", "a Lua VM - ours was removed (#356)"),
			new("luas", GmPrivileges.Developer, "", "Run server side lua script.", "a server-side Lua VM - ours was removed (#356)"),
			new("massive", GmPrivileges.Developer, "cityname  [ window time ] | [objowner guildid ]  | [savearea ] | [savescore ] | [resetscore ] | [clearbroken ] | [info ] | [structures ]", "Command for manipulating player cities.", "player cities and Battlekeeps (#146)"),
			new("MaterialInfo", GmPrivileges.Developer, "", "Gives ground material information.", "ground material data, which the terrain service does not carry"),
			new("memoryprofile", GmPrivileges.Developer, "", "Enable/disable server memory profiling.", "server memory profiling; deliberately not built"),
			new("mentor", GmPrivileges.Developer, "[force | load | break | status | sync | askme]", "Mentor/pupil commands", "the mentor half of the apprentice system (#144)"),
			new("missiontimeout", GmPrivileges.Developer, "minutes", "Sets the  stats::e_QuestTimeout  stat", "the stats::e_QuestTimeout stat wired to quest expiry"),
			new("morphvalues", GmPrivileges.Developer, "[client] list | apply | save | clear [pattern] | getallmorphnames | get name | set name", "All morphvalues applied.", "live morph manipulation (#672/#766)"),
			new("mousetarget", GmPrivileges.Developer, "on | off", "Enables or disables mouse targeting", "a client input mode, which is client-local state"),
			new("move", GmPrivileges.Developer, "charge  [me] | flee | follow [me] [min [pref [max]]]  [me] mindist maxdist |formation | panic | patrol [patrolname] | roam | to x y z | rotate x y z |", "Make target execute a server controlled move.", "server-controlled move orders (charge/flee/follow/patrol/roam)"),
			new("net", GmPrivileges.Developer, "flood", "Networking tests", "a network flood test; deliberately not built"),
			new("normalset", GmPrivileges.Developer, "change[normalset", "Attempts to set the normalset on an npc", "normalsets on an NPC, which the spawn model does not carry"),
			new("npc", GmPrivileges.NpcCommands, "distance | knubot | setknubot | loadknubot | reloadknubot | openknubot", "Knubot commands", "the Knubot dialogue debug surface"),
			new("npcsleep", GmPrivileges.Developer, "disablesleeping | enablesleeping | putalltosleep | wakeallup | puttargettosleep | wakeuplastsleeper", "Manipulates NPC sleeping", "NPC sleeping, which the simulation does not model"),
			new("observer", GmPrivileges.Developer, "maxobserverdistance maxcharactercapacity who", "Change the max observer distance and character capacity", "observer distance/capacity limits, which interest streaming does not expose"),
			new("openurl", GmPrivileges.Developer, "", "ITEM_SHOP_BROWSER", "a CLIENT-LOCAL browser command - it never reaches a server"),
			new("pet", GmPrivileges.Developer, "spawn [hash level assistbrain followbrain defendbrain attacktagbrain] | capture [assistbrain followbrain defendbrain", "Commands for pet control.", "pets with retail\u0027s four brains (#307/#823)"),
			new("pupil", GmPrivileges.Developer, "[force | load | status | askme]", "Pupil commands", "the pupil half of the apprentice system (#144)"),
			new("pvplevel", GmPrivileges.SetStats, "pvp level", "Levels your character to the desired PvP level", "a PvP level, which progression does not model"),
			new("questmarkeron", GmPrivileges.Developer, "", "Set Quest marker.", "forced quest markers (#559/#857/#848)"),
			new("RaidFinderRemove", GmPrivileges.Developer, "id | name", "Removes a character from the Raid Finder", "the Raid Finder roster (#669)"),
			new("reload", GmPrivileges.Developer, "all | npcs | items | quests | character_template | questtables | questactionselection | questtext | questtextmastertable | questtextlist |", "Reload some or all of the tables", "live content reload from the database"),
			new("reportcharoffline", GmPrivileges.TeleportDynels, "this", "Reports character as offline forcefully.", "the login-hang workaround retail needed; we have no such hang"),
			new("resetacg", GmPrivileges.TeleportDynels, "this", "Resets the ACG the character is currently in.  Notice, this will give the character a notification and transport him out.", "ACG instances (#899)"),
			new("resetplayfield", GmPrivileges.Developer, "", "Shuts the current playfield down and teleports all characters out of it. New Destination may be provided as arguments.", "playfield shutdown and mass teleport-out"),
			new("respawn", GmPrivileges.NpcCommands, "clear[", "Respawns your target", "respawning a target with death effects"),
			new("resurrectionpoint", GmPrivileges.Developer, "create | {delete all | id}|list | show | teleport id | closest discover} | forget}", "Manages resurrection points on the current playfield", "resurrection-point management (#519)"),
			new("ruictest", GmPrivileges.Developer, "id", "Rui's test command", "Funcom's own test command - not a game mechanic"),
			new("scriptlist", GmPrivileges.Developer, "help", "Command for starting tracing scripts.", "script tracing (#356)"),
			new("setcontrolmode", GmPrivileges.Developer, "controlmode", "Sets the controlmode of your target", "control modes on a target, which the movement model does not carry"),
			new("setlookspackage", GmPrivileges.Developer, "", "Sets Looks Package on the target.", "looks packages by RDB id (#766)"),
			new("setname", GmPrivileges.ChangeName, "firstname [name] | lastname [name] | nick name | title name", "", "live first/last/nick/title changes"),
			new("setnick", GmPrivileges.ChangeNick, "firstname [name] | lastname [name] | nick name | title name", "", "live first/last/nick/title changes"),
			new("setregionscript", GmPrivileges.Developer, "scriptid", "Sets the scriptgroup on the pf", "region scripts (#356)"),
			new("setregionvar", GmPrivileges.Developer, "ID Variable-name Variable-value", "Sets a region script variable", "region script variables (#356)"),
			new("setscriptgroup", GmPrivileges.SetStats, "scriptgroupid scriptgroupparams", "Sets the scriptgroup on the target", "script groups (#356)"),
			new("showerrors", GmPrivileges.Developer, "id", "Sets the displaying of visual debug output", "client-side visual debug output"),
			new("simpledynel", GmPrivileges.Developer, "create createdestructible initdestructible inflict", "Simple dynel command", "simple dynels - destructible props the world model does not carry"),
			new("simpledyneldebug", GmPrivileges.Developer, "hide | disarm | disarmfail | lockpick | lockpickfail | canuse state | canuse state", "debug commands on simpledynel", "simple dynels - destructible props the world model does not carry"),
			new("simulatenpc", GmPrivileges.Developer, "reset | hash", "Change the targets look into the hash.", "changing a target\u0027s look to an arbitrary hash"),
			new("softreset", GmPrivileges.TeleportDynels, "", "Teleports player and his raid out of the playfield and does a shutdown to do a soft reset of that instance.", "playfield shutdown and mass teleport-out"),
			new("space", GmPrivileges.Developer, "space [splitmerge|mincellsize|info|show|dump layer split_level merge_level length", "Command to configure dynamic space", "the dynamic-space partitioning retail used; our zones are flat"),
			new("spawn", GmPrivileges.AcgSpawn, "minlvl [- maxlvl] [s seed] [m | t] [p x y", "Spawns an object", "spawning an object by HASH with level/seed/position (#899)"),
			new("spawnacgoutdoorentrance", GmPrivileges.Developer, "style playfield seed minlevel maxlevel", "Spawns an entrance to a ACG outfoor playfield", "ACG outdoor playfields (#899)"),
			new("spawnpoints", GmPrivileges.Developer, "clear | respawn | overridetimer | time", "Clear or respawn the guru spawnpoints of the current playfield", "guru spawn points (#363)"),
			new("spawnquest", GmPrivileges.AcgSpawn, "HASH", "Spawns a Quest", "spawning a quest by HASH"),
			new("substate", GmPrivileges.Developer, "list enter exit clear", "Bcc Substate command", "BCC substates by state id (#803)"),
			new("syncdisplay", GmPrivileges.Developer, "", "Toggles SyncDisplay for your target", "server/client position debug lines"),
			new("tacticalmap", GmPrivileges.Developer, "", "Debug the tactical map", "the tactical map, which we do not model"),
			new("teleportcity", GmPrivileges.Developer, "", "Teleport to group controlled city", "group-controlled cities (#146)"),
			new("time", GmPrivileges.Developer, "* factor | / factor | fps factor | reset | step deltatime |", "Manipulates frametimes", "FRAME-time manipulation (the deltatimer system) - deliberately not built; the time-of-day setter is .settime"),
			new("togglespellrecharge", GmPrivileges.Developer, "", "Toggle spell recharge", "server-side cast cooldowns - nothing gates a cast yet (#271)"),
			new("token", GmPrivileges.SetStats, "list | get [token] | change [token", "List, get, change or reimburse tokens.", "tokens (#899)"),
			new("tracescripts", GmPrivileges.Developer, "help", "Usage: /tracescripts [help] [client/both] [stop] [group=x] [script=x] [maxcmds=x] [maxthreads=x] [follow] [anypf] [notarget] [_target_]\nAny param is optitional. Order of params is not important. Server is default. [_target_] is you whatever you've targeted. Don't use spaces before/after = . Default 100 cmds per thread. 10 threads.", "script tracing (#356)"),
		];
	}
}
