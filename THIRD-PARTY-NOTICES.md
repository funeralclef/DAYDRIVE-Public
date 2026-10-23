# Third-party notices

DAYDRIVE uses or can download the following third-party components. Their
respective licenses remain in effect; no ownership is claimed by this project.

The native Linux edge host also uses the distribution's GTK 3, GLib, Cairo, Pango,
GDK Pixbuf, and gtk-layer-shell libraries. These system libraries are not bundled
in the application archive; their licenses and notices are supplied by the distribution.
Linux GPU detection can also use the system Vulkan loader and graphics drivers,
and the optional NVIDIA `nvidia-smi` utility; none are bundled by DAYDRIVE.

- Avalonia UI (including Avalonia.Controls.ColorPicker) and CommunityToolkit.Mvvm — MIT License.
- llama.cpp (`ggml-org/llama.cpp`) — MIT License. The app downloads an official
  release asset from GitHub when local AI is installed.
- Qwen3.5 model weights (`Qwen`) and the selected GGUF conversions (`unsloth`) —
  Apache License 2.0. Model files and the optional multimodal projector are
  downloaded from their Hugging Face repositories and are not bundled.
- PdfPig — Apache License 2.0. PdfPig contains additional notices for software
  derived from PDFBox; see the package and upstream repository for the complete
  notice text.
- Entity Framework Core and Microsoft.Extensions.DependencyInjection — MIT License.
- ModelContextProtocol C# SDK — MIT License.
- SkiaSharp — MIT License; bundled native Skia components retain their upstream
  BSD-style notices.
- SQLite and SQLitePCLRaw — SQLite public-domain dedication and Apache License 2.0
  components respectively.
- Phosphor Icons — MIT License; the bundled regular icon font is version 2.1.2.
- lucide-animated (`pqoqubbw/icons`) — MIT License. DAYDRIVE ports selected
  semantic hover animations and Lucide vector shapes to its native Avalonia icon control.
- Lucide icons — ISC License; vector shapes used by lucide-animated remain under
  the Lucide upstream license.
- Literata and Shantell Sans — SIL Open Font License 1.1. The font files
  and their license texts are bundled under `Assets/Fonts`.

Upstream license texts and notices:

- https://github.com/AvaloniaUI/Avalonia
- https://github.com/ggml-org/llama.cpp
- https://huggingface.co/Qwen/Qwen3.5-4B
- https://huggingface.co/unsloth/Qwen3.5-4B-GGUF
- https://github.com/UglyToad/PdfPig
- https://github.com/dotnet/efcore
- https://github.com/modelcontextprotocol/csharp-sdk
- https://github.com/mono/SkiaSharp
- https://www.sqlite.org/copyright.html
- https://github.com/phosphor-icons/web
- https://github.com/pqoqubbw/icons
- https://github.com/lucide-icons/lucide
- https://github.com/google/fonts/tree/main/ofl/literata
- https://github.com/google/fonts/tree/main/ofl/shantellsans

