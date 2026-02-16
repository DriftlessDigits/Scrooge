# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Dagobert is a Dalamud plugin for Final Fantasy XIV that automates market board price undercutting. It adjusts retainer listing prices to undercut the cheapest competing offer by a configurable amount (fixed gil or percentage).

- **SDK**: Dalamud.NET.Sdk 14.0.1
- **Framework**: .NET 10.0 (net10.0-windows7.0)
- **Language**: C#
- **License**: AGPL-3.0-or-later

## Build Commands

```bash
# Build (from repo root)
dotnet build Dagobert.sln -c Debug

# Release build
dotnet build Dagobert.sln -c Release
```

The build requires Dalamud libraries at `%appdata%\XIVLauncher\addon\Hooks\dev\` (configured in `Dalamud.Plugin.Bootstrap.targets`). No test project exists; testing is done manually in-game.

## Architecture

### Core Flow

```
Plugin (entry point, command registration, window management)
  ├── AutoPinch (Window) — orchestrates the automation
  │     ├── TaskManager — sequences game UI interactions with delays
  │     └── MarketBoardHandler — listens for price data, calculates undercuts
  ├── ConfigWindow (Window) — ImGui settings UI
  ├── Configuration — persisted settings (IPluginConfiguration)
  └── Communicator (static) — chat output with SeString/item payloads
```

**Price update flow**: AutoPinch enqueues tasks to navigate retainer UI → MarketBoardHandler receives market data via `IMarketBoard.OfferingsReceived` → calculates new price → fires `NewPriceReceived` event → AutoPinch sets the price on the RetainerSell addon.

### Key Patterns

**TaskManager (ECommons LegacyTaskManager)**: All game UI automation is sequenced through enqueued tasks. Each task returns `bool?` — `null` (in progress/retry), `true` (done), `false` (failed). Delays between tasks prevent race conditions with game UI state.

**Unsafe memory access**: Game addon nodes are accessed via `FFXIVClientStructs` pointers (e.g., `addon->UldManager.NodeList[27]`). Node indices are hardcoded and will break on game UI changes.

**AddonLifecycle listeners**: Used to detect when game UI elements (RetainerSell, ItemSearchResult, RetainerList, ContextMenu) appear or update. Registered in both AutoPinch and MarketBoardHandler.

**Undercut modes**: `FixedAmount` (subtract N gil), `Percentage` (reduce by N%), or `GentlemansMatch` (copy lowest price exactly). FixedAmount and Percentage clamp to minimum 1 gil. `MaxUndercutPercentage` skips items where the cut would be too aggressive. The ConfigWindow hides undercut amount and max % fields when GentlemansMatch is selected.

**Vendor price floor**: When enabled, items are skipped if the undercut price falls below `Item.PriceLow` (what a vendor pays you). Uses sentinel value `-2` to signal this case.

**Sentinel values in NewPrice**: `> 0` = valid price to set, `-1` = no MB listings found, `-2` = below vendor price floor. AutoPinch uses a switch statement in the else block of `SetNewPrice()` to dispatch error messages for each sentinel.

### Dependencies

- **ECommons** (git submodule): Provides TaskManager, AddonMaster wrappers, service container (`Svc`), ImGui helpers, Callback.Fire() for simulating UI clicks
- **FFXIVClientStructs**: Direct game memory access (RetainerManager, AtkUnitBase, addon structs)
- **Lumina**: Game data sheets (Item lookups)
- **System.Speech**: Windows TTS notifications

### Multi-language Support

Mannequin item detection checks context menu text in English, German, Japanese, and French. Any new context menu checks must handle all four languages.

## Code Style

- **Indent**: 2 spaces (configured in `.editorconfig`)
- **Documentation**: XML doc comments (`<summary>`, `<param>`, `<returns>`) on all public and internal methods
- **Readability over cleverness**: Prefer explicit loops and variables over one-liners

## Dalamud Documentation

Official Dalamud docs, API reference, and plugin development guides: https://dalamud.dev/

## CI/CD

Releases are triggered by pushing a `v*` tag. The GitHub Actions workflow delegates to a reusable workflow in `SHOEGAZEssb/DalamudPluginRepo` which builds, packages, and publishes to the plugin repository.
