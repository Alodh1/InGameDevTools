# Performance and Memory Review

Status: completed read-only review. Last updated: 2026-07-10.

No runtime fixes were implemented as part of this review.

## Executive Summary

The game's large memory increase is not caused by one ordinary collection growing a little too large. The project combines several independent high-impact behaviors:

1. At client asset finalization, particle discovery eagerly loads the bytes for effectively every JSON asset in the game and all active mods. Those decompressed byte arrays remain rooted by Vintage Story's global asset registry. In the current installation, the scanned JSON corpus is about 343.1 MB before managed-object overhead.
2. Opening data-heavy editors adds much larger retained graphs. Patch Creator retains both the complete source string and a parsed Newtonsoft <code>JToken</code> tree for every JSON asset. Worldgen independently does the same for every worldgen JSON. The Vanilla Blocks browser independently parses source JSON per runtime block variant rather than per distinct source asset.
3. While the main editor is expanded, recovery work runs every frame. It repeatedly enumerates and deserializes recovery files and constructs editor payloads before determining whether anything is dirty or whether the five-second save delay has elapsed. If a texture-paint canvas exists, the complete canvas is PNG-encoded every frame; a dirty canvas is then base64-encoded too.
4. Several visible editors perform whole-document parsing, cloning, JSON conversion, or native text-buffer allocation every frame. Particle editing and previews are the strongest examples.
5. Editor indexes, histories, preview resources, live-apply snapshots, static roots, and event-registered renderers have incomplete release lifecycles. This explains why the process often keeps a high memory watermark after closing a tab or the editor.

The best explanation for the reported behavior is therefore:

| Observed behavior | Most likely cause |
| --- | --- |
| Large jump just from enabling/loading the mod | The unconditional particle asset scan materializes every JSON asset's <code>Asset.Data</code>. |
| Very large jump after opening Patch Creator | Full source strings plus full <code>JToken</code> graphs for the entire JSON corpus. |
| Another large jump after opening Worldgen | A second independent source-string and <code>JToken</code> index for worldgen assets. |
| Repeated sharp spikes while the editor is open | Recovery payload generation, texture PNG/base64 encoding, per-frame parsing/cloning, native ImGui buffers, and preview mesh uploads. |
| Spikes after using the texture painter | Full-canvas PNG generation every expanded-editor frame, full texture uploads, and retained CPU/GPU copies. |
| Memory does not return after closing the UI | Assets are globally rooted; editor indexes and histories stay cached; live snapshots and preview resources remain referenced; some registered/static objects are not disposed. |
| High CPU and memory pressure in the Particles tab | Whole-family rebuilds, serialized deep clones for dirty checks, JSON token creation, and per-frame CPU/GPU preview mesh creation. |

The startup scan is the primary always-on cause. Patch Creator, Worldgen, Vanilla Blocks, recovery, texture painting, and particle previews can then raise the process into multi-gigabyte territory depending on which tools are used. A live heap/native allocation trace is still needed to assign an exact byte total to each subsystem, but the retention and allocation mechanisms themselves are directly confirmed in code.

## Scope and Method

This review covered the current working tree, including the user's uncommitted changes. It was deliberately read-only apart from this report. The audit included:

- Startup and shutdown lifecycle.
- Asset discovery and indexing.
- All major editors' retained document models and per-frame paths.
- Recovery/autosave behavior.
- Undo histories, diff views, and source-save previews.
- Particle, model, animation, and block preview rendering.
- Live-apply snapshots and runtime patches.
- Static caches, event registrations, and cross-world/mod-disposal behavior.
- The installed Vintage Story and VSImGui assemblies where library behavior materially affects the result.
- The current installed base-game/mod JSON corpus, recovery files, configuration, and recent client log.
- A representative isolated Newtonsoft parse measurement for the largest local JSON asset.

No live Vintage Story process was running during this audit. Consequently, this report distinguishes:

- **Confirmed retention:** a strong reference path can be followed in code or the installed runtime.
- **Confirmed allocation churn:** an allocation occurs on a draw/tick path, but its post-GC residency depends on runtime behavior.
- **Representative measurement:** measured outside the game to establish scale, not claimed as an exact live-heap total.
- **Profiler validation needed:** a plausible impact whose exact magnitude depends on content, driver, or engine lifecycle.

Line references describe the reviewed working tree as of 2026-07-10 and may move after later edits.

## Environment Evidence

### Installed JSON corpus

A read-only scan of the current Vintage Story installation and active mod ZIPs found approximately:

| Corpus | JSON count | Uncompressed JSON bytes |
| --- | ---: | ---: |
| Base game | about 9.4k | 256.9 MB |
| Combined, deduplicated active asset locations | 13,115 | 343,094,758 bytes |
| Combined worldgen JSON | 1,455 | 69,504,391 bytes |

The dominant categories include roughly 213.2 MB of shapes and 69.5 MB of worldgen data. Only 16 base-game JSON files, totaling about 69 KB, contain the literal <code>particleProperties</code> marker used by the embedded-particle scan. In other words, almost the entire base JSON corpus is loaded and decoded to discover a tiny relevant subset.

The largest local JSON asset is:

<code>survival:worldgen/schematics/story/resoarchive.json</code>

Its source is 17,598,197 bytes/characters in this installation.

### Representative JSON retention measurement

In a fresh process using the project's Newtonsoft.Json version, after forcing collection before each stage, that one 17.6 MB asset produced these retained deltas:

| Stage | Retained delta |
| --- | ---: |
| UTF-16 source string | 35,270,544 bytes |
| Parsed <code>JToken</code> object graph | 216,380,928 bytes |
| Combined | 251,651,472 bytes |

This is a representative worst-case schematic, not a universal multiplier for every JSON file. It demonstrates why retaining text and token graphs for large, structurally dense schematics is disproportionately expensive. Patch Creator and Worldgen can each retain their own copy of this asset, making this single file responsible for about 503 MB across those two indexes before surrounding index objects.

### Current recovery/configuration state

The current configuration has:

- <code>OpenOnStartup: true</code>
- <code>EnableRecoveryAutosave: true</code>
- A five-second recovery delay.

The current recovery tree contains 25 snapshots totaling about 1.38 MB on disk. Their payload strings contain about 1.22 million characters. Merely reading the JSON wrappers and materializing those payload strings creates a rough lower bound above 5 MB of string allocation per recovery-list enumeration, before deserialized objects, arrays, sorting, and filesystem overhead.

At 60 visible UI frames per second, repeatedly listing this current recovery tree can therefore produce more than 300 MB/s of string churn. This is an allocation-rate estimate, not retained heap.

### Startup timing signal

The current client debug log shows the LevelFinalize notification at 13:47:15.774 and the custom-particle completion message at 13:47:44.003. Because the latter occurs at the end of particle loading, 28.229 seconds is an upper bound on that startup phase, not an isolated benchmark. It is nevertheless consistent with a broad all-JSON load/decode pass.

## Memory Model: Why Task Manager Shows Such Large Spikes

Several different memory classes are involved:

| Memory class | Examples in this project | Why the number stays high |
| --- | --- | --- |
| Managed retained memory | <code>Asset.Data</code>, source strings, <code>JToken</code> graphs, histories, live snapshots | Strong references keep objects alive. |
| Managed transient/LOH memory | PNG scanlines, JSON strings, mesh arrays, deep clones, diff matrices | Large allocations promote or force Gen 2/LOH collections; the CLR may keep committed segments after objects die. |
| Native process memory | ImGui text buffers, compression/native graphics work | It is outside the managed heap and may fragment or retain allocator high-water capacity. |
| GPU/driver/shared memory | Framebuffers, uploaded meshes, paint textures, staging buffers | Driver reclamation may be deferred; Task Manager can attribute shared graphics memory to the process. |

This distinction explains why forcing a managed collection would not necessarily restore the process working set. Some data is genuinely live, some CLR segments remain committed, some allocations are native, and some are owned by the graphics driver.

## Ranked Findings

### F-01 — Critical: startup particle discovery permanently loads the complete JSON corpus

**Trigger:** every client <code>AssetsFinalize</code>, whether the editor is opened or not.

**Evidence**

- <code>source/InGameDevToolsModSystem.cs:105-115</code> unconditionally calls <code>_particleEffectsManager.LoadAssets()</code>.
- <code>source/InGameDevTools/Utils/ParticleEffectsManager.cs:53-67</code> runs the particle asset scans.
- <code>ParticleEffectsManager.cs:104-195</code> enumerates <code>api.Assets.AllAssets</code>, accepts every JSON candidate, calls <code>api.Assets.TryGet(asset.Location, true)</code>, and calls <code>ToText()</code>. Only afterward does it test the text for <code>particleProperties</code>.
- Decompiled installed Vintage Story behavior confirms that <code>TryGet(..., true)</code> loads an unloaded asset through its origin. <code>Asset.IsLoaded</code> is equivalent to <code>Data != null</code>, and the globally indexed <code>Asset</code> retains that <code>Data</code>.

**Memory mechanism**

The scan forces every JSON file's decompressed <code>byte[]</code> into the global asset registry. In the current installation, the direct byte payload is about 343.1 MB. Array objects, asset metadata, allocator alignment, and already loaded duplicate/runtime structures add overhead.

Each <code>ToText()</code> call also creates a UTF-16 string. Most strings are temporary, but large ones enter the large object heap and increase collection/commit pressure. The scan performs this work for shapes, worldgen, recipes, languages, patches, configs, and other unrelated JSON before rejecting them.

An immediate second startup wave follows in <code>DebugWindowManager.Load</code> at <code>DebugWindowManager.cs:64-69</code>. <code>BuildSourceAssetIndex</code> at <code>DebugWindowManager.cs:498-550</code> reparses every block/item JSON plus config-animation JSON to construct editor source indexes. This is smaller than the all-JSON particle scan but adds token-graph allocation before the user has chosen an editor.

**Why it matters**

This is the strongest explanation for a large baseline increase merely from enabling the mod. The data remains reachable through Vintage Story's asset manager even if the editor is never opened or the particle manager discards its temporary scan structures.

**Confidence:** very high; the root path was confirmed against the installed game assembly.

### F-02 — Critical: Patch Creator retains every JSON twice at object-graph scale

**Trigger:** starting the Patch Creator index.

**Evidence**

- <code>source/InGameDevTools/Animations/PatchCreatorEditor.cs:313-345</code> adds authored patches and then every loaded asset ending in <code>.json</code>. It does not restrict indexing to patchable gameplay categories.
- <code>PatchCreatorEditor.cs:366-370</code> reads the complete text and parses it as a <code>JToken</code>.
- <code>PatchCreatorEditor.cs:1999-2027</code> stores both <code>SourceText</code> and <code>Root</code> in every <code>PatchCreatorAssetEntry</code>.
- The computed search text incorporates the full source text, so filtering can allocate another large combined string for an entry.
- <code>source/InGameDevTools/Utils/DevToolsAssetIndexer.cs:20-24,40-47,72-89</code> also keeps pending/indexed bookkeeping after reaching the ready state.

**Memory mechanism**

Compact UTF-8 JSON becomes a UTF-16 string and then a much larger graph of <code>JObject</code>, <code>JArray</code>, property, value, string, and collection objects. The representative 17.6 MB schematic retained about 251.7 MB for those two forms. The index repeats this design across approximately 13.1k JSON assets and retains the result for the manager lifetime.

The 343.1 MB corpus alone has a theoretical UTF-16 source footprint around 686 MB for predominantly ASCII JSON. Parsed token graphs add an amount determined by document structure and can greatly exceed the text. The isolated schematic measurement demonstrates multi-gigabyte total potential, but an in-game heap capture is required for the exact aggregate.

**Lifetime**

Changing tabs or closing the window does not clear the Patch index. Main-tab cleanup at <code>DebugWindowManager.cs:1219-1226</code> only handles Worldgen preview state and Entity AI live state.

**Confidence:** very high for the mechanism; exact aggregate requires profiling.

### F-03 — Critical: Vanilla Blocks parses and retains source JSON per runtime block variant

**Trigger:** first opening the Vanilla Blocks browser.

**Evidence**

- <code>source/InGameDevTools/Animations/VanillaAnimationEditor.Browser.cs:109-123</code> lazily starts block-list construction.
- <code>VanillaAnimationEditor.Index.cs:152-176</code> loops runtime blocks.
- <code>VanillaAnimationEditor.Index.cs:1536-1543</code> reads and parses the resolved source JSON.
- <code>VanillaAnimationEditor.Types.cs:217-233</code> retains the parsed source through each <code>VanillaBlockSourceInfo</code>.
- <code>VanillaAnimationEditor.Index.cs:248-257</code> clears selection documents, not the completed block index.

**Memory mechanism**

Many runtime variants resolve to the same source asset, but the parsed source object is not shared by source location. The same JSON can therefore exist as many independent <code>JObject</code> graphs. The installed <code>clutter.json</code>, for example, is about 310 KB and supplies many variants. The current log reports roughly 21,907 block types, so the retained duplication can plausibly reach hundreds of megabytes or more depending on the variant-to-source distribution.

The selector also creates a new visible-index list every frame at <code>VanillaAnimationEditor.Browser.cs:765-811</code>. At roughly 21.9k blocks, its backing array grows to approximately 128 KB and lands on the LOH each frame even when the combo is closed.

**Confidence:** high for duplication; exact retained size needs a heap snapshot grouped by source location.

### F-04 — Critical: recovery performs expensive full-state work every expanded-editor frame

**Trigger:** the main editor is open and expanded. Hidden and collapsed paths return before recovery updates; closing through the window X can run one last update on the closing frame.

**Evidence**

- <code>source/InGameDevTools/Animations/DebugWindowManager.cs:1045-1247</code> reaches <code>UpdateDevToolsRecoveryAutosaves()</code> on every expanded draw.
- <code>DebugWindowManager.Recovery.cs:123-138</code> calls every editor's tracking method each time.
- Model recovery serializes the document before the dirty decision at <code>DebugWindowManager.Recovery.cs:141-167</code>.
- Vanilla recovery rebuilds all <code>VanillaAnimationDocument</code> payloads at <code>DebugWindowManager.Recovery.cs:261-294</code>.
- Patch, ConfigLib, loot, transform, and recipe paths similarly construct JSON/text payloads before tracking can reject clean state.
- <code>source/InGameDevTools/Utils/DevToolsRecoveryManager.cs:22-40,144-179</code> encodes and hashes dirty text before checking whether the pending hash/delay makes a write unnecessary.

**Memory mechanism**

The autosave delay controls disk writes, not payload construction. Whole documents, tokens, arrays, hashes, UTF-8 buffers, and pending payload strings can be rebuilt at frame rate. Pending snapshots retain their full payload until flushed.

The visible recovery banner adds a separate issue:

- <code>DebugWindowManager.Recovery.cs:15-19</code> calls <code>ListSnapshots()</code> every visible frame.
- <code>DevToolsRecoveryManager.cs:72-93</code> recursively enumerates every <code>latest.json</code>, reads it completely, deserializes its full payload, sorts, and creates an array.
- The recovery review popup can perform another listing in the same frame.

With the current 25 snapshots, the banner's rough string-allocation lower bound exceeds 5 MB per frame. The exact retained amount is lower after collection, but the allocation rate is sufficient to drive constant Gen 2/LOH activity and a high committed-memory watermark.

**Confidence:** very high.

### F-05 — Critical: texture-paint recovery PNG/base64 encodes the full canvas every expanded-editor frame

**Trigger:** a texture-paint canvas exists while the main editor is open and expanded.

**Evidence**

- <code>DebugWindowManager.Recovery.cs:297-319</code> evaluates <code>_modelTexturePaintCanvas.EncodePng()</code> before <code>TrackBinary</code> can inspect dirty state or the recovery delay.
- <code>source/InGameDevTools/Utils/DevToolsTexturePaintCanvas.cs:8-14</code> permits 4096 by 4096 RGBA canvases.
- <code>DevToolsTexturePaintCanvas.cs:48-81</code> builds a complete scanline array, compressed stream, compressed <code>ToArray()</code>, output stream, and final <code>ToArray()</code>.
- If dirty, <code>DevToolsRecoveryManager.cs:43-60</code> creates a base64 string before the later hash/delay short-circuit.

**Memory mechanism**

At 4096 by 4096:

| Object/work | Approximate size |
| --- | ---: |
| Retained RGBA canvas | 64 MiB |
| Per-encode scanline buffer | about 64 MiB |
| Compression/output streams and copies | content-dependent; potentially multiple 64 MiB-class buffers |
| Worst-case dirty-frame base64 UTF-16 string | up to about 171 MiB |

A noisy 4K canvas can therefore allocate several hundred MiB in one frame. Repeating that at draw rate explains abrupt working-set spikes, LOH collections, pauses, and a process watermark that does not quickly fall. A clean canvas still pays PNG encoding; dirty state only adds the base64/hash work.

At 1024 by 1024, the retained canvas and scanline are each about 4 MiB, which is still substantial at frame rate.

**Related texture cost**

Painting marks the texture dirty on each mouse-down frame. <code>ModelEditor.Uv.cs:1151-1207</code> re-uploads the full pixel buffer with <code>GL.TexImage2D</code>. At 4K this combines a retained 64 MiB managed canvas, roughly 64 MiB of GPU texture storage, and repeated full 64 MiB pinned uploads plus driver staging/reallocation. CPU and GPU copies remain cached across tab changes and normal UI close.

**Confidence:** very high; this is the most direct explanation for repeated spikes after texture painting has been initialized.

### F-06 — Critical while visible: Particles rebuilds and deep-compares whole effect graphs every frame

**Trigger:** drawing the Particles editor.

**Evidence**

- <code>ParticleEffectsManager.cs:530-549,904-921</code> rebuilds dictionaries, family builders, variants, sorted lists, strings, and arrays every frame.
- Visible family rows evaluate <code>family.IsModified</code> at <code>ParticleEffectsManager.cs:579-594</code>.
- <code>ParticleEffectsManager.cs:1731-1746,1949-1971</code> implements dirty comparison by cloning both particle graphs, normalizing them, converting both to <code>JToken</code>, and performing deep equality.
- Installed <code>AdvancedParticleProperties.Clone()</code> serializes through a <code>MemoryStream</code>/<code>BinaryWriter</code> and deserializes through a <code>BinaryReader</code>; it is not a cheap memberwise copy.
- Selected families, variants, and emitters repeat modified-state checks at <code>ParticleEffectsManager.cs:683-714,770-777</code>.
- The selected emitter independently constructs complete before/after <code>JToken</code> trees every frame at <code>ParticleEffectsManager.cs:642-649</code>.
- Each retained entry already holds both <code>Properties</code> and <code>OriginalProperties</code>.

**Memory mechanism**

The tab combines retained duplicate effect graphs with a very high allocation rate from serialization-based clones, streams, byte arrays, token trees, family/list reconstruction, and reflection/JSON support. This is allocation and CPU churn rather than an unbounded particle collection.

Static particle JSON/status dictionaries at <code>ParticleEffectsManager.cs:3647-3653</code> have no eviction and retain visited buffers for process lifetime.

**Confidence:** very high.

### F-07 — High: Worldgen independently retains full source and token trees

**Trigger:** starting the Worldgen index.

**Evidence**

- <code>source/InGameDevTools/Animations/WorldgenEditor.cs:258-305</code> adds authored assets, the worldgen category, all loaded client assets, and optionally server assets.
- <code>WorldgenEditor.cs:339-352</code> filters worldgen JSON, reads the complete text, parses a token tree, and retains an entry.
- <code>WorldgenEditor.Types.cs:35-57</code> stores the <code>Asset</code>, <code>SourceText</code>, and <code>Root</code>.
- Selecting a document retains additional original/current text at <code>WorldgenEditor.cs:6496-6509</code>.

**Memory mechanism**

The current worldgen corpus is 1,455 files and 69.5 MB of compact JSON, including 17.6 MB, 9.0 MB, and 7.4 MB schematics. Like Patch Creator, the editor retains UTF-16 text plus a much larger parsed object graph. Patch Creator and Worldgen do not share parsed documents.

The computed search representation includes full source content and can create further large strings when filtering. The index stays reachable after changing tabs.

**Confidence:** very high.

### F-08 — High: large ImGui text widgets allocate two unmanaged buffers every drawn frame

**Trigger:** a large <code>InputTextMultiline</code> widget is drawn, including read-only widgets.

**Evidence**

The active installed VSImGui ZIP contains the same <code>ImGui.NET.dll</code> as the inspected NuGet assembly, version 1.89.9.2. Its <code>ImGui.InputTextMultiline(ref string, maxLength, ...)</code> wrapper allocates two unmanaged buffers of the requested capacity with <code>Marshal.AllocHGlobal</code>, copies/clears them, and frees them on every call.

The project has 43 multiline calls. Representative visible capacities are:

| Editor/widget | Source | Requested capacity | Native allocation per drawn frame |
| --- | --- | ---: | ---: |
| Model raw JSON | <code>ModelEditor.Serialization.cs:466</code> | 4,000,000 | about 8 MB |
| Entity AI JSON | <code>AiBehaviorEditor.cs:1814-1815</code> | at least 2 MiB | about 4 MiB |
| Worldgen JSON | <code>WorldgenEditor.cs:1366-1367</code> | at least 2 MiB | about 4 MiB |
| Block/Item JSON | <code>BlockItemJsonEditor.cs:436</code> | 1 MiB | about 2 MiB |
| Loot/trade JSON | <code>LootDropEditor.cs:741</code> | 1 MiB | about 2 MiB |
| Particle JSON | <code>ParticleEffectsManager.cs:3735</code> | 2 MiB | about 4 MiB |
| Patch diff | <code>PatchCreatorEditor.cs:1213</code> | 2 MiB | about 4 MiB |

Patch source preview at <code>PatchCreatorEditor.cs:589</code> sizes itself from the selected source. Selecting the 17.6 MB schematic can therefore allocate roughly 35 MB of native buffers every frame for a read-only preview.

**Memory mechanism**

The buffers are freed each call, so they are not a managed leak. Repeated large native allocation/free cycles can fragment the native heap, raise process working-set high water, and add substantial copying/zeroing CPU cost. Managed profilers alone will miss this source.

**Confidence:** very high; behavior was verified against the active assembly.

### F-09 — High: particle preview creates CPU and GPU meshes every frame

**Trigger:** running a particle preview, especially near the 6,000-particle cap.

**Evidence**

- <code>source/InGameDevTools/Utils/PreviewParticleSystem.cs:19-23</code> caps live particles at 6,000.
- <code>ParticleEffectsManager.cs:2280-2301</code> creates billboard lists each frame.
- <code>source/InGameDevTools/Utils/DevToolsPreview3DRenderer.cs:264-319</code> creates a fresh <code>MeshData</code> and backing arrays, uploads a new GPU mesh, renders it, and disposes it. Quad and cube batches can repeat the process in one frame.
- Runtime-block preview defaults to roughly 30 Hz at <code>ParticleEffectsManager.cs:1880-1886,2137-2149</code> and creates preview managers/deep clones at <code>ParticleEffectsManager.cs:3088-3166,3435-3444</code>.

**Memory mechanism**

At 6,000 billboards, managed mesh arrays are LOH-sized. A representative estimate is roughly 1.0-1.2 MiB of managed billboard mesh allocation per frame, or 60-70 MiB/s at 60 FPS, plus lists and effect objects. Continuous GPU buffer creation/upload/deletion adds driver-side spikes because deletion can be deferred.

The reference placement cache at <code>ParticleEffectsManager.cs:2420-2440</code> has no size bound. Each browsed reference can retain an uploaded GPU mesh and up to 5,000 managed triangle records. It is not cleared on ordinary selection changes, tab switches, collapse, or editor close.

**Confidence:** high.

### F-10 — High: histories, diff views, and save previews duplicate whole documents

**Trigger:** editing large documents, building diffs, or opening a source-save preview.

**Evidence**

- <code>source/InGameDevTools/Utils/DevToolsTextHistory.cs:8-57</code> retains up to 40 full text states.
- Model history has a limit of 120 at <code>ModelEditor.cs:21</code>; each <code>ModelHistoryEntry</code> stores full JSON, and edits serialize before/after states at <code>ModelEditor.cs:194-203,4430-4488</code>.
- Vanilla animation history holds up to 100 deep snapshots per document at <code>VanillaAnimationEditor.HistoryExport.cs:24-110,331-446</code>.
- Animation history retains cloned animation state plus a serialized duplicate.
- <code>source/InGameDevTools/Utils/DevToolsTextDiffView.cs:13-23,71-97</code> has a static cache retaining original text, current text, and the computed diff.
- <code>DevToolsTextDiff.cs:20-52,142-157</code> formats/parses both sides, splits lines, and can allocate an LCS <code>int[,]</code> up to 1201 by 1201, about 5.8 MB.
- <code>DebugWindowManager.cs:125-140</code> source-save requests retain both old and new text. The popup normalizes/splits them every frame and renders all lines without clipping at <code>DebugWindowManager.cs:1766-1837</code>.

**Memory mechanism**

Whole-document snapshots scale linearly with history depth. A compact 2 MiB ASCII JSON document is roughly 4 MiB as a UTF-16 string; 120 model snapshots alone can approach 480 MiB, before the current document, parsed shape, metadata clones, diff, recovery payload, and preview.

This is bounded retention, but the bounds are large enough to be a primary memory contributor.

**Confidence:** very high.

### F-11 — High: editor indexes and unvirtualized lists accumulate through the session

**Trigger:** visiting multiple tabs and browsing large asset lists.

**Evidence**

- Main-tab cleanup at <code>DebugWindowManager.cs:1219-1226</code> does not unload Patch, Worldgen, Recipe, Block/Item, loot, animation, or source indexes.
- Patch Creator renders its filtered list at <code>PatchCreatorEditor.cs:454-471</code> without an ImGui list clipper.
- Worldgen similarly loops at <code>WorldgenEditor.cs:421-440</code>.
- Block/Item and Recipe browser loops iterate all entries at <code>BlockItemJsonEditor.cs:319-360</code> and <code>RecipeEditor.cs:447-466</code>.
- Category/domain LINQ projections and label strings are rebuilt on draw paths.

**Memory mechanism**

Each editor leaves its retained index behind, causing a stepwise session ratchet: startup asset bytes, then block lists, then Patch tokens, then Worldgen tokens, then histories and previews. Unvirtualized list work adds per-frame labels, arrays, filtering, and native ImGui calls even when only a small portion is visible.

**Confidence:** very high.

### F-12 — High: live-apply snapshots have no removal lifecycle

**Trigger:** applying or merely capturing many distinct live targets.

**Evidence**

- <code>source/InGameDevTools/Utils/LiveApplyManager.cs:11</code> owns the entry dictionary.
- <code>LiveApplyManager.cs:74-123</code> revert operations only mark entries unapplied; they do not remove entries or release snapshots.
- <code>LiveApplyManager.cs:228-245</code> permanently stores snapshots through <code>EnsureEntry</code>.
- <code>DebugWindowManager.cs:1629-1641</code> clears editor-specific hashes/overrides, not the live-apply dictionary.
- Snapshot closures can retain vanilla animation arrays and runtime shapes, particle arrays, cloned attribute trees, original model bytes/text, recipe registry arrays, and loot arrays.
- Vanilla originals are captured eagerly even when auto-apply is disabled at <code>VanillaLiveApply.cs:23-32</code>.

**Memory mechanism**

The dictionary grows by target and keeps closure-captured object graphs for the full manager lifetime. Reverting changes runtime state but does not release the original snapshot. Depending on target type, one entry can retain entire arrays or asset payloads rather than a small delta.

**Confidence:** very high.

### F-13 — High: disposal omissions can retain the complete manager and GPU/native graph

**Trigger:** mod disposal/hot reload, repeated client lifecycles, and some world transitions. Ordinary tab switching/closing also leaves many resources warm.

**Evidence**

- <code>DebugWindowManager.cs:34-59</code> creates <code>TransformGizmoRenderer</code>, <code>ImGuiAnimationViewportRenderer</code>, and <code>DetachedEditorCamera</code>.
- <code>DebugWindowManager.cs:71-94</code> does not dispose those objects.
- Their cleanup exists at <code>TransformGizmoRenderer.cs:1022-1028</code>, <code>ImGuiAnimationViewportRenderer.cs:91-94</code>, and <code>DetachedEditorCamera.cs:394-402</code>.
- The gizmo registers a renderer and mouse handlers and holds the manager, allowing the event manager to root the entire editor graph.
- <code>DebugWindowManager._instance</code> is assigned at construction and never cleared. <code>AnimationsManager</code> has a similar static instance without reset.
- <code>ParticleEffectsManager</code> is not disposable and retains preview particles, renderer/framebuffer, and placement cache.
- Vanilla preview resources retain a cloned shape, two client animators, managed mesh data, GPU mesh references, and a framebuffer; cleanup exists but is not called by manager disposal.
- <code>DevToolsPreview3DRenderer.EngineParticleSpawnRedirect</code> installs Harmony ID <code>ingamedevtools.preview-particles</code> without a matching unpatch in the mod system.

**Memory mechanism**

Event lists and static fields can keep the manager, API/world, all editor indexes, histories, texture canvas, live snapshots, and GPU wrapper objects reachable even after the mod system nulls its own fields. GPU resources may also remain registered or cached.

Exact-size RGBA8 plus Depth32 preview framebuffers cost roughly 8 bytes per pixel before driver overhead: about 15.8 MiB at 1920 by 1080, 28.1 MiB at 2560 by 1440, and 63.3 MiB at 4K. Model, particle, and vanilla previews can each retain their own framebuffer.

**Confidence:** very high for missing calls/root paths; live retained totals need post-disposal profiling.

### F-14 — High/medium: global pose replacement and caches raise the baseline for all animations

**Trigger:** all client animations, even with the editor closed.

**Evidence**

- <code>InGameDevToolsModSystem.cs:54-57</code> installs global animation transpilers.
- <code>source/InGameDevTools/Integration/Transpilers/ElementPose.cs:9-19</code> adds an entity reference, enum, and hash to every replacement pose.
- Transpilers replace pose construction in <code>Animation.GenerateFrame</code> and <code>ClientAnimator.LoadPosesAndAttachmentPoints</code>.
- <code>source/InGameDevTools/Utils/ObjectCache.cs:10-11</code> keeps keys in two strong dictionaries.
- The configured cleanup threshold is stored but never used; cleanup is age-only. Dictionary capacity does not shrink.
- Cleanup scans the whole cache under a write lock and allocates two <code>HashSet</code>s.

**Memory mechanism**

The extra pose fields add approximately 16 bytes per pose on x64 before GC/reference-scanning effects. Content-heavy generated animation frames can have many poses. Shape-element cache entries remain for roughly one to two cleanup intervals, while dictionary bucket capacity stays at its peak.

The 500,000 argument passed to the name cache is **not** an immediate allocation or working capacity. It is an unused cleanup-threshold value and should not be mistaken for the startup spike.

The global animation hook also performs locked cache lookups for every injected pose calculation. Only player managers are inserted, so non-player animators repeatedly miss and still take read/write locks. This is primarily CPU/lock overhead.

**Confidence:** high for the mechanism; workload-dependent impact.

### F-15 — High conditional: runtime particle overrides deep-clone on particle ticks

**Trigger:** at least one live particle override is active.

**Evidence**

- <code>source/InGameDevTools/Integration/ParticleRuntimePatches.cs:54-67</code> stores a deep-cloned override.
- Patches cover all discovered block particle methods at <code>ParticleRuntimePatches.cs:102-200</code>.
- The prefix at <code>ParticleRuntimePatches.cs:202-251</code> acquires a per-block lock before override lookup, copies a sorted dictionary, creates a new array, and deep-clones every original and override particle property.
- For a matching block singleton, the lock remains held through the original emitter call until the finalizer.

**Memory mechanism**

Installed <code>AdvancedParticleProperties.Clone()</code> serializes/deserializes through streams, so each runtime clone creates streams, buffers, and a new object graph. Dense particle ticks can create high allocation rates and contention. This is conditional and is not the initial startup cause.

**Confidence:** very high.

### F-16 — High workload-dependent: animated blocks duplicate resources and can orphan renderers

**Trigger:** loaded instances using the animated-block runtime feature.

**Evidence**

- <code>source/InGameDevTools/Integration/AnimatedBlockRuntime.cs:75-125</code> clones a shape, resolves it, generates frames, tessellates a mesh, and creates a dedicated animator/renderer for every instance. Resources are not shared by block code/shape/animation.
- Installed <code>AnimatableRenderer</code> already uploads and registers itself, but this code registers it again. Opaque rendering is duplicated while alive.
- Initialization is queued. There is no disposed/unloaded guard, so queued initialization can run after unload.
- The exception path after partial creation does not dispose a renderer that was already constructed.

**Memory mechanism**

Per-instance managed shape/frame/mesh graphs and GPU buffers scale with loaded block count. The duplicate registration increases render work. Queue races or partial failures can leave renderers and meshes without a later unload callback.

**Confidence:** high.

### F-17 — High/medium while visible: AI and Block/Item repeatedly parse whole documents

**Entity AI**

<code>AiBehaviorEditor.cs:363-369</code> calls dirty comparison for every visible browser row. <code>AiBehaviorEditor.cs:1951-1961</code> parses both current and original JSON strings into <code>JObject</code> and performs deep equality. The same strings can be reparsed multiple times per frame across rows.

**Block/Item**

<code>BlockItemJsonEditor.cs:396</code> parses the full buffer for the center view. Inspector and preview paths parse it again at <code>BlockItemJsonEditor.cs:1237,1457-1472</code>. Structured editor sections deep-clone subtrees during draw. The visible raw widget also incurs the native two-buffer cost described in F-08.

**Memory mechanism**

Repeated token graphs are mostly temporary, but large documents create LOH pressure and high allocation rates. Because these operations are draw-driven rather than edit-driven, an idle visible tab can be expensive.

**Confidence:** high.

### F-18 — High feature-gated: model, rig, onion-skin, and motion-path previews rebuild large data

**Model preview**

Continuous gizmo drags call <code>ModelMarkChanged</code>, causing <code>ModelEditor.Viewport.cs:670-695</code> to dispose the previous preview mesh, serialize the full model, parse a <code>Shape</code>, tessellate it, and upload new GPU buffers on each modified frame. Old meshes are correctly disposed, but the repeated managed/native/GPU allocation can still spike memory while drivers defer reclamation.

**Rig visualization**

<code>TransformGizmoRenderer.cs:103-112</code> requests rig visualizations every opaque frame. Onion skinning clones pose trees/matrices for neighbor frames. Motion paths serialize the entire animation for a cache key and include playback time in that key, so playback invalidates the cache and rebuilds 8-240 sampled pose trees per render frame.

**Memory mechanism**

These are feature-gated allocation/CPU/GPU storms rather than always-on retained leaks. They can dominate while active.

**Confidence:** high.

### F-19 — Medium/high: cross-world static behavior references can retain old entities/worlds

**Trigger:** player/entity despawn, world leave/rejoin, or multiplayer churn.

**Evidence**

- <code>source/InGameDevTools/StandaloneAnimationBehaviors.cs:139-152</code> removes third-person behavior entries only through <code>IDisposable.Dispose</code>.
- First-person static state similarly clears only in <code>Dispose</code>.
- Decompiled engine behavior calls <code>OnEntityDespawn</code> on behaviors; it does not automatically invoke <code>IDisposable</code>. These classes do not override the despawn method.
- Static behavior collections are cleared only during full patch teardown.
- <code>DebugWindowManager._behavior</code> can retain an old player and later uses null-coalescing assignment rather than replacing it for a new world.

**Memory mechanism**

Static collections can keep behavior, entity, player, API, and old-world graphs reachable until overwritten or full mod disposal.

**Confidence:** high for the reference path; severity depends on session behavior.

## Why Memory Does Not Fall After Closing a Tab

Closing or switching tabs generally stops some draw-time churn, but it does not reverse the large retained state:

- Startup-loaded <code>Asset.Data</code> remains in Vintage Story's global asset registry.
- Patch, Worldgen, block, recipe, loot, and other indexes stay on <code>DebugWindowManager</code>.
- Histories and static diff/particle-edit caches remain populated.
- Texture canvas, GL texture, preview scenes, framebuffers, particle reference models, and uploaded meshes remain warm.
- Live-apply entries keep original snapshots even after revert.
- Event registrations and static manager instances can root the whole manager graph through disposal.
- The CLR does not normally return every empty LOH/Gen 2 segment to the operating system immediately.
- Native allocators and GPU drivers can retain or defer reclamation of freed blocks.

This means a flat high working set after closing the UI is expected from the current ownership model even when the most aggressive per-frame allocations have stopped.

## Important Non-Causes and Nuances

- The preview particle collection is capped at 6,000; it is not an unbounded list leak. The issue is per-frame reconstruction/upload and retained preview resources.
- The <code>500000</code> cache constructor argument does not preallocate 500,000 entries. Its corresponding threshold is simply unused.
- Several mesh/framebuffer rebuild paths correctly dispose the previous resource. Their problem is allocation/upload churn and delayed driver reclamation, not necessarily a direct leaked object on every rebuild.
- Recovery encoding does not continue while the editor is fully hidden or in its explicit collapsed mode. Its full-state payload work occurs on the expanded path; retained canvas/index/preview memory survives the close/collapse.
- The current tutorial-related working-tree changes are not a credible source of the reported large spike. Tutorial anchors are no-ops unless active and retain only small rectangle/state structures.
- A high process working set is not identical to a high live managed heap. Native ImGui allocations and GPU/shared memory must be profiled separately.

## Recommended Validation Plan

The following measurements would turn this static diagnosis into exact live byte totals without first changing production behavior:

1. **Startup baseline**
   - Capture before client <code>AssetsFinalize</code>, after particle <code>LoadAssets</code>, and after a forced full GC.
   - Sum <code>Asset.Data?.LongLength</code> and count assets transitioning from unloaded to loaded.
   - Expected dominant root: asset manager/global assets to <code>Asset</code> to <code>byte[] Data</code>.

2. **Tab-by-tab retained heap**
   - Capture closed editor, Animations, first Vanilla Blocks open, Patch Creator ready, Worldgen ready, and Particles.
   - Group <code>JObject/JToken</code> instances by editor owner and source location.
   - For Vanilla Blocks, compare runtime option count with distinct source locations.

3. **Recovery allocation rate**
   - Compare recovery enabled/disabled, empty/current recovery directory, editor expanded/collapsed, and texture canvas absent/present.
   - Trace <code>EncodePng</code>, <code>MemoryStream.ToArray</code>, <code>Convert.ToBase64String</code>, <code>JsonConvert</code>, <code>byte[]</code>, and <code>string</code>.
   - Test clean and dirty canvases at 512, 1024, 2048, and 4096 pixels.

4. **Native ImGui allocation**
   - Use native allocation/ETW tooling while displaying large multiline fields.
   - Managed allocation tools will not show the two <code>AllocHGlobal</code> buffers.

5. **Particle editor and preview**
   - Compare closed editor, idle Particles tab, paused preview, running preview, and 6,000 particles.
   - Group allocation stacks under <code>AdvancedParticleProperties.Clone</code>, <code>MemoryStream</code>, <code>JToken.FromObject</code>, <code>ParticlePropertiesMatch</code>, <code>MeshData</code>, and <code>UploadMesh</code>.
   - Record managed allocation rate, Gen 2/LOH collections, CPU frame time, GPU buffer creation/deletion, and VRAM/shared memory.

6. **History and live-apply growth**
   - Edit a controlled large document through maximum history depth.
   - Apply/revert many distinct target types.
   - Inspect history strings/snapshots and <code>DevToolsLiveApplyManager._entries</code> dominators after revert and UI close.

7. **Lifecycle test**
   - Open every preview, close the editor, leave/rejoin a world, and dispose/reload the mod.
   - Force GC between phases.
   - Inspect generations of <code>DebugWindowManager</code>, registered renderers, mouse delegates, static instances, <code>FrameBufferRef</code>, mesh refs, GL textures, and old player/world objects.

8. **Conditional runtime paths**
   - Profile a live particle override in a particle-heavy scene.
   - Profile many animated block instances.
   - Profile onion skin/motion paths during playback and a scene with many non-player animators.

Useful tooling includes <code>dotnet-counters</code>, <code>dotnet-gcdump</code> or PerfView for managed data, ETW/native heap tooling for ImGui and compression/native allocations, and RenderDoc or driver tooling for GPU allocations.

## Remediation Priority for a Later Change

No fixes are included here. Based on impact and confidence, a future implementation pass should be prioritized in this order:

1. Stop the unconditional all-JSON particle load/decode at startup.
2. Redesign Patch, Worldgen, and Vanilla Blocks indexes so they do not retain duplicate full text/token graphs for every candidate or variant.
3. Move recovery serialization/PNG/base64 work behind dirty-state and time gates; stop listing/deserializing every snapshot every frame.
4. Eliminate frame-rate whole-graph cloning/parsing in Particles, AI, Block/Item, and other editors.
5. Replace high-capacity per-frame multiline marshalling and virtualize large browser lists.
6. Bound or delta-compress histories, diffs, search text, and live-apply snapshots.
7. Reuse particle/model preview mesh buffers and avoid full texture reallocation/upload per paint frame.
8. Define explicit tab-close, world-leave, and manager-disposal ownership for all indexes, histories, renderers, framebuffers, meshes, textures, caches, Harmony patches, and static references.
9. Address global animation-cache and conditional runtime patch overhead after the larger memory multipliers are removed.

## Conclusion

The initial spike has a clear primary cause: particle discovery eagerly loads the complete active JSON corpus into globally retained asset byte arrays. The largest subsequent increases come from editors that retain source strings plus parsed JSON graphs, especially Patch Creator, Worldgen, and the per-variant Vanilla Blocks index. The sharp repeated spikes are explained by frame-driven recovery and texture encoding, deep particle cloning, native ImGui buffers, and preview mesh/texture uploads. Finally, incomplete unload/disposal paths and deliberately warm editor caches explain why the process does not return to its pre-editor baseline.

These findings are sufficient to guide a focused remediation pass. Exact per-subsystem live totals should be captured with the validation sequence above before changing ownership and caching semantics.
