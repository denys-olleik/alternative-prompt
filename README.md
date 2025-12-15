## Documentation
Sends the content of `prompt.md` as a request and appends the response to the end of the file with delimiter and model used.

## How to use

1. Compose your prompt in `prompt.md`.
2. Run the executable in the same folder (the app reads `prompt.md` from the current working directory).

## CLI (current code)

First argument must be the model name, then optional flags.

Examples:
- `llm.exe gpt-4.1-mini-2025-04-14`
- `llm.exe gpt-5.2-2025-12-11 -e xhigh`
- `llm.exe gpt-5.1-2025-11-13 -v high -e none --images --archive --audio`

All options:
- `<model>`: required first argument. Supported:
  - `gpt-4.1-2025-04-14`
  - `gpt-4.1-mini-2025-04-14`
  - `gpt-5-2025-08-07`
  - `gpt-5.1-2025-11-13`
  - `gpt-5.2-2025-12-11`
  - `gpt-4o-mini-tts` (not intended for chat requests)
- `--images`: attach all PNGs from `./images` as `input_image` parts; on success, moves them to `./image-archive`.
- `--archive`: archive the full conversation to `./archive/promptN.md` and reset `prompt.md` to only the last response block.
- `-t <float>`: temperature. If omitted for GPT‑4.1 family, defaults to `0.1`. Otherwise omitted unless explicitly set.
- `-v <low|medium|high>`: sets `text.verbosity` (passed to `/v1/responses`).
- `-e <none|minimal|low|medium|high|xhigh>`: reasoning effort (validated per-model):
  - GPT‑5 (`gpt-5-2025-08-07`): `minimal|low|medium|high`
  - GPT‑5.1 (`gpt-5.1-2025-11-13`): `none|low|medium|high`
  - GPT‑5.2 (`gpt-5.2-2025-12-11`): `none|low|medium|high|xhigh`
  - GPT‑4.1 family: `minimal|low|medium|high`
- `--audio [text]`: also generate TTS WAV via `/v1/audio/speech` using `gpt-4o-mini-tts`, writing `output.wav` (and archiving any existing `output.wav` to `./audio-archive/outputN.wav`). If `[text]` is omitted, uses the model response text.

Notes:
- Chat requests use `https://api.openai.com/v1/responses`.
- TTS requests use `https://api.openai.com/v1/audio/speech`.
- API key must be present in environment variable `openai`.

Requirements:
- API key must be set in environment variable `openai` (the app reads `Environment.GetEnvironmentVariable("openai")`).
