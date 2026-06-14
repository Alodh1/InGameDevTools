# Changelog

## 0.3.8 - 2026-06-14

Changes since the `0.3.7` dev build:

- Added block animation authoring for placed blocks, including automatic setup for simple blocks, an `InGameDevToolsAnimatedBlock` runtime, and safe fallback behavior when a block already has a custom entity class.
- Added animator and model-editor cut tooling, viewport tool selectors, cut-orientation controls, and freely resizable editor panes.
- Fixed animator cut output so split elements keep their faces and remain visible after cutting.
- Expanded the vanilla animation editor with block targets, preview playback speed, motion trails, first-person/orbit preview improvements, live shape preview refreshes, and safer export/source JSON handling.
- Improved prism/model generation with multi-axis smooth rotations, smoother surface panels, smoother flat sector/triangle generation, preview rotation, resizing/deformation controls, larger size limits, and fixes for rotated-shape gaps and pulled/offset sphere-like primitives.
- Added model-editor workflow tools for setting all face textures together, drag/drop reparenting while preserving world position, file copy/delete/rename/move-folder actions, cleaner file picker text, element splitting, multi-selection helpers, and better transform/gizmo behavior.
- Added structured block/item, ConfigLib, patch, recipe, loot/drop, worldgen, Entity AI, particle, and settings editor improvements while preserving raw unknown JSON fields.
- Improved recipe editing for cooking, stack arrays, stack attributes, recipe attributes, raw JSON formatting/copying, validation, and live apply.
- Added focused tests for new collectible document, ConfigLib document, patch document, primitive math, and editor serialization helpers.

