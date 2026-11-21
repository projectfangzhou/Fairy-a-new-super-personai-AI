# Third-Party Notices

This project includes the following third-party components. Their licenses apply to those components, not to the project's own MIT-licensed code.

## On-device embedding (PC)

- **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`, Microsoft) — MIT License. Unmodified NuGet package; its
  Windows native library runs the embedding model locally.
  See https://github.com/microsoft/onnxruntime/blob/main/LICENSE
- **Qwen3-Embedding-0.6B** (Alibaba Qwen) — Apache-2.0 License. NOT distributed with this repository or
  installer: the int8 ONNX export (`onnx/model_quantized.onnx`, ~585 MB) and its tokenizer files are
  downloaded into `Dependencies/qwen3-embedding/` on the machine that wants local retrieval. See
  https://huggingface.co/Qwen/Qwen3-Embedding-0.6B

## MIT License Components

- **Microsoft.Data.Sqlite / Microsoft.Maui / Microsoft.Extensions.*** — MIT License
- **CommunityToolkit.Mvvm** — MIT License
- **Markdig / Markdig.Wpf** — BSD-2-Clause License
- **Microsoft.Web.WebView2** — Microsoft proprietary (redistribution permitted)
- **SQLitePCLRaw** — Apache-2.0 License

## Update signing

- No third-party cryptography is used. Update manifests are signed and verified with **ECDSA P-256 over
  SHA-256** from `System.Security.Cryptography` (signature in IEEE P1363 form, public key `0x04‖X‖Y`).
  The private key is held offline by the release owner and is never in this repository.

## Apache-2.0 Components

- **SQLitePCLRaw** — Apache-2.0 License. See https://www.apache.org/licenses/LICENSE-2.0
- **UniDesktop API (UDA) SDK `uda_ffi` v0.2.1** — dual licensed **MIT OR Apache-2.0**, copyright the UniDesktop project (https://github.com/UniDesktop/SDK). The prebuilt `x86_64-pc-windows-gnu` shared library is redistributed unmodified under `Dependencies/uda/uda_ffi.dll`, together with its public header `uda.h`. Source archive `uda-ffi-v0.2.1-x86_64-pc-windows-gnu.zip` verified against the release's published `SHA256SUMS.txt` (`3e1655e7b5c20f8031c1ab2e58d3d19e2fc26ddb8c812c3fb2584285989dbe73`) before the DLL was extracted. No patches were applied to it.

## voidtools Everything SDK

- **Everything SDK** — MIT License. Original copyright belongs to voidtools (David Carpenter). Redistribution of the SDK is permitted with the copyright and license notice retained.
- **Everything.exe / es.exe** — Proprietary. NOT distributed with this installer. Users must install Everything separately. No part of Everything's binary is bundled.

## Fonts

- **OpenSans** — SIL Open Font License 1.1 (OFL-1.1). See https://opensource.org/licenses/OFL-1.1

## Other

- **Live2D Cubism Core** — Proprietary. Not bundled. Users must obtain their own licensed copy from Live2D Inc.

## Agent runtimes

- **@earendil-works/pi-coding-agent 0.84.3** — MIT License. Redistributed verbatim under `ThirdParty/PiAgent/` as the Fairy Code agent loop, together with its runtime-loader dependency `jiti 2.7.0` (MIT). Per-file SHA-256 is recorded in `ThirdParty/PiAgent/manifest.json`. See `ThirdParty/PiAgent/THIRD-PARTY-NOTICES.md` for the open item: the upstream bundle embeds its own transitive dependencies, and their per-package notices still have to be exported before a public stable release.

---

If you believe a component is missing from this list, please open an issue on the project's GitHub repository.
