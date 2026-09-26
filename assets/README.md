# Logo Procedure Pilot

- `logo-original.png` : image fournie par l’utilisateur, conservée sans modification.
- `app-symbol.png` : symbole adapté sur fond transparent avec l’outil intégré imagegen, utilisé dans l’en-tête et les icônes Windows.

`build.rs` prépare les résolutions 16, 20, 24, 32, 40, 48, 64, 128 et 256 pixels dans un fichier ICO, puis l’intègre à l’EXE. Le PNG utilisé par la fenêtre et l’en-tête est également intégré à la compilation. Aucun fichier image n’est requis à côté de l’EXE.

## Prompt de préparation (outil intégré, sans CLI)

Prepare the supplied existing Procedure Pilot logo symbol as an application icon asset. Precise extraction, NOT a redesign. Retain exactly the blue document sheet with folded cyan corner, three pale horizontal strokes, and overlapping blue/turquoise circular compass with directional arrow. Keep original proportions, shapes, colors, gradients and white circular inner face/separators unchanged. Remove ONLY the surrounding off-white background and the entire text wordmark 'procedure pilot' underneath. Output just this symbol centered on a square transparent canvas with a narrow 4% transparent margin on all sides, filling about 92% of the canvas. No added text, shadow, decorations or new elements. Preserve the opaque white details inside the compass.
