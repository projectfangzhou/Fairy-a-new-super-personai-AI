# Third-Party Notices

This project includes the following third-party components. Their licenses apply to those components, not to the project's own MIT-licensed code.

## MIT License Components

- **Microsoft.Data.Sqlite / Microsoft.Maui / Microsoft.Extensions.*** — MIT License
- **CommunityToolkit.Mvvm** — MIT License
- **Markdig / Markdig.Wpf** — BSD-2-Clause License
- **Microsoft.Web.WebView2** — Microsoft proprietary (redistribution permitted)
- **SQLitePCLRaw** — Apache-2.0 License

## On-device embedding

- **ONNX Runtime** (`Microsoft.ML.OnnxRuntime`, Microsoft) — MIT License. Unmodified NuGet package; its
  Android native libraries are what run the embedding model on the phone.
  See https://github.com/microsoft/onnxruntime/blob/main/LICENSE
- **bge-small-zh-v1.5** (BAAI) — MIT License. Copyright Beijing Academy of Artificial Intelligence.
  The int8 ONNX export published in `Xenova/bge-small-zh-v1.5` is bundled unmodified at
  `Resources/Raw/embedding/bge-small-zh-v1.5-int8.onnx` together with its WordPiece `vocab.txt`.
  It runs entirely on the device and turns document passages into 512-dimension vectors for retrieval.
  See https://huggingface.co/BAAI/bge-small-zh-v1.5

## Update signing

- No third-party cryptography is used. Update manifests are signed and verified with **ECDSA P-256 over
  SHA-256** from `System.Security.Cryptography` (signature in IEEE P1363 form, public key `0x04‖X‖Y`).
  The private key is held offline by the release owner and is never in this repository.

## Apache-2.0 Components
- **SQLitePCLRaw** — Apache-2.0 License. See https://www.apache.org/licenses/LICENSE-2.0
- **Shizuku client library** (`dev.rikka.shizuku:api`, `:provider`, `:aidl` 13.1.5, RikkaApps) — Apache-2.0 License.
  Copyright RikkaApps. Bound unmodified from the published AARs in `Dependencies/shizuku/`; the binding lives in
  `Dependencies/ShizukuApi/` and is used only to request the user's authorization and to run the fixed shell
  commands listed in `Platforms/Android/AndroidShizukuBridge.cs`. See https://github.com/RikkaApps/Shizuku

## voidtools Everything SDK

- **Everything SDK** — MIT License. Original copyright belongs to voidtools (David Carpenter). Redistribution of the SDK is permitted with the copyright and license notice retained.
- **Everything.exe / es.exe** — Proprietary. NOT distributed with this installer. Users must install Everything separately. No part of Everything's binary is bundled.

## Fonts

- **OpenSans** — SIL Open Font License 1.1 (OFL-1.1). See https://opensource.org/licenses/OFL-1.1

## Other

- **Live2D Cubism Core** — Proprietary. Not bundled. Users must obtain their own licensed copy from Live2D Inc.

---

If you believe a component is missing from this list, please open an issue on the project's GitHub repository.
