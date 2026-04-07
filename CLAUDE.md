# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

RDMods is a Unity C# modding project for **Rhythm Doctor** that adds accessibility features to the level editor. The solution contains two projects that work together via IPC:

- **RDLevelEditorAccess** (.NET Standard 2.1): BepInEx mod that runs inside Unity, providing accessibility features and screen reader support
- **RDEventEditorHelper** (.NET Framework 4.8): Standalone WinForms application for editing event properties with an accessible UI

**Architecture**: The mod runs inside Unity's level editor and communicates with the helper via file-based IPC using `temp/source.json` and `temp/result.json`.

## Setup

Copy `Directory.Build.user.props.example` → `Directory.Build.user.props` and set `<GameDir>` to the Rhythm Doctor installation path. The build system auto-deploys outputs to the game directory.

## Build Commands

```bash
dotnet build RDLE-a11y.sln              # Debug (auto-deploys to GameDir)
dotnet build RDLE-a11y.sln -c Release
dotnet build RDLevelEditorAccess/RDLevelEditorAccess.csproj  # Individual project
dotnet build RDEventEditorHelper/RDEventEditorHelper.csproj
dotnet clean RDLE-a11y.sln
./release.sh                            # Build Release + package into release/main/
```

**Auto-deployment**: `Directory.Build.props` copies outputs on every build:
- Mod DLL → `{GameDir}/BepInEx/plugins/`
- Helper EXE → `{GameDir}/`

## Project Structure

```
RDLevelEditorAccess/
├── EditorAccess.cs           # BepInEx plugin entry, Harmony patches, AccessLogic MonoBehaviour (core, ~4000 lines)
├── AccessibilityModule.cs    # Public API (AccessibilityBridge) + UnityDispatcher
├── CustomUINavigator.cs      # Disables native UI navigation
├── InputFieldReader.cs       # Text-to-speech for input fields
└── IPC/
    └── FileIPC.cs            # File-based IPC with Helper

RDEventEditorHelper/
├── Program.cs                # Entry point, reads source.json, writes result.json
└── EditorForm.cs             # WinForms property editor UI

agents references/Assembly-CSharp/
└── RDLevelEditor/            # Decompiled game code (349 files)
    ├── scnEditor.cs          # Main editor controller (~4000 lines)
    ├── LevelEvent_Base.cs    # Base class for all events
    ├── LevelEventInfo.cs     # Event metadata system
    ├── BasePropertyInfo.cs   # Property type system
    └── InspectorPanel.cs     # Property panel base

docs/
├── manual-cn.md / manual-en.md        # User manuals (also .html versions)
├── changelog-cn.txt / changelog-en.txt
```

## Keyboard Shortcuts

The mod provides extensive keyboard navigation for accessibility:

| Shortcut | Function |
|----------|----------|
| **Insert** or **F2** | Add event at current timeline position |
| **Ctrl+Insert** or **Ctrl+F2** | Add row/sprite (context-dependent) |
| **Return** | Activate selected item / Open property editor |
| **Arrow Keys** | Navigate timeline / Move events |
| **Alt+Arrow** | Fine adjustment (0.01 beat) |
| **Shift+Arrow** | Medium adjustment (0.1 beat) |
| **Plain Arrow** | Coarse adjustment (1 beat) |
| **Tab** | Navigate UI elements in menus |

When `virtualMenuState != None`, arrow keys navigate virtual menus instead of the timeline.

## Key Architecture Concepts

### AccessLogic — the heart of the mod

`AccessLogic` (in `EditorAccess.cs`) is a MonoBehaviour injected into the scene. Its `Update()` dispatches to one of three mutually exclusive input handlers each frame:

- **`HandleGeneralUINavigation`** — active when a Unity UI menu is open; handles Tab/Arrow/Enter within Unity UI elements
- **`HandleTimelineNavigation`** — default handler; event selection, movement, insertion/deletion on the timeline
- **`HandleVirtualMenu`** — active when `virtualMenuState != None`; arrow keys drive a custom keyboard menu instead of the timeline

Key fields: `_editCursor` (BarAndBeat, current insert position), `virtualMenuState` (VirtualMenuState enum), `virtualMenuIndex`, `virtualSelection` (multi-event selection set).

### VirtualMenuState

`AccessLogic` uses a `VirtualMenuState` enum to track which virtual menu is active:

```csharp
private enum VirtualMenuState
{
    None,
    CharacterSelect,   // Adding row/sprite
    EventTypeSelect,   // Selecting event type
    LinkSelect,        // Selecting hyperlink target
    EventChainSelect,  // Selecting saved event chain (;)
    ConditionalSelect  // Browsing/toggling conditions on an event
}
```

### Game Code Reference

**CRITICAL**: Always check `agents references/Assembly-CSharp/` before modifying code. This folder contains decompiled game code that shows how the level editor works internally.

Key concepts from the game:
- **Tab system**: Song(0), Rows(1), Actions(2), Rooms(3), Sprites(4), Windows(5)
- **onlyUI properties**: Properties marked `onlyUI = true` are NOT saved to level files
- **PropertyInfo types**: Bool, Int, Float, String, Enum, Color, SoundData, Nullable, Array

### SoundData Panel Sentinel Values

The `CreateSoundDataPanel()` in `EditorForm.cs` uses special Tag values for non-file ListView items:

| Tag | Meaning | Serialized as |
|-----|---------|---------------|
| `"__track_default__"` | Use track default (nullable SoundData) | empty string `""` |
| `"__manual__"` | Manual filename input mode | value from `ManualInput` TextBox |

When handling ListView selection or sound preview, always guard against both sentinels.

### Localization

**Game built-in localization keys** are in `agents references/localization/` (`.bytes` files). Before creating a new `eam.*` key, always check whether the game already provides a key for that string. Use native keys when available; only create `eam.*` keys for mod-specific screen-reader text that has no equivalent in the game.

Examples of native key locations:
- Enum names: `Enums.bytes` — e.g. `enum.ConditionalType.Custom`
- Editor UI labels: `LevelEditor.bytes` — e.g. `editor.Conditionals.expression`
- Character names: `Enums.bytes` — e.g. `enum.Character.Ian.short`

When using native keys, call `RDString.Get(key)` (goes through `RDStringPatch`, supports `eam.*` injection) or `RDString.GetWithCheck(key, out bool exists)` (bypasses the patch — use for native keys when you need an existence check). Do **not** call `GetWithCheck` for `eam.*` custom keys, as the patch is not applied there.

### IPC Protocol

The mod and helper communicate via JSON files:

1. **Mod → temp/source.json**: Event type and properties
   ```json
   {
     "editType": "event",
     "eventType": "AddClassicBeat",
     "token": "unique-session-id",
     "properties": [
       { "name": "bar", "type": "Int", "value": "1" },
       { "name": "btn", "type": "Button", "methodName": "DoSomething" }
     ],
     "levelAudioFiles": ["song.ogg", "sfx.wav"]
   }
   ```
   - For row editing: `"editType": "row"` and `"eventType": "MakeRow"`
   - For level settings: `"editType": "settings"`
   - `levelAudioFiles`: Array of audio files in level directory (for SoundData properties)

2. **Mod launches** `RDEventEditorHelper.exe`

3. **Helper shows** WinForms editor

4. **Helper → temp/result.json**: User action
   - Save: `{ "token": "...", "action": "ok", "updates": { "bar": "2" } }`
   - Execute: `{ "token": "...", "action": "execute", "methodName": "DoSomething" }`
   - Cancel: `{ "token": "...", "action": "cancel" }`

5. **Mod polls** for result, applies changes, deletes result file

The `token` field is used to match responses to requests and prevent race conditions.

When the helper sends `action: "execute"`, the mod first looks for `methodName` on the `LevelEvent` object (reflection-based `ButtonAttribute` buttons), then falls back to calling it on the current `InspectorPanel` via `scnEditor.instance.inspectorPanelManager.GetCurrent()` (hardcoded panel buttons such as `BreakIntoOneshotBeats`).

Hardcoded panel buttons (not in the property system) are registered in the `HardcodedButtons` static dictionary in `FileIPC.cs` and appended to the property list during serialization with `type: "Button"`, identical in format to reflection-based buttons.

#### Dynamic UI Visibility System

The Helper supports dynamic property visibility via bidirectional IPC:

1. **Helper → temp/validateVisibility.json**: Request to evaluate `enableIf` condition
   ```json
   {
     "token": "...",
     "enableIfExpression": "rhythm == 'X'",
     "currentValues": { "rhythm": "X", "bar": "1" }
   }
   ```

2. **Mod → temp/validateVisibilityResponse.json**: Evaluation result
   ```json
   {
     "token": "...",
     "isVisible": true
   }
   ```

This allows properties to show/hide in real-time as the user edits, without losing focus. The mod announces visibility changes via low-priority screen reader notifications.

### AccessibilityBridge (Public API)

`AccessibilityBridge` in `AccessibilityModule.cs` is the entry point — do NOT call `FileIPC` directly:

```csharp
AccessibilityBridge.Initialize(gameObject);       // Call once on startup (from AccessLogic.Awake)
AccessibilityBridge.EditEvent(levelEvent);         // Open event property editor
AccessibilityBridge.EditRow(rowIndex);             // Open row property editor
AccessibilityBridge.EditSettings();               // Open level settings editor
AccessibilityBridge.CreateCondition(targetEvent); // Open condition creator (attaches to targetEvent)
AccessibilityBridge.EditCondition(localId);       // Open condition editor for existing condition
AccessibilityBridge.Update();                     // Called every frame from AccessLogic.Update()
AccessibilityBridge.IsEditing                     // True while Helper window is open
AccessibilityBridge.SetConditionalSavedCallback(Action<int> callback); // Notify when condition saved
```

### ModUtils Utilities

Static helper class in `EditorAccess.cs` with formatting and localization methods:

```csharp
ModUtils.eventNameI18n(LevelEvent_Base evt)      // Get localized event name
ModUtils.eventSelectI18n(LevelEvent_Base evt)    // Get selection announcement text
ModUtils.FormatBarAndBeat(BarAndBeat bb)         // Format bar/beat display
ModUtils.FormatBeat(float beat)                  // Format beat with smart rounding
```

### InputFieldReader

`InputFieldReader.cs` implements a sophisticated text-to-speech system for input fields:

- **State diffing**: Compares previous/current text and caret position
- **Character-by-character reading**: Announces typed/deleted characters
- **Caret movement**: Reads character at cursor when navigating
- **Password support**: Announces "星号" for password fields
- **Focus detection**: Prevents false announcements on focus changes

The reader monitors all TMP_InputField components and provides real-time feedback for screen reader users.

### SaveState Pattern

When modifying event or row properties programmatically, wrap changes in `SaveStateScope` to enable undo. `SaveStateScope` is a game-provided `IDisposable` that calls `scnEditor.SaveState` / commits on dispose:

```csharp
using (new SaveStateScope())
{
    levelEvent.someProperty = newValue;
}
```

If you also need to call `UpdateUIInternal()` on affected controls, do it **outside** the scope — UI updates must not be part of the saved state transaction.

### Unity + BepInEx Pattern

The mod uses a two-part initialization: `EditorAccess` (BepInEx plugin, applies Harmony patches in `Awake`) and `AccessLogic` (MonoBehaviour injected into scene, runs per-frame logic in `Update`). All Harmony patches use `[HarmonyPostfix]`. `RDStringPatch` intercepts `RDString.Get` to inject `eam.*` custom keys.

## Code Style Guidelines

- Chinese comments are standard; use XML docs for public APIs; use `[ModuleName]` prefix in log messages
- Private fields: camelCase or `_prefix`; methods/classes/properties: PascalCase
- Import groups (System → BepInEx/Harmony → Unity → RDLevelEditor), alphabetical within groups
- Use `using Button = UnityEngine.UI.Button;` / `using Button = System.Windows.Forms.Button;` for namespace conflicts
- Major section separators: `// ==================...`

## Unity-Specific Guidelines

### Null Checking (MANDATORY)

Unity objects can be "fake null" - always check before access:

```csharp
if (scnEditor.instance == null) return;
if (menuObj != null && menuObj.activeInHierarchy) { }
```

### Accessibility (Screen Reader Support)

Use the game's `Narration` class:

```csharp
Narration.Say("已选中按钮", NarrationCategory.Navigation);
Narration.Say("菜单项", NarrationCategory.Navigation,
              itemIndex: 2, itemsLength: 5,
              elementType: ElementType.Button);
```

## Debugging

- **Mod logs**: `{GameDir}/BepInEx/LogOutput.log`
- **Helper logs**: `{GameDir}/RDEventEditorHelper.log` (requires DEBUG mode enabled in code)
- **Manual Helper test**: Create `temp/source.json` and run `RDEventEditorHelper.exe` from `{GameDir}`

## Git Commit Messages

Use short Chinese descriptions: `添加 XX 功能` / `修复 XX 问题` / `重构 XX 模块`
