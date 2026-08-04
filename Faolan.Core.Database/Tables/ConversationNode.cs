using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace Faolan.Core.Database.Tables
{
	// One NPC-conversation SCREEN, identified by WHAT IT SAYS: the ordered list of LDB string references its
	// remoteformat screens are built from (ContentKey), scoped to the NPC saying them. Holds the response packets to
	// send (ordered), fully structured since #188. Imported from captures by Faolan.Importer; served by
	// ConversationService. Scoped by the NPC's TEMPLATE id, not by the session-volatile handle and not by its
	// display name.
	//
	// A node is a VERTEX of the NPC's dialogue graph: `ConversationEntry` names the roots (what opens the
	// conversation) and `ConversationEdge` the branches (which option leads where, under which quest gate). It carries
	// no position of its own - the same screen reached two ways is one row, and a menu that loops back is an edge
	// rather than an infinite unrolling.
	//
	// ⚠️ It used to be keyed by the answer-index PATH the player walked to reach it ("", "0", "0>1", …), which was
	// chosen before we could read the dialogue's own identifiers off the wire (#642). That key was provably lossy
	// against the shipped data: it could not represent a loop (three exist - Bhaltair, Guard Captain Marcus,
	// Hadrathus - and going round one twice CLOSED THE WINDOW on the player, because the next lap had never been
	// captured), it duplicated every convergent screen once per route, and an answer INDEX means different things in
	// different variants of the same screen. The option's own LDB string id is the durable branch name, so the index
	// never needed to be persisted at all.
	//
	// The PK is DERIVED from that identity (ComputeId), never store-generated. The importer delete+reinserts this
	// whole table on every re-harvest, so an AUTOINCREMENT surrogate renumbered every node - and, through the
	// packet/screen/parameter children, ~17k rows - on every run, churning faolan.sql (the reviewable data mirror,
	// #411) for zero semantic change. A derived id is byte-stable across re-imports instead, and it ENFORCES the
	// identity: two nodes agreeing on every component are the same node and collide on the PK.
	[Index(nameof(NpcTemplateId))]
	public class ConversationNode : IDerivedKey
	{
		[Key]
		[DatabaseGenerated(DatabaseGeneratedOption.None)]
		public uint Id { get; set; }

		// WHAT THIS SCREEN SAYS, as the ordered `category:id` list of the LDB string references its remoteformat
		// screens carry - the NPC's spoken line (category 50002), then each option button (50001; `0` is the universal
		// "Goodbye"), then the close screen (category 100, id 0) where there is one. In the shipped data the
		// `(id, category)` pair determines the decryption key 1:1, so this IS a content-derived name for the screen.
		//
		// Denormalised from the child ConversationScreen rows on purpose: it is the node's IDENTITY, and a derived key
		// has to be computable from the row's own columns (DatabaseConventionTests recomputes it without loading the
		// tree). ConversationGraphTests gates it against the children it was derived from.
		[MaxLength(512)]
		public string ContentKey { get; set; } = null!;

		// 0 for all but a handful of nodes. Retail reuses the same line + option strings at two different script states
		// and the follow-ups differ, so the content alone does not separate them: Tina's `2683`, Tina's `12413`,
		// Laranga's `19827` and Turach's `2822` each need two rows. Assigned deterministically by the importer's fold
		// (ConversationGraph), ordered by the walk key of each class, so it is a pure function of the corpus rather
		// than of iteration order.
		public uint Discriminator { get; set; }

		// WHO SAYS IT: the NPC's TYPE id (NpcStats.template_id from the same spawn), and the screen's other identity
		// half (#646).
		//
		// ⚠️ It used to be a nullable GATE beside a name KEY, which was backwards. Names are ambiguous in the world -
		// "Halen van Dummy" is the internal name of 27 templates (the quest-giver Atraxus 12236 plus children and
		// dummies), "Conarch Village Guard" and "Smuggler" of 5 each - and this column's own comment used to say so
		// while still keying on the name and gating on the template. That worked only because the corpus happens to
		// hold one dialogue per name; a second Smuggler with its own tree could not be told apart by the key, and the
		// gate that had to do it was nullable. `Shop` already keys by template and is the precedent.
		//
		// NOT NULL, therefore: a screen we cannot attribute to a template is one the runtime could never route a click
		// to, so the importer drops it with a report line rather than storing an unreachable row.
		public uint NpcTemplateId { get; set; }

		[ForeignKey(nameof(NpcTemplateId))]
		public virtual NpcTemplate NpcTemplate { get; set; } = null!;

		// Hand-reviewed: the importer must not touch this row. Set from the editor, NEVER by an import.
		//
		// The importer harvests the bulk from captures and gets most of it right; the tail is corrected by hand, and
		// without this flag the next `--only conversations` would undo that work - upsert replaces a row whenever a
		// capture has more packets, and overwrite drops the table outright. `--force` overrides it, for the case where
		// the correction itself has gone stale.
		//
		// ⚠️ NOT part of ComputeId, deliberately: the id is the node's IDENTITY (who says it, and what it says) and
		// marking a row reviewed does not change any of that. Folding it in would give a verified row a different key
		// from the one the harvest computes, so the importer would stop seeing it as the same node and insert a
		// duplicate beside it - the exact opposite of protecting it.
		public bool Verified { get; set; }

		public virtual ICollection<ConversationPacket> Packets { get; set; } = [];

		// The branches OUT of this screen, and the roots that open on it.
		public virtual ICollection<ConversationEdge> OutgoingEdges { get; set; } = [];

		// The NPC's DISPLAY NAME, resolved at import from the captured FA015807 spawn with this handle. A LABEL, not an
		// identity (#646): it is what a log line, a report and the travel refusal text read, and nothing routes on it.
		// Empty when the capture never named the NPC - which is now merely a missing label rather than the "#<handle>"
		// stand-in identity it used to have to be.
		[MaxLength(128)]
		public string NpcName { get; set; } = null!;

		public static uint ComputeId(uint npcTemplateId, string contentKey, uint discriminator)
		{
			return DerivedKey.From(npcTemplateId, contentKey, discriminator);
		}

		public uint ComputeId()
		{
			return ComputeId(NpcTemplateId, ContentKey, Discriminator);
		}

		public override string ToString()
		{
			return $"{Id} [{nameof(ConversationNode)}] {NpcName} ({NpcTemplateId}) {ContentKey}";
		}
	}
}
