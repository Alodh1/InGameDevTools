# Changelog

## Unreleased

- Fixed model-editor move, rotate, and resize gizmos so the solid preview updates throughout a drag again without rebuilding generator previews on every edit frame.

- Reduced DevTools memory spikes and editor-frame allocation by making particle discovery and JSON indexes lazy, gating recovery/texture encoding, reusing preview buffers, bounding histories, and releasing cached/runtime resources on close and disposal.

- Added an optional MeshLib model-editor mode with validated triangle/quad `noncuboid` authoring, component/topology/UV tools, cuboid conversion, true-mesh primitives, and reflective preview/live-apply integration.

- Made the Creature, PlayerModel, Clothing, and Tool / Weapon generators emit editable semantic MeshLib surfaces when MeshLib editor mode is active, while preserving their original Vanilla cuboid output.

- Replaced the MeshLib extrude, inset, and subdivide toolbar actions with direct viewport gizmos and wire previews.

- Added a model-editor Tool / Weapon generator: a parameter-driven held-item builder that assembles a grip (with cord wraps, taper and langets), a guard (crossguard/quillons/disc-tsuba/basket/knuckle), a pommel (ball/wheel/cap/faceted/scent-stopper/ring), and one of eleven working heads — blade (taper, curve, fuller, midrib, ricasso, serrations, six tip styles), axe bit (beard, sweep, double-bit, spike/hammer/pick poll), hammer/maul, flanged/spiked mace, spear/polearm (leaf/triangle/diamond/needle/barbed/lance heads, prongs for a trident, wings, side axe and back hook for a halberd, or a curved naginata/glaive blade), pick, spade, hoe, scythe/sickle, bow (straight/recurve/longbow with limbs + string), and staff/club. Ships ~45 archetype presets (sword, greatsword, dagger, rapier, sabre, katana, khopesh, gladius, spear, pike, trident, naginata, halberd, axe, bardiche, warhammer, mace, morningstar, pickaxe, shovel, hoe, scythe, shortbow, ...), a seeded randomizer, multi-texture metal/handle/accent assignment, a live ghost, and a drop-in itemtype JSON export (tool, attack power/range, durability, tooltier, transforms). Parameters were mined from the base-game tools and the Armory, Vanilla Armory, Dark Souls Armory, Alpha Weapon Pack and Mitsuyo's Japanese gear shapes.
- Greatly deepened the Tool / Weapon generator's parameters, especially for blades: a real per-segment cross-section / grind (flat, single bevel/chisel, double bevel/lens, diamond/midrib, hollow) with angled bevel facets, a thick spine ridge tapering to a thin edge, separate spine vs tip thickness (distal taper), edge-thickness fraction, bevel width/angle, a false-edge/swedge spine, and a wider fuller. Added grip cross-sections (round/oval/octagon/square/ribbed) with swell/waisting and a metal collar; guard forward-offset and cup plate; pommel peen button; axe top horn, cheeks and a convex/concave edge profile; hammer face shapes (round/octagon/waffle) and cheeks; mace neck collar and flange twist; spearhead fuller and socket reinforcing rings; and bow limb taper and arrow rest.
- Added a model-editor Clothing generator: a parameter-based wearable builder covering all 15 vanilla clothescategory slots (head/face/neck/shoulder/upper+over body/arm/hand/waist/lower body/foot/emblem and the three armor slots). It step-parents fitted garment panels onto the seraph or digitigrade wearer bones (the way the game composes worn shapes), previews body + clothes together for fit, has presets (hood, coat, robe, cuirass, gauntlets, greaves, boots, cloak, ...), and exports a drop-in wearable item JSON with warmth/protection/stat modifiers.
- Greatly expanded the Clothing generator with a seeded decoration layer: edge tatters/fringe/fur-trim, organic branches (forking, with leaf clusters), vines, scattered leaves, thorns, studs/rivets, gems, spikes, feather plumes, a capelet mantle, cloth layers, armor plating (scale/lamellar/plate/brigandine grids), straps & buckles, collar styles (high/popped/ruff/cowl), hood puffs/cowl/pointed-tip/chest-drape, a third "accent" texture, plus global Wear (damage) and Asymmetry controls. New presets include a one-click "Wildwood cloak" (semi-tattered cloak with branches and leaves), Druid robe, Plated armor, Brigand, Regal mantle and Ragged shroud.
- Fixed the DevTools window closing behavior when leaving a world, and moved the animation, creature, and Prism generators into the main editor window as embedded panels.
- Fixed creature-generator scaling UVs so large generated parts stay textured, and rebuilt membrane wings as segmented torso-connected spars with per-segment membrane panels instead of detached web panels or one giant sheet.
- Fixed procedural animation previews for generated shapes so passive generated surface panels no longer double-transform, and generator slider edits can live-update the currently looping generated animation.
- Fixed shape animation export so sparse/generated keyframes complete partial XYZ transform groups before writing JSON, preventing exported animations from crashing Vintage Story's animation interpolation.
- Improved model-editor chisel mode with a clearer placed-texture picker and automatic merging of adjacent same-texture chisel cubes.
- Added a model-editor Chisel size control for placing and removing smaller-than-one-unit chisel cells.
- Added a model-editor UV randomizer that offsets all visible faces using the selected texture while preserving each face's UV size.
- Added a model-editor UV tab preview color override so dynamically tinted textures such as leaves can be inspected with a chosen color.
- Added a model-editor texture painter in the UV tab for creating, editing, and saving authored PNG textures directly from the selected texture slot.
- Improved the model-editor texture painter workflow so paint mode can clone the selected game/authored texture, save it as an authored PNG copy, and automatically point the model texture slot at the saved copy.
- Added model-editor drag import from the Shapes browser so an existing shape can be dropped into the open model as a movable grouped sub-model, with texture-code conflicts renamed automatically.
- Fixed multi-element model-editor resize so selected cuboids scale as one shape instead of pushing each element face independently.
- Fixed model-editor Cut so elements with children can be cut; children are reparented to the resulting cut piece that contains them.
- Added a ConfigLib scratch-config workflow that can generate a ConfigLib patch, ModConfig default JSON, and authored C# config loader from new settings.
- Added English localization assets and a shared localization helper, with the main DevTools shell, command palette, diagnostics, source-save popup, and core animation controls moved onto lang keys.
- Added additional DevTools localization files for German, Spanish, French, Italian, Japanese, Brazilian Portuguese, Russian, Ukrainian, Simplified Chinese, Korean, Polish, Dutch, and Turkish.
- Added a DevTools language selector in Settings and moved the Settings tab's visible controls/status text onto localization keys.
- Added non-spam safety backups: deduplicated overwrite backups for authored saves and rolling recovery snapshots for dirty editor drafts.
- Fixed model recovery snapshots so renaming a new shape no longer creates one recovery entry per partial filename; legacy partial-name model snapshots are pruned for the current document.
- Fixed transform preview helper methods that triggered AttributeRenderingLibrary stack-simulation warnings for trap and inventory transform placement.
