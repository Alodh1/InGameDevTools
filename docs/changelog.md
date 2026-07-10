# Changelog

## Unreleased

- Reduced DevTools memory spikes and editor-frame allocation by making particle discovery and JSON indexes lazy, gating recovery/texture encoding, reusing preview buffers, bounding histories, and releasing cached/runtime resources on close and disposal.

- Added an optional MeshLib model-editor mode with validated triangle/quad `noncuboid` authoring, component/topology/UV tools, cuboid conversion, true-mesh primitives, and reflective preview/live-apply integration.

- Fixed the DevTools window closing behavior when leaving a world, and moved the animation, creature, and Prism generators into the main editor window as embedded panels.
- Fixed creature-generator scaling UVs so large generated parts stay textured, and rebuilt membrane wings as segmented torso-connected spars with per-segment membrane panels instead of detached web panels or one giant sheet.
- Fixed procedural animation previews for generated shapes so passive generated surface panels no longer double-transform, and generator slider edits can live-update the currently looping generated animation.
- Fixed shape animation export so sparse/generated keyframes complete partial XYZ transform groups before writing JSON, preventing exported animations from crashing Vintage Story's animation interpolation.
- Improved model-editor chisel mode with a clearer placed-texture picker and automatic merging of adjacent same-texture chisel cubes.
- Added model-editor drag import from the Shapes browser so an existing shape can be dropped into the open model as a movable grouped sub-model, with texture-code conflicts renamed automatically.
- Fixed multi-element model-editor resize so selected cuboids scale as one shape instead of pushing each element face independently.
- Added a ConfigLib scratch-config workflow that can generate a ConfigLib patch, ModConfig default JSON, and authored C# config loader from new settings.
