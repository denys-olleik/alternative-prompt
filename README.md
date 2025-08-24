## How to use

1. Compose your prompt in `prompt.md`.
2. Run the `.exe` in the same folder.

All options
- -r: use reasoning model (gpt-5-2025-08-07). Ignores -m. Temperature ignored.
- -m: use mini model (gpt-4.1-mini-2025-04-14).
- --images: attach all PNGs from ./images as image_url parts; on success, moves them to ./image-archive.
- -t <float>: temperature (non-reasoning models only). Default 0.1.
- -v <low|medium|high>: verbosity (reasoning models only).
- -e <minimal|low|medium|high>: reasoning_effort (reasoning models only).
- --audio [text]: also generate TTS WAV via gpt-4o-mini-tts.

Common combinations:
mini: `.\Prompt.exe -m
reasoning: `.\Prompt.exe -r -v high -e minimal`
with audio: `.\Prompt.exe -r -v high -e minimal --audio`

NOTE: GPT5 removes temperature and adds verbosity and reasoning flags.
