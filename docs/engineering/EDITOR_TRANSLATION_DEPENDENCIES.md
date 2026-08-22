# Editor local translation dependencies

This file records the reviewed, pinned dependencies used by the Editor SRT translation path. It is evidence for source and release review; the model/runtime remain optional downloads and are not embedded in the installer.

| Component | Pin | Download size | SHA-256 | License/source |
|---|---:|---:|---|---|
| Qwen3-8B Q4_K_M GGUF | Hugging Face commit `7c41481f57cb95916b40956ab2f0b139b296d974` | 5,027,783,488 bytes | `d98cdcbd03e17ce47681435b5150e34c1417f50b5c0019dd560e4882c5745785` | Qwen official model repository, Apache-2.0 |
| llama.cpp Windows x64 Vulkan | release `b10566` / stable line `v0.2.0` | 34,937,857 bytes | `68e15a0a0d07df55a695ec4d81465cf57400431d54ae19fadcb51dc919724042` | `ggml-org/llama.cpp`; license files are retained from the verified runtime ZIP |
| Dịch Trung Tu Tiên skill | user-supplied reviewed ZIP | 32,005 bytes | `2969340edd47d3d860fc2bd7b4e0211723d5b8cad6a670d44dac707243e18213` | exact supplied content; no modification |

Runtime contract:

- downloads are commit/release pinned and must match both exact byte length and SHA-256 before promotion;
- partial model/runtime downloads are resumable but never treated as ready;
- ZIP extraction is entry-count, expanded-size and traversal bounded;
- inference uses an app-owned `llama-cli` child over local files/stdout; no HTTP listener or fixed port;
- model/runtime/checkpoints live only in app-owned `Tools/Translation` and `Data/Projects/Translation` roots;
- cancellation reaps the child process and preserves only completed translation batches.
