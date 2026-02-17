# AGENTS.md - Development Guidelines for RDMods

This file provides guidelines for AI agents working on this codebase.

## Project Overview

This is a Unity C# modding project for the game "Rhythm Doctor". The main project is `RDLevelEditorAccess`, a BepInEx mod that provides accessibility features for the level editor.

## Game Code Reference

**Important**: This repository includes decompiled game code in `agents references/` folder for AI agents to reference when writing mods. Before starting any work, always check this folder first to understand the game's existing code.

Key reference areas:
- `agents references/Assembly-CSharp/RDLevelEditor/` - Level editor code
- `agents references/Assembly-CSharp/Narration.cs` - Accessibility narration module

## Technology Stack

- **Language**: C# (.NET Standard 2.1)
- **Mod Framework**: BepInEx
- **Patching**: HarmonyLib (0Harmony)
- **Target Game**: Rhythm Doctor
- **IDE**: Visual Studio

## Build Commands

### Building the Project

```bash
# Build the solution (Debug configuration)
dotnet build RDMods.sln

# Build Release configuration
dotnet build RDMods.sln -c Release

# Build a specific project
dotnet build RDLevelEditorAccess/RDLevelEditorAccess.csproj
```

### Running Tests

This project does not currently have a test suite. To add tests:
1. Create a separate test project (e.g., xUnit or NUnit)
2. Add reference to RDLevelEditorAccess
3. Run with: `dotnet test`

To run a single test when tests are added:
```bash
dotnet test --filter "FullyQualifiedName~TestClassName.TestMethodName"
```

### Linting/Code Analysis

- Use Roslyn analyzers if available
- Visual Studio provides IntelliSense and built-in code analysis
- Enable `Treat warnings as errors` in project settings for production builds

### Version Control

- **Always commit after completing a task chain**: Run `git add . && git commit -m "简短中文描述"`
- Write commit messages in Chinese summarizing what was done

### Deployment

The project includes `Directory.Build.props` which automatically copies the built DLL to the game's BepInEx plugins folder after a successful build:

```bash
# Game folder (configured in Directory.Build.props)
# Default: D:\SteamLibrary\steamapps\common\Rhythm Doctor\BepInEx\plugins
```

The build output is automatically deployed to the mod directory.

## Code Style Guidelines

### General Principles

- Write clear, readable code over clever one-liners
- Keep methods focused and single-purpose
- Document complex logic with comments
- Follow existing codebase conventions

### Naming Conventions

| Element | Convention | Example |
|---------|------------|---------|
| Classes/Types | PascalCase | `EditorAccess`, `AccessLogic` |
| Methods | PascalCase | `HandleGeneralUINavigation` |
| Properties | PascalCase | `CurrentIndex`, `TargetEventSystem` |
| Private fields | camelCase | `allControls`, `inputFieldReader` |
| Constants | PascalCase | `MaxEventCount` |
| Parameters | camelCase | `menuName`, `rootObject` |
| Unity Components | Follow game conventions | Use existing class names from Assembly-CSharp |

### File Organization

```
RDLevelEditorAccess/
  ├── EditorAccess.cs       # Main plugin entry point
  ├── AccessLogic.cs        # Core mod logic
  ├── CustomUINavigator.cs # UI navigation utilities
  ├── AccessibilityModule.cs # WinForms bridge for accessibility
  └── InputFieldReader.cs  # Input field handling
```

### Import Ordering

Order imports alphabetically within groups:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

using BepInEx;
using HarmonyLib;

using UnityEngine;
using UnityEngine.UI;
using UnityEngine.EventSystems;

using RDLevelEditor;
```

### Unity-Specific Guidelines

#### MonoBehaviour Lifecycle

- Use `Awake()` for initialization
- Use `Update()` for per-frame logic
- Use `OnDestroy()` for cleanup
- Always check for null before using Unity objects

```csharp
public class AccessLogic : MonoBehaviour
{
    private void Awake()
    {
        Instance = this;
        Debug.Log("Logic initialized");
    }

    private void Update()
    {
        if (scnEditor.instance == null) return;
        // Handle input and game logic
    }

    private void OnDestroy()
    {
        if (Instance == this) Instance = null;
    }
}
```

#### Null Checking

Always check for null before accessing Unity objects:

```csharp
// Good
if (scnEditor.instance == null) return;
if (menuObj != null && menuObj.activeInHierarchy) { }

// Bad - may cause NullReferenceException
var items = rootObject.GetComponentsInChildren<Graphic>();
```

### Harmony Patching

Use Harmony attributes for patching:

```csharp
[HarmonyPatch(typeof(scnEditor))]
public static class EditorPatch
{
    [HarmonyPatch("SelectEventControl")]
    [HarmonyPostfix]
    public static void SelectEventControlPostfix(LevelEventControl_Base newControl)
    {
        // Handle post-patch logic
    }
}
```

### Error Handling

- Use try-catch for operations that may fail
- Log errors with appropriate severity:

```csharp
try
{
    // Risky operation
}
catch (Exception ex)
{
    Debug.LogError($"Error description: {ex.Message}");
    Debug.LogException(ex);  // Full stack trace
}
```

### Threading

- Unity operations must run on the main thread
- Use dispatchers for cross-thread communication:

```csharp
public class UnityDispatcher : MonoBehaviour
{
    private readonly ConcurrentQueue<Action> _queue = new ConcurrentQueue<Action>();

    private void Update()
    {
        while (_queue.TryDequeue(out var action))
        {
            try { action(); }
            catch (Exception e) { Debug.LogError($"Error: {e}"); }
        }
    }

    public void Enqueue(Action action) => _queue.Enqueue(action);
}
```

### Debug Logging

Use appropriate log levels:

```csharp
Debug.Log("Information message");           // General info
Debug.LogWarning("Warning message");        // Warnings
Debug.LogError("Error message");            // Errors
Debug.LogException(ex);                     // Exceptions with stack trace
```

### Code Comments

- Use XML documentation for public APIs
- Use inline comments for complex logic
- Chinese comments are used in this codebase (consistent with existing code)

```csharp
/// <summary>
/// Checks if a menu is active and navigates to it
/// </summary>
/// <param name="menuObj">The menu GameObject to check</param>
/// <param name="name">Display name for narration</param>
/// <returns>True if menu was intercepted</returns>
private bool CheckAndNavigate(GameObject menuObj, string name)
{
    // Check if menu exists and is visible
    if (menuObj != null && menuObj.activeInHierarchy)
    {
        HandleGeneralUINavigation(menuObj, name);
        return true;
    }
    return false;
}
```

### Region Blocks

Use regions to organize code sections (as used in this codebase):

```csharp
// ===================================================================================
// 第一部分：加载器 (Loader)
// ===================================================================================
[Code here]

// ===================================================================================
// 第二部分：核心逻辑 (Worker)
// ===================================================================================
[Code here]
```

### Unity API Usage

- Use `GetComponent<T>()` for getting components
- Use `GetComponentsInChildren<T>()` for getting child components
- Cache component references when possible
- Use Unity's built-in event systems where applicable

```csharp
// Get component
var button = element.GetComponent<Button>();

// Get all children components
var allControls = rootObject.GetComponentsInChildren<Graphic>()
    .Where(g => g.gameObject.activeInHierarchy)
    .ToList();

// Check for component existence
if (selectableComponent != null && es != null)
{
    selectableComponent.Select();
}
```

### WinForms Integration

When using Windows Forms (as in AccessibilityModule.cs):
- Always use fully qualified names or aliases for conflicting types
- Run WinForms on a separate STA thread
- Use Invoke for cross-thread communication

```csharp
using Button = System.Windows.Forms.Button;
using Control = System.Windows.Forms.Control;
using Form = System.Windows.Forms.Form;

private static void WinFormLoop()
{
    Application.EnableVisualStyles();
    _activeForm = new EditorForm();
    Application.Run(_activeForm);
}
```

## Important Notes

1. **Game Assembly References**: The project references game DLLs from Steam library path. These must exist for the project to build.

2. **Decompiled Game Code**: The `agents references/` folder contains decompiled game code. Always check this folder first when you need to understand game APIs, especially for:
   - Level editor functionality (`RDLevelEditor/`)
   - Accessibility features (`Narration.cs`)

3. **Auto-Deployment**: Built DLLs are automatically copied to the game's BepInEx/plugins folder via Directory.Build.props.

4. **Target Framework**: The project uses netstandard2.1 for compatibility with Unity's IL2CPP scripting backend.

5. **No Existing Tests**: This project currently has no test suite. Consider adding tests for core functionality.

6. **Harmony Patching**: When adding new patches, ensure they are registered in the plugin's Awake method:

```csharp
var harmony = new Harmony("com.your-mod-id");
harmony.PatchAll();
```
