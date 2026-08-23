# Third-party notices

BiliSub Studio uses the following third-party components and data in its local AI/media pipeline. This file is distributed with the Windows application and exact-source archive.

## Piper TTS runtime

- Component: `piper-tts 1.4.2`
- Project/package: Piper text-to-speech runtime
- Use in BiliSub Studio: executed as an app-managed local process; no cloud inference is required.
- BiliSub Studio pins the Windows x64 wheel by exact version and SHA-256.
- License obligations are governed by the upstream Piper package/project license distributed by its maintainers.

## Vietnamese Piper voice: vi_VN-vais1000-medium

- Voice collection: `rhasspy/piper-voices`
- Exact source revision: `3d796cc2f2c884b3517c527507e084f7bb245aea`
- Voice path: `vi/vi_VN/vais1000/medium/vi_VN-vais1000-medium`
- Model collection license: MIT as declared by the upstream repository metadata.
- Training dataset: VAIS-1000.
- Dataset license: Creative Commons Attribution 4.0 International (CC BY 4.0).
- Original dataset/model attribution remains with the VAIS-1000 and Piper voice authors/contributors.
- BiliSub Studio does not claim authorship of the original voice model or dataset.

Pinned files used by BiliSub Studio:

- `vi_VN-vais1000-medium.onnx` — 63,201,294 bytes — SHA-256 `ec7c89e2c85f4d1edc24b6120c18aaf1bda614f06b511567eb9c7c0de15e2dab`
- `vi_VN-vais1000-medium.onnx.json` — 4,860 bytes — SHA-256 `fafb9da1354ed4b77c31af228ed41fb41cd825c14cffa105454b25e6ae751ee0`

BiliSub Studio exposes two routing profiles from this one licensed local voice model:

- `vais1000-female-profile-v1`: original Piper synthesis profile.
- `vais1000-male-profile-v1`: a deterministic synthetic acoustic profile created locally by lowering pitch approximately three semitones and compensating tempo before the normal timing-fit stage.

The male profile is a generated acoustic transformation for subtitle-voice routing. It is not presented as a recording or likeness of a real male speaker.

## NghiTTS reference

The open-source NghiTTS project was reviewed as an architectural/reference implementation for Vietnamese Piper-compatible TTS and text handling. BiliSub Studio does **not** embed its Vue/Vite application, WebView runtime, localhost server, or cloud inference path.

The previously evaluated `sannht/vi_voice` generic weights (`deepman3909` / `calmwoman3688`) are not downloaded or distributed by the production path because the reviewed weight index did not provide a sufficiently clear model-weight license for release.
