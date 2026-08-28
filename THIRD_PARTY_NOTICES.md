# Third-party notices

BiliSub Studio's Voice/TTS path uses these upstream components locally.
This notice accompanies the app; no authorship of upstream software or voices is claimed.

## Piper and Vietnamese normalization

- [Piper](https://github.com/OHF-Voice/piper1-gpl): piper-tts 1.7.0, local Python API.
  The upstream GPL license and package notices govern its use.
- [ONNX Runtime](https://github.com/microsoft/onnxruntime): 1.22.1.
- [NumPy](https://numpy.org): 2.5.2.
- [vietnormalizer](https://pypi.org/project/vietnormalizer/0.2.3/): 0.2.3.
- [gdown](https://github.com/wkentaro/gdown): 6.1.0, used only to retrieve the pinned
  public Drive artifacts, including Drive confirmation handling.

Direct runtime versions are pinned; this notice does not claim that all transitive
package distributions are locked by SHA-256.

## NGHI Ngọc Huyền voice

Source: [nghimestudio/nghitts](https://github.com/nghimestudio/nghitts) and its
[official model folder](https://drive.google.com/drive/folders/1f_pCpvgqfvO4fdNKM7WS4zTuXC0HBskL).
Upstream voice/model attribution remains with NGHI and its contributors.
BiliSub uses the exact Ngọc Huyền ONNX/config files, not NGHI's browser application.

- Model: ngochuyen.onnx, 63,516,050 bytes,
  SHA-256 2140977786d76d834736c059dacfa553d4931dac2b2c7aaaea438bb2aa9da697.
- Config: ngochuyen.onnx.json, 4,855 bytes,
  SHA-256 971f57f8d504223fee5b40d664f503cf769baf7db21f7d2ae0554a75d07de2f8.
- Canonical voice: ngoc_huyen, 22,050 Hz, mono.
- Full source/download provenance is recorded in docs/engineering/EDITOR_NGHITTS_AUDIT.md.

The current Voice task is local implementation and validation, not a release or a
new legal clearance for redistribution. Upstream license/voice-use obligations
must be reviewed before any later publication.

Previous VAIS synthetic male/female routes and Kokoro are not production TTS paths.
