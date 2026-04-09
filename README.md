# Novalist AI Assistant Extension

Bring the power of large language models directly into your Novalist writing workflow. The AI Assistant extension connects to a local or cloud AI provider and gives you two main tools: an **AI Chat** sidebar and a **Story Analysis** view — both fully aware of your book's chapters, scenes, and codex entities (characters, locations, items, lore, and custom types).

## What does this extension do?

### AI Chat

A chat panel that opens in the right sidebar. You can ask the AI anything about your story — brainstorm plot ideas, check character consistency, explore "what if" scenarios, or get writing feedback. The AI automatically receives your project's entity data (characters, locations, items, lore entries, and any custom entity types) as context, so its answers are grounded in your actual story world.

- Streaming responses with live preview while the AI generates its answer.
- Full conversation history within a session — the AI remembers earlier messages so you can have a natural back-and-forth.
- Support for models with a thinking/reasoning step (the thinking output is displayed in a collapsible section).
- One-click chat clearing to start a fresh conversation.

### Story Analysis

A full content view that lets you run AI-powered analysis on a chapter or your entire story. Select a chapter, hit **Analyse Chapter** (or **Analyse Whole Story**), and the AI processes each scene individually. The results include:

- **Entity reference detection** — Finds mentions of your codex entities (characters, locations, items, lore) inside the scene text, even when they are referred to indirectly.
- **Inconsistency detection** — Flags potential continuity errors, contradictions, or factual mismatches between scenes and your codex.
- **Writing suggestions** — Proposes new entities (characters, locations, items) that appear in the text but aren't in your codex yet.
- **Scene statistics** — AI-generated stats for each scene such as word frequency and entity usage.

Results are displayed as filterable findings you can browse by type or scene.

### Settings & Customization

All AI options are available under **Settings → AI / LLM**:

- **Provider selection** — Choose between **LM Studio** (local LLMs on your machine) or **GitHub Copilot CLI** (cloud).
- **Model management** — Browse and select from loaded models, refresh the model list, and test your connection.
- **Generation parameters** — Temperature, top-P, min-P, context length, frequency penalty, and repeat-last-N.
- **Analysis checks** — Toggle which checks run during story analysis (entity references, inconsistencies, suggestions, scene stats).
- **Custom system prompt** — Override the default system prompt sent to the AI. Use `{{LANGUAGE}}` as a placeholder for the current UI language.
- **Response language** — Force the AI to respond in a specific language regardless of the UI language.

### Localization

The extension ships with English and German translations. The UI language follows your Novalist language setting automatically.

## Installation

### From the Extension Store (recommended)

1. Open Novalist and go to **Extensions → Browse Store**.
2. Find **AI Assistant** and click **Install**.

### Manual Installation

1. Download the latest `com.novalist.ai.zip` from [Releases](https://github.com/Drommedhar/novalist-aiassistant/releases).
2. Extract the ZIP into your Novalist extensions directory:
   - **Windows:** `%APPDATA%\Novalist\Extensions\com.novalist.ai\`
   - **macOS:** `~/Library/Application Support/Novalist/Extensions/com.novalist.ai/`
   - **Linux:** `~/.config/Novalist/Extensions/com.novalist.ai/`
3. Restart Novalist.

## Building from Source

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
- [Novalist.Sdk](https://www.nuget.org/packages/Novalist.Sdk/) (pulled automatically via NuGet)

### Build

```bash
dotnet build -c Release
```

The build output will be automatically deployed to your local Novalist extensions folder.

## Requirements

- Novalist **1.5.0** or later

## License

[MIT](LICENSE)