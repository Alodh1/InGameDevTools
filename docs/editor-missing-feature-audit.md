# Editor Missing Feature Audit

Status: living backlog. Last updated: 2026-06-11.

Progress: 23/29 completed.

Strict standard: any vanilla, API-supported, or confirmed mod-authored field/workflow that can be expressed in JSON but is not first-class editable in its editor counts as missing. Raw JSON preservation is valuable, but it does not close a first-class editor gap.

Corpus note: this file was recreated after the prior audit file was not present in the worktree. The completed items below are cross-checked against current source. Remaining findings are confirmed from current editor behavior and code evidence; a fresh full crawl of installed mod zips under `VintagestoryData\Mods` is still a validation task.

## Completed From This Audit

| Editor | Done feature | Evidence source | Notes |
| --- | --- | --- | --- |
| Recipe | New or empty grid recipes no longer crash the visual grid preview. | `source/InGameDevTools/Animations/RecipeEditor.cs` `GetGridRows` | Empty `ingredientPattern`/`pattern` now receives a default row before sizing. |
| Recipe | Shapeless flag, wildcard stack names, `allowedVariants`, `skipVariants`, stack attributes, recipe attributes, tool consumption, liquid ratios, returned/cooked stacks, and cooking `validStacks` have structured controls. | `source/InGameDevTools/Animations/RecipeEditor.cs` `DrawRecipeInspector`, `EditStackFields`, `DrawCookingIngredientsEditor` | Wildcard/custom codes can be typed through the stack code combo filter. |
| Recipe | Uncommon recipe-level fields have a structured `Advanced fields` editor. | `source/InGameDevTools/Animations/RecipeEditor.cs` `DrawRecipeAdvancedFieldsEditor` | Done this pass. Existing unknown scalar/object/array fields are editable and new advanced fields can be added by type. |
| Block/Item JSON | Block/Item JSON editor is visible as a main tab and command palette target. | `source/InGameDevTools/Animations/DebugWindowManager.cs`; `source/InGameDevTools/Animations/DebugWindowManager.CommandPalette.cs` | Done this pass. The tab was present but hard-disabled. |
| Block/Item JSON | Core block/item JSON fields have structured source-edit controls. | `source/InGameDevTools/Animations/BlockItemJsonEditor.cs` `DrawBlockItemJsonStructuredEditor` | Covers identity, variants, byType, creative inventory, shape, textures, draw/render fields, behaviors, drops, attributes, and common gameplay props. |
| Block/Item JSON | Runtime/source scope is visible per structured field group. | `source/InGameDevTools/Animations/BlockItemJsonEditor.cs` `DrawBlockItemJsonScopeLabel` | Source-save-only fields are clearly separated from live attributes plus source save fields. |
| Loot/Drops | Trade table item `attributes` are editable from the structured trade item editor. | `source/InGameDevTools/Animations/LootDropEditor.cs` `DrawTradeItemAttributesEditor` | Done this pass. Includes add, apply, format, remove, and malformed JSON status. |
| Loot/Drops | Trade table item advanced fields are editable without using the full trade JSON buffer. | `source/InGameDevTools/Animations/LootDropEditor.cs` `DrawTradeItemAdvancedFieldsEditor` | Done this pass. Unknown scalar/object/array fields can be edited and new advanced trade fields can be added by type. |
| Loot/Drops | Weighted drop groups and condition/flow markers are visualized. | `source/InGameDevTools/Animations/LootDropEditor.cs` `DrawLootDropGroupingPanel` | Shows consecutive weighted groups, per-row pick odds, tool/stat gates, attributes, and last-drop stop behavior. |
| Worldgen | Advanced row fields are first-class editable for supported worldgen rows instead of only preserved in raw JSON. | `source/InGameDevTools/Animations/WorldgenEditor.cs` `Advanced fields` | Handles unhandled scalar, JSON object/array, and NatFloat-shaped row fields. |
| Entity AI | `aitasksByType` is first-class editable. | `source/InGameDevTools/Animations/AiBehaviorEditor.cs` `Typed tasks (aitasksByType)` | Includes typed task groups, add/remove/list/edit, and validation. |
| Entity AI | Unknown AI task parameters are editable without dropping to full raw JSON. | `source/InGameDevTools/Animations/AiBehaviorEditor.cs` `Other parameters` | Covers scalar and JSON object/array properties plus add/remove. |
| Particles | Nested `SecondaryParticles` and `DeathParticles` are structurally editable. | `source/InGameDevTools/Utils/ParticleEffectsManager.cs` `ChildParticlesEditor` | Includes add, clone, reorder, remove, and recursive editing. |
| Particles | Preview pause, loop, emit rate, intensity, and speed controls are present. | `source/InGameDevTools/Utils/ParticleEffectsManager.cs` `DrawPreviewPanel` | Preview delta is scaled and paused through `ParticlePreviewTiming`. |
| Particles | `VelocityEvolve` per-axis controls are editable. | `source/InGameDevTools/Utils/ParticleEffectsManager.cs` `VelocityEditor` | Done this pass. Runtime world spawn still strips this field for VS 1.22 client stability. |
| Particles | `VelocityEvolve` JSON export is an explicit user choice. | `source/InGameDevTools/Utils/ParticleEffectsManager.cs` `JsonOutput` | Copied/applied particle JSON can intentionally include or omit `VelocityEvolve`, with the compatibility warning shown in the UI. |
| Models | Element tree filtering is present. | `source/InGameDevTools/Animations/ModelEditor.cs` `Filter elements...` | Keeps matching ancestors visible. |
| Models | Root, element, and face extra metadata are editable. | `source/InGameDevTools/Animations/ModelEditor.cs` `DrawModelExtraMetadataEditor` | Rare model JSON fields now round-trip through explicit metadata editors instead of being serialization-only. |
| Animations | Animation keyframe and element extra metadata are editable and preserved. | `source/InGameDevTools/Animations/VanillaAnimationEditor.cs`; `source/InGameDevTools/Animations/VanillaAnimationEditor.HistoryExport.cs` | Source token extras participate in export, dirty checks, and undo/redo. |
| Transforms | Custom transform attribute and typed-map aliases can be targeted. | `source/InGameDevTools/Animations/TransformsEditor.cs` custom slot modes | Adds custom attribute and custom typed map context selectors for source-level aliases outside the built-in slots. |
| Patches | JSON Patch operation coverage and vanilla condition building are structured. | `source/InGameDevTools/Animations/PatchCreatorEditor.cs` operation controls and `DrawPatchCreatorConditionBuilder` | Covers add, replace, remove, copy, move, test, JsonPatchesLib extras, selected/from path picking, and vanilla `condition.when` with `useValue`/`isValue`. |
| ConfigLib | Complex object/array setting mapping is supported. | `source/InGameDevTools/Animations/ConfigLibGeneratorEditor.cs` `VisitConfigLibToken`, `DrawConfigLibSettingAuthoringControls` | Object and array defaults can be selected, typed as schema object/array, and manually overridden. |
| Settings | Animation/IK editor settings are exposed centrally. | `source/InGameDevTools/Animations/SettingsEditor.cs` `Animation##settings-animation` | Covers IK mode, drag-axis lock, dragged-part rotation preservation, and clearing saved IK anchors. |

## Remaining Backlog

### Recipe

| Priority | Missing feature | Evidence source | Current behavior | Recommended fix |
| --- | --- | --- | --- | --- |
| P1 | Recipe kind-specific validation and affordances for all vanilla/custom processors. | `RecipeEditor.cs` kind switch and flow editor | Grid, pattern, cooking, barrel/alloy-like flows are covered; custom processors can only be safely completed via Raw JSON. | Add processor-aware panels for confirmed recipe schemas found in vanilla and installed mods. |

### Block/Item JSON

No confirmed remaining strict gaps from this pass.

### Loot/Drops

No confirmed remaining strict gaps from this pass.

### Worldgen

| Priority | Missing feature | Evidence source | Current behavior | Recommended fix |
| --- | --- | --- | --- | --- |
| P1 | Full semantic editors for every advanced worldgen field. | `WorldgenEditor.cs` advanced row field renderer | Unknown fields are editable generically, not always with domain-specific controls. | Promote frequently occurring advanced fields to typed controls with validation and preview integration. |
| P2 | Complete exact preview parity for every generation pass. | `WorldgenEditor.cs` preview status strings | Some previews are approximate or depend on available server API/peek state. | Track exact-vs-approximate preview state per asset kind and add exact samplers where engine hooks are available. |

### Entity AI

| Priority | Missing feature | Evidence source | Current behavior | Recommended fix |
| --- | --- | --- | --- | --- |
| P2 | First-class typed controls for every behavior/task-specific parameter. | `AiBehaviorEditor.cs` task specs plus generic other params | Unknown parameters are editable generically. | Add parameter specs for high-frequency AI task types discovered in vanilla and installed mods. |

### Particles

| Priority | Missing feature | Evidence source | Current behavior | Recommended fix |
| --- | --- | --- | --- | --- |
| P2 | Structured editors for every particle provider edge field. | `ParticleEffectsManager.cs` `ParticleEditor.Draw` | Common fields, nested particles, and velocity evolve are structured; rare provider fields may still require raw asset editing. | Add an advanced field section from serialized token diffs, excluding known unsafe generated fields. |

### Models

No confirmed remaining strict gaps from this pass.

### Animations

No confirmed remaining strict gaps from this pass.

### Transforms

No confirmed remaining strict gaps from this pass.

### Patches

No confirmed remaining strict gaps from this pass.

### ConfigLib

No confirmed remaining strict gaps from this pass.

### Settings

| Priority | Missing feature | Evidence source | Current behavior | Recommended fix |
| --- | --- | --- | --- | --- |
| P3 | No confirmed missing user-facing setting from this pass. | `SettingsEditor.cs` | Animation/IK settings are now exposed. | Re-audit after more editor-specific preferences are added. |

## Raw-Only And Ambiguous Notes

- Mod-specific fields that are merely preserved in Raw JSON are not automatically listed as confirmed gaps until their schema and workflow are verified.
- Parse failures from malformed installed assets should be tracked separately from missing editor support.
- `VelocityEvolve` is a compatibility edge case: it is editor-visible and export-selectable, but runtime world spawn still strips it to avoid the known VS 1.22 client crash path.
- Particle JSON can now be loaded, formatted, edited, applied, and copied inside the particle editor. This is useful for rare provider fields, but it is not counted as closing the strict typed-control gap.
- Recipe validation now covers known vanilla recipe kinds. Custom processor schemas still need confirmation before the recipe validation backlog item can be closed.
