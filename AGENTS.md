# AGENTS.md

Compact guidance for AI agents working in this repo. For full architecture details, see `CLAUDE.md`.

## What This Project Is

Unity C# mod for **Rhythm Doctor** level editor — adds keyboard navigation and screen reader support. Two separate .NET projects communicating via file-based IPC:

- **RDLevelEditorAccess** (.NET Standard 2.1): BepInEx mod running inside Unity
- **RDEventEditorHelper** (.NET Framework 4.8): Standalone WinForms property editor

## Setup (Required Before Building)

```bash
cp Directory.Build.user.props.example Directory.Build.user.props
# Edit Directory.Build.user.props — set <GameDir> to RD install path
# Example: C:\Program Files (x86)\Steam\steamapps\common\Rhythm Doctor
```

Builds auto-deploy to GameDir on every `dotnet build`. Without GameDir configured, build succeeds but nothing deploys.

## Build & Release

```bash
dotnet build RDLE-a11y.sln              # Debug (auto-deploys)
dotnet build RDLE-a11y.sln -c Release
dotnet build RDLevelEditorAccess/RDLevelEditorAccess.csproj  # Single project
dotnet build RDEventEditorHelper/RDEventEditorHelper.csproj
./release.sh                            # Release build + package into release/main/
```

Auto-deployment targets (from `Directory.Build.props`):
- Mod DLL + PDB → `{GameDir}/BepInEx/plugins/`
- Helper EXE → `{GameDir}/`

No test suite exists. `EditorFormTests.cs.bak` in Helper project is a disabled test file.

## Architecture Quick Reference

### Source Files (all that exist)

| File | Role |
|------|------|
| `RDLevelEditorAccess/EditorAccess.cs` | **Core (~4200 lines)**: BepInEx plugin (`EditorAccess`), main logic (`AccessLogic`), `ModUtils`, `RDStringPatch` |
| `RDLevelEditorAccess/AccessibilityModule.cs` | Public API (`AccessibilityBridge`) + `UnityDispatcher` |
| `RDLevelEditorAccess/IPC/FileIPC.cs` | File-based IPC (~3200 lines), all Helper launch/result handling |
| `RDLevelEditorAccess/CustomUINavigator.cs` | Disables Unity native directional key navigation |
| `RDLevelEditorAccess/InputFieldReader.cs` | TTS for input fields (state diffing) |
| `RDEventEditorHelper/Program.cs` | Entry point, reads source.json, writes result.json |
| `RDEventEditorHelper/EditorForm.cs` | WinForms property editor UI (~2200 lines) |

### Execution Flow

`EditorAccess.Awake()` → Harmony patches + scene loaded handler → `AccessLogic` MonoBehaviour created → `AccessLogic.Update()` dispatches to:
1. `HandleGeneralUINavigation` (Unity UI menus open)
2. `HandleTimelineNavigation` (default)
3. `HandleVirtualMenu` (when `virtualMenuState != None`)

### Harmony Patches (all in EditorAccess.cs)

| Patch Class | Target | Type | Purpose |
|---|---|---|---|
| `EditorPatch` | `scnEditor.SelectEventControl` | Postfix | Announce selection |
| `EditorPatch` | `scnEditor.AddEventControlToSelection` | Postfix | Announce multi-select |
| `VirtualMenuInputBlockPatch` | `scnEditor.userIsEditingAnInputField` | Postfix | Block input while virtual menu open |
| `TabSectionPatch` | `TabSection.ChangePage` | Postfix | Announce tab page change |
| `TabSwitchPatch` | `scnEditor.ShowTabSection` | Postfix | Announce tab switch |
| `TimelinePatch` | `Timeline.PreviousPage` / `NextPage` | Postfix | Announce timeline page |
| `TimelineNavigationPatch` | `scnEditor.PreviousButtonClick` / `NextButtonClick` | Postfix | Navigate by page with selection |
| `CopyVirtualSelectionPatch` | `scnEditor.Copy` | Prefix | Ctrl+Shift+C: copy virtual selection |
| `CutVirtualSelectionPatch` | `scnEditor.Cut` | Prefix | Ctrl+Shift+X: cut virtual selection |
| `PasteAlignmentPatch` | `scnEditor.Paste` | Prefix+Postfix | Align pasted events to edit cursor |
| `RDStringPatch` | `RDString.Get` | Prefix | Inject `eam.*` custom localization keys |

### IPC Protocol

Mod writes `temp/source.json`, launches Helper EXE, polls for `temp/result.json`. Token-based session matching prevents races.

Edit types: `"event"`, `"row"`, `"settings"`, `"condition"`, `"jump"`, `"chainName"`, `"gridCustom"`, `"tickInput"`.

Result actions: `"ok"` (with updates dict), `"execute"` (with methodName), `"cancel"`, `"bpmCalculator"` (with updates), `"validateVisibility"` (real-time property visibility check).

**Never call `FileIPC` directly** — use `AccessibilityBridge` in `AccessibilityModule.cs`.

### Game Code Reference

**Always check `agents references/Assembly-CSharp/` before modifying code.** This is decompiled game code. The `RDLevelEditor/` subfolder has ~360 files covering the editor API. Key files:

- `scnEditor.cs` — main editor controller
- `LevelEvent_Base.cs` — base class for all events
- `LevelEventInfo.cs` / `BasePropertyInfo.cs` — event/property metadata
- `InspectorPanel.cs` — property panel base
- `SaveStateScope.cs` — undo scope

The `Assembly-CSharp-Old/` folder is an older decompile — use the unmarked one for current game code.

## Critical Patterns & Gotchas

### Unity "Fake Null"

```csharp
// WRONG: Unity objects can be "fake null" after Destroy
if (someUnityObj != null) { }

// RIGHT:
if (scnEditor.instance == null) return;
if (menuObj != null && menuObj.activeInHierarchy) { }
```

### SaveStateScope for Undo

`SaveStateScope` calls `SaveState()` in constructor (saves current state to undo stack), then increments `changingState`. `Dispose()` decrements `changingState`. When `changingState != 0`, `SaveState()` is a no-op — so nested scopes don't create extra undo points.

**Key rules:**
- Wrap programmatic property changes in `using (new SaveStateScope())` for undo support
- Call `UpdateUIInternal()` **outside** the scope
- **Harmony Postfix caveat**: Postfix runs after the original method returns, so the original method's `SaveStateScope` has already disposed and `changingState` is back to 0. If you create a new `SaveStateScope` in Postfix, it will produce a **separate** undo point. This means one logical operation (e.g. paste+align) requires two Ctrl+Z to undo. **Do not wrap Postfix modifications in `SaveStateScope`** if they should be atomic with the original method's changes — the undo from `DecodeData` will already restore the pre-method state.

```csharp
using (new SaveStateScope())
{
    levelEvent.someProperty = newValue;
}
// UI update goes here, OUTSIDE the scope
selectedControl.UpdateUIInternal();  // on LevelEventControl_Base
```

### SoundData Sentinel Values

In `EditorForm.cs`, `CreateSoundDataPanel()` uses two sentinel tags:
- `"__track_default__"` → nullable SoundData, serialized as `""`
- `"__manual__"` → manual filename input mode

Guard against both in ListView selection / sound preview.

### Localization

- Game native keys live in `agents references/localization/auto/` (`.bytes` files)
- `RDString.Get(key)` — goes through `RDStringPatch`, supports `eam.*` custom keys
- `RDString.GetWithCheck(key, out bool exists)` — bypasses patch; use for checking native key existence only. **Never use for `eam.*` keys.**
- Custom `eam.*` keys defined in `RDStringPatch` class (`EditorAccess.cs` bottom)
- Check native keys first before adding new `eam.*` keys

### Narration API

```csharp
Narration.Say("text", NarrationCategory.Navigation);
Narration.Say("text", NarrationCategory.Navigation,
              flipCategoryQueueBehaviour: true);  // queue after current speech
```

### Accessing Private Game Fields

Use reflection cache pattern from `EditorAccess.cs`. Write via public methods like `CycleSnapValues(int)` when available.

### Ctrl+Shift+Enter Quick Action

`HandleAltEnter()` dispatches by event type. When adding support for a new event type, add a case to the switch in `HandleAltEnter()`.

**Key modifier exclusion**: Ctrl+Enter and Shift+Enter handlers must exclude the other modifier to avoid Ctrl+Shift+Enter being intercepted:
```csharp
if (Input.GetKeyDown(KeyCode.Return) && ctrl && !shift) { ... }   // Ctrl+Enter
if (Input.GetKeyDown(KeyCode.Return) && shift && !ctrl) { ... }   // Shift+Enter
if (Input.GetKeyDown(KeyCode.Return) && ctrl && shift) { ... }    // Ctrl+Shift+Enter
```

### Virtual Menu Pattern

To add a new virtual menu:
1. Add value to `VirtualMenuState` enum
2. Add `Start*()` method: set `virtualMenuState`, `virtualMenuIndex`, call `SetFakeInputField()`, announce
3. Add `Handle*()` method: Up/Down navigate, Enter confirm, Escape cancel, optional number keys
4. Add case in `HandleVirtualMenu()` switch
5. On confirm: wrap changes in `SaveStateScope`, call `selectedControl.UpdateUIInternal()`, then `CloseVirtualMenu()`

Current `VirtualMenuState` values: None, CharacterSelect, EventTypeSelect, LinkSelect, EventChainSelect, ConditionalSelect, GridSelect, VirtualSelectionOptions, BeatModifierPatternSelect.

### Beat Modifier (SetRowXs) Pattern Property

`LevelEvent_SetRowXs.pattern` is a 6-char string. Each position represents a beat in the measure:
- `-` = normal (beat plays), `x` = skip (beat muted)
- `u`/`d` = up/down arrow, `b`/`r` = pull/release (rare, right/middle click)

Semantic naming follows `Narration.GetSkipSpeech` logic: list 1-indexed positions of `x` chars. E.g. `"-x-x-x"` → "X拍2 4 6".

## Code Style

- **Chinese comments** are standard; XML docs for public APIs
- `[ModuleName]` prefix in log messages
- Private fields: camelCase or `_prefix`; methods/classes/properties: PascalCase
- Import order: System → BepInEx/Harmony → Unity → RDLevelEditor, alphabetical within
- Namespace conflict resolution: `using Button = UnityEngine.UI.Button;` or `using Button = System.Windows.Forms.Button;`
- Section separators: `// ==================...`
- Git commits: short Chinese — `添加 XX 功能` / `修复 XX 问题` / `重构 XX 模块`

## Debugging

- Mod logs: `{GameDir}/BepInEx/LogOutput.log`
- Helper logs: `{GameDir}/temp/RDEventEditorHelper.log` (overwrites each launch)
- Manual Helper test: create `temp/source.json`, run `RDEventEditorHelper.exe` from `{GameDir}`
- PDB files auto-deploy alongside DLLs for debugging

## Key Game Concepts

- **Tab system**: Song(0), Rows(1), Actions(2), Rooms(3), Sprites(4), Windows(5)
- **onlyUI properties**: `onlyUI = true` → NOT saved to level files
- **PropertyInfo types**: Bool, Int, Float, String, Enum, Color, SoundData, Nullable, Array
- **Grid**: `scnEditor.instance.denominator` = 1/N beat snap. Alt+G opens GridSelect menu.
