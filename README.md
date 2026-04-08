# Novalist AI Assistant Extension

An AI-powered extension for [Novalist](https://github.com/Drommedhar/novalist-official) that adds chat, story analysis, and scene statistics capabilities. Supports **LM Studio** and **GitHub Copilot CLI** as AI providers.

## Features

- **AI Chat** — Ask questions about your story directly from the sidebar. The AI has full context of your book, chapters, and codex entities.
- **Story Analysis** — Run AI-powered analysis on individual scenes or entire chapters to detect entity references, inconsistencies, and get writing suggestions.
- **Scene Statistics** — Get AI-generated statistics for your scenes including word frequency and entity usage.
- **Configurable Providers** — Choose between LM Studio (local LLMs) or GitHub Copilot CLI.
- **Customizable** — Fine-tune temperature, context length, frequency penalty, system prompt, and response language.
- **Localized** — Ships with English and German translations.

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

## Configuration

After installing, go to **Settings → AI / LLM** to configure:

1. **Enable AI features**
2. **Select a provider** (LM Studio or Copilot CLI)
3. **Configure connection** (base URL, model, API token)
4. **Test the connection**

## Requirements

- Novalist **0.0.1** or later
- [Novalist.Sdk](https://www.nuget.org/packages/Novalist.Sdk/) 2.0.1+

## License

[MIT](LICENSE)