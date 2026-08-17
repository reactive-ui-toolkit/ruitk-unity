---
name: uibuilder-capabilities
description: Keep plans/UI_BUILDER_CAPABILITIES.md in sync. Invoke whenever work adds, changes, or removes a RUITK Builder capability — anything under Builder/ that a user could notice.
---

# Builder capability record

`plans/UI_BUILDER_CAPABILITIES.md` is the portable description of what the
RUITK Builder does. It exists so the builder can be rebuilt for another
engine's toolkit from BEHAVIOUR rather than by reading the Unity source.

## The rule

**Any change under `Builder/` that a user could notice must update
`plans/UI_BUILDER_CAPABILITIES.md` in the same commit.** That includes:

- a new gesture, menu item, shortcut, pane, or affordance
- a changed default, range, or interaction rule (zoom limits, what Delete acts
  on, what Escape cancels, what Save writes)
- a removed or renamed capability
- a change to the disk contract, the undo model, or the diagnostics tiers

A pure bug fix that restores documented behaviour does NOT need an entry — the
document already claims it works. But if the fix changes the RULE (what a
gesture does, when something is confirmed, what is reversible), it does.

## How to write an entry

- Describe **behaviour the user sees**, never the implementation. "Deleting a
  module marks it pending; Save performs it" — not "MarkForDeletion adds to
  `_pendingDeletes`".
- Put it in the section it belongs to; add a section only for a genuinely new
  surface.
- Keep the "Known non-capabilities" list honest. A port team is misled more by
  a missing limitation than by a missing feature.
- No UB-## ids and no defect history here — those belong in
  `Plans~/UI_BUILDER_BUGS.md`. This file is the current-state contract; that
  file is the record of what broke and why.

## Relationship to the other trackers

| File | Holds |
|---|---|
| `plans/UI_BUILDER_CAPABILITIES.md` | what the builder does, now (this skill) |
| `Plans~/UI_BUILDER_BUGS.md` | defects, root causes, field reports, UB-## ids |
| `Plans~/VISUAL_EDITOR_PLAN.md` | what is still to be built |

## Check before finishing

After a builder change, ask: could the owner demo this? If yes, is it in the
capability file? If a capability was removed, is the line gone rather than left
claiming something untrue?
