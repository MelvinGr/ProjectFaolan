namespace Faolan.Core.Enums
{
	/// <summary>
	///     The BehaviorControlCenterIIR's <c>behaviorId</c> - the COMMAND a behaviour record carries (#536).
	/// </summary>
	/// <remarks>
	///     ⚠️ These are the CLIENT'S OWN NAMES, not ours. The id is a key into <c>InstantiateCommand::classmap</c>
	///     (the lookup <c>FUN_007dcd60</c> does before constructing the command object that reads the record's tail),
	///     and that map is populated by two registration functions which push each command's NAME STRING next to its
	///     id: <c>FUN_007375b0</c> (22 commands - the interaction/animation set) and <c>FUN_01134510</c> (12 - the
	///     combat/movement set). So this catalog is read off the binary rather than inferred from behaviour, the same
	///     way the opcode catalogs in <see cref="Opcodes" /> are.
	///     <para>
	///         The client's string is quoted per member wherever the C# identifier had to drop punctuation
	///         (<c>Combat:Melee</c>, <c>Sneak/Hide</c>, ...). One member is registered under a CLASS name rather than a
	///         display name - <c>SwapAlternateEquipmentCommand_c</c> - and is kept verbatim.
	///     </para>
	///     <para>
	///         The names corroborate the corpus independently, which is what makes them trustworthy: over 67307
	///         captured records <see cref="UseDynel" /> carries a 12-byte tail (a class+handle reference),
	///         <see cref="UseCharacter" /> 4 (a handle), <see cref="PlayAnimation" /> a VARIABLE 10-39 (an animation
	///         name), <see cref="Emote" /> and <see cref="LimboDeath" /> none - and the three attack commands
	///         (<see cref="CombatMelee" />, <see cref="CombatRanged" />, <see cref="CombatUnarmed" />) share one tail
	///         family, which is why <c>BehaviorAcks</c> had already noticed 0x0F and 0x11 behaving alike without
	///         knowing why.
	///     </para>
	/// </remarks>
	public enum BehaviorId : uint
	{
		Transition = 0x01,
		Movement = 0x02,
		MagicAttack = 0x04, // "Magic:Attack"
		Emote = 0x05,

		/// <summary>The generic action channel, and the busiest record in the corpus (35852 of 67307, no tail).</summary>
		BccAction = 0x07,
		CombatShieldTaunt = 0x08, // "Combat:Shield Taunt"
		Equip = 0x09,
		Mount = 0x0A,
		Dance = 0x0B,
		DisarmTrap = 0x0C,
		SearchTrap = 0x0D,
		UseItem = 0x0E,

		/// <summary>The ordinary weapon swing - what this codebase used to call "NormalAttack".</summary>
		CombatMelee = 0x0F, // "Combat:Melee"
		CombatRanged = 0x10, // "Combat:Ranged"
		CombatUnarmed = 0x11, // "Combat:Unarmed"
		UseDynel = 0x12,
		CombatEvasiveManeuver = 0x15, // "Combat:Evasive Maneuver"
		UseCharacter = 0x16,
		TargetAnimation = 0x18,
		CombatSiege = 0x1B, // "Combat:Siege"
		PlayAnimation = 0x1D,
		SneakHide = 0x1E, // "Sneak/Hide"

		/// <summary>The death transition - the client's own name for it is the limbo state, not "death pose".</summary>
		LimboDeath = 0x1F, // "Limbo/Death"
		Search = 0x20,
		Climbing = 0x24,
		Revive = 0x25,
		TransitionDrunkenBrawling = 0x26, // "Transition:Drunken Brawling"
		Tradeskill = 0x27,
		CombatDrunkBrawling = 0x28, // "Combat:Drunk Brawling"
		TransitionLeaveKnockout = 0x29, // "Transition:Leave Knockout"
		ObjectInteraction = 0x2A,
		PickPocket = 0x2B,
		SwapAlternateEquipmentCommand = 0x2E, // registered as "SwapAlternateEquipmentCommand_c"
		CombatReactive = 0x2F // "Combat:Reactive"
	}
}
