# Novalist AI Assistant Extension

Bring the power of large language models directly into your Novalist writing workflow. The AI Assistant extension connects to a local or cloud AI provider and gives you two main tools: an **AI Chat** sidebar and a **Story Analysis** view — both fully aware of your book's chapters, scenes, and codex entities (characters, locations, items, lore, and custom types).

## What does this extension do?

### AI Chat

A chat panel that opens in the right sidebar. You can ask the AI anything about your story — brainstorm plot ideas, check character consistency, explore "what if" scenarios, or get writing feedback. The AI automatically receives your project's entity data (characters, locations, items, lore entries, and any custom entity types) as context, so its answers are grounded in your actual story world.

- Streaming responses with live preview while the AI generates its answer.
- Full conversation history within a session — the AI remembers earlier messages so you can have a natural back-and-forth.
- Support for models with a thinking/reasoning step (the thinking output is displayed in a collapsible section).
- One-click chat clearing to start a fresh conversation.
- A **Context** button that lets you say exactly what the model is told: this scene, the rest of the chapter, the outline, or particular characters. What you tick is what is sent — nothing is inferred, and if something had to be left out to fit the budget the panel says which.

### Editorial critique, in the margin

**Critique this scene** reads the open scene and leaves its notes where an editor would: as comments anchored to the sentences they are about, in the [Inbox](https://github.com/Drommedhar/novalist-official) alongside your own.

A panel of findings is easy to build and easy to ignore, because acting on one means holding a remark in your head, finding the sentence and deciding. A comment on the sentence is the same information at the point of use.

Two things make it safe:

- A remark becomes a **comment**, which changes nothing.
- A proposed wording becomes a **suggested edit**, which you take or turn down like any other. Nothing here rewrites a sentence you wrote — the SDK does not allow it to.

Proposing wordings is off unless you ask for it. Being told what is wrong and being told what to write instead are different invitations.

**Critique the whole book** does the same scene by scene. It is one model call per scene, so it reports as it goes and can be stopped.

#### Choose who is reading

One editor looking for everything at once returns the same shape of note on every scene, and half of it is always for a different day. So the read is a choice, and each one is its own command — searchable in the palette, bindable to a key:

- **Developmental editor** — whether the scene works. Who wants what, what is in the way, what has changed by the end. Nothing about sentences.
- **Line editor** — the sentences only. Telling where it should show, filter words, rhythm, words doing no work.
- **Voice** — whether everyone sounds like themselves and unlike each other, and whether the narration sounds like the point-of-view character rather than like you.
- **Continuity reader** — only contradictions. Facts, who knows what, details that change, time that does not add up.
- **First reader** — an ordinary reader, not an editor. Where they were confused, bored, unconvinced, or wanted to keep going, said plainly and in the first person.
- **Agent reading a submission** — the reasons somebody would stop: a slow start, a competent but undistinctive voice, a familiar premise handled familiarly.

Each reader is held to its own kinds of finding, so the line editor cannot wander into structure and the continuity reader cannot tell you a sentence is flat. The same scene read twice by two of them says two different things, and both are worth having.

The whole-book pass takes a reader too, and both commands accept it as an argument, so a script can ask for a continuity sweep without touching the palette.

### Build a story bible from the manuscript

For the writer who has 90,000 words and an empty Codex, which is a common way to arrive at Novalist. It reads the book and proposes entries for the people, places, things and lore it finds, drawn only from what the prose actually establishes.

It **proposes and does not create**. Every entry is shown with its summary and the scenes it came from, and you tick the ones worth keeping. That approval step matters more here than anywhere else: a bad pass over a whole novel would otherwise leave a hundred entries to delete one at a time. It is also where the proposals that are the same character under two names get merged, which no model does reliably.

Entries it creates carry an **Appears in** section, so you can get to the scene a claim came from instead of taking it on trust.

### Outline a book from a premise

Give it a premise and roughly how many chapters, and it builds the shape into your binder: chapters, scenes, a line of synopsis each, and named plot threads with the scenes assigned to them.

It writes **no prose**. What you get is a binder full of titled empty scenes with intent behind each one — which is what an outline is, and what you can then argue with, reorder and throw half of away. Chapters are appended, so running it on a book that already has chapters adds to it rather than replacing anything.

### Several wordings, not one

**Rewrite (three ways)** and **Brainstorm (three options)** hand their versions over as candidates for you to pick from, rather than pasting a numbered list into your manuscript.

Anything that replaces prose you already wrote arrives as a **suggested edit** rather than an overwrite. Generated text is wrong a fair amount of the time, and undo is a poor way to find that out.

### One section of a Codex entry at a time

The Wiki summary is regenerated whole or not at all, which is the wrong unit for how an entry actually gets filled in — the history is fine and the appearance needs another attempt.

Press the button on a section head in the Codex and this writes that section alone, from the same dossier the summary uses, with the section's own title as the instruction. Titles are the writer's words and the clearest statement of what belongs underneath, so "How they speak" gets an answer about how they speak, not a précis of the whole entry.

Press it again and the extension is told what the section says and that you did not want it, and asked for a genuinely different angle. A re-roll that cannot see what it is replacing hands back the same paragraph with the clauses in a different order.

Where the dossier is silent, so is the answer. An invented fact in a reference entry is worse than a short one, because it gets read later as something you decided.

### Your voice, described from your own prose

Every prompt used to say "match the existing voice", which asks the model to infer a style from the paragraph it happens to be holding. That works for rewriting a distinctive passage and fails everywhere it matters — continuing from a line of dialogue, describing a room, writing towards a beat. What comes back is competent house style, and you edit your own voice back into it every time.

**Build a profile from my prose** reads a spread of scenes from across the book and asks for a description of how you write: sentence length and how it varies, where the rhythm falls, how you punctuate and attribute dialogue, how much interiority there is, what your descriptions attend to and what they skip. It samples across the book rather than the opening, because an opening is the most rewritten thing a writer owns and the least like the rest of them.

From then on it is appended to every prompt that writes prose, and it is told to outrank any general sense of good style — where the two disagree, the model follows you. Pick **No profile** and nothing changes. You can build more than one and switch, which is the honest answer for a writer whose thriller and whose children's book do not sound alike.

The profile is a description, not your text: it is derived once and stored as prose you can read.

### Your own prompts

Every built-in prompt here is somebody's opinion about what to say to a model, and that opinion is often wrong for a particular book — a prompt tuned for a thriller is not the one a literary novelist wants.

**Settings → AI / LLM → Your own prompts** holds prompts you write, and they appear in the same editor menu as the built-in ones. The template language is deliberately tiny — substitution and nothing else:

| Placeholder | Becomes |
| --- | --- |
| `{{selection}}` | The highlighted text. |
| `{{preceding}}` | The prose before the caret. |
| `{{directive}}` | What you typed after the slash. |
| `{{scene}}` / `{{chapter}}` | The scene's and chapter's titles. |
| `{{synopsis}}` | The scene's synopsis. |
| `{{pov}}` | Whose point of view the scene is in. |
| `{{characters}}` | The cast, one per line. |
| `{{context}}` | Codex entries this scene is allowed to send. |

A placeholder that is not one of these is left exactly as you typed it, so a stray `{{tone}}` comes back visibly rather than disappearing.

Each prompt says where its answer goes (replace the selection, insert after it, or insert at the caret), whether it works with nothing selected, and how many versions to ask for. Ask for more than one and you get a picker.

`{{context}}` goes through the same inclusion rules as everything else: a prompt you wrote does not get more access to your Codex than one we wrote.

### What the model is told

One place decides what goes into a request, in what order, and what gets cut when it does not fit. The order is not stylistic — models attend most reliably to the start and end of a long context, so the instruction leads and the passage being worked on comes last, with background in the middle where inattention costs least.

When there is not room, the least important thing is dropped rather than whatever happened to be at the bottom, and you are told what went. The budget is set in **Settings → AI / LLM**.

**Preview** in the chat shows exactly what would be sent, before a token is spent on it: every block that would go, what kind it is, roughly how many tokens it costs, and whether it fitted — with the assembled prompt itself underneath. The prompt used to be built and sent in one step and nothing showed what went, so ticking six characters and getting an answer that ignored two had no explanation.

**Model** beside it picks which model answers *this* request. The model lived in the settings form and nowhere else, so trying a heavier one for a single hard paragraph meant opening Settings, changing it, generating, and changing it back — which nobody does, so nobody tries. The choice lasts for the session and is dropped the moment you change the model in Settings, since that is you saying what the default should be.

Token figures are estimates. They come from the same four-characters-per-token rule the budget is enforced with, which is wrong for any particular string and close enough to decide what to drop; they are not a billing figure.

### Story Analysis

A full content view that lets you run AI-powered analysis on a chapter or your entire story. Select a chapter, hit **Analyse Chapter** (or **Analyse Whole Story**), and the AI processes each scene individually. The results include:

- **Entity reference detection** — Finds mentions of your codex entities (characters, locations, items, lore) inside the scene text, even when they are referred to indirectly.
- **Inconsistency detection** — Flags potential continuity errors, contradictions, or factual mismatches between scenes and your codex.
- **Writing suggestions** — Proposes new entities (characters, locations, items) that appear in the text but aren't in your codex yet.
- **Scene statistics** — AI-generated stats for each scene such as word frequency and entity usage.

Results are displayed as filterable findings you can browse by type or scene.

### Settings & Customization

All AI options are available under **Settings → AI / LLM**:

- **Provider selection** — Choose between:
  - **LM Studio / OpenAI-compatible** — any service that speaks the OpenAI chat protocol. An **Endpoint preset** drop-down fills in the address for LM Studio, Ollama, OpenAI, OpenRouter, Groq, DeepSeek, Mistral, Together and xAI, so you do not have to look the URL up. You can still type an address by hand for anything not listed.
  - **Anthropic** — calls the Messages API directly with your own API key. Available models are fetched from the API rather than hardcoded, so a newly released model shows up without an extension update.
  - **GitHub Copilot CLI** or **Claude CLI** — drives the command-line tool as a subprocess.
- **Model management** — Browse and select from loaded models, refresh the model list, and test your connection.
- **Generation parameters** — Temperature, top-P, min-P, context length, frequency penalty, and repeat-last-N. These apply to the OpenAI-compatible providers. They are deliberately **not** sent to Anthropic: current Claude models reject them, so the request would fail rather than being merely ignored.
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