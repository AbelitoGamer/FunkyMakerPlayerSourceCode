== Funky Maker Player - Note Skins / Skins de Notas ==

────────────────────────────────────────────────────────────
ENGLISH
────────────────────────────────────────────────────────────
Each folder in "notestyles/" is one note skin. The folder name is the
name shown in Settings > Graphics > Note Style (with a live preview).

Two skins come with the game. Copy one and edit it as a starting point:
  "FMM (Default)/"  type Funky Maker (the default skin)
  "FMM (XML)/"      type Codename (the same look as an XML atlas)
Each has a skin.json listing every setting its type supports.
If you delete their files, the game puts back the missing ones on the
next start (your edits are kept).

A skin.json "noteSystem" picks the type: "fmm" = Funky Maker,
"atlas" (or no skin.json) = Codename.

── TYPE: FUNKY MAKER (like the original FunkyMaker Mobile) ──
Plain images. ONE up arrow that the game rotates and colours per lane.
  note.png       REQUIRED  white/grey UP arrow (used for notes AND strums)
  goodnote.png   optional  positive (O) notes
  badnote.png    optional  negative (X) notes
  holdtrail.png  optional  hold body (stretched)
  holdend.png    optional  hold end cap (empty rows are ignored)
skin.json settings (defaults shown; colours are left, down, up, right):
  "noteSystem":    "fmm"
  "noteColors":    ["#CA4E9D", "#00FFFE", "#10FA05", "#FC3844"]
  "pressedColors": ["#C576A8", "#4AEBEB", "#55E84F", "#E46E75"]
  "strumColor":    "#828282"
  "dropShadow":    true
  "noteScale" / "receptorScale": size (default: fits the lane)
  "noteAlpha": 1.0   "strumAlpha": 0.85   "holdAlpha": 0.84
  "tintNegative" / "tintPositive": colour of X / O notes without
                   their own image ([r, g, b])

── TYPE: CODENAME (PNG + XML atlas, Sparrow format) ──
One PNG + one XML (the PNG is found by having the same name as the XML).
Separate atlases also work: fmm.png/.xml or notes.png/.xml for the notes,
notefmm.png/.xml or noteStrumline.png/.xml for the strums.
Frame names (numbered 0000, 0001, ... for animations):
  Notes:   purple / blue / green / red          (checked first)
           or noteLeft / noteDown / noteUp / noteRight
  Strums:  staticLeft/Down/Up/Right            idle
           pressLeft/Down/Up/Right             key pressed, no note
           confirmLeft/Down/Up/Right           note hit
           confirmHoldLeft/... (optional, falls back to confirm)
  Holds:   purple hold piece / purple hold end (blue, green, red ...)
           or a NOTE_hold_FMM.png strip of 8 columns: body + end per lane
  X / O notes (optional): noteLeft1... (negative), noteLeft2... (positive)
Careful: a frame whose name starts with a colour (like "purple hold
piece") can be mistaken for a note. Name notes "purple0000" etc.
skin.json settings:
  "noteSystem": "atlas"
  "noteScale" / "receptorScale": size (default 0.65)
  "fps": [24, 24, 24, 24]   strum animation speed: static, press,
                            confirm, confirmHold
  "colorizeByLane": false   tint grey notes with the lane colours
  "noteAlpha": 1.0   "strumAlpha": 1.0   "holdAlpha": (renderer default)
  "tintNegative": [255, 50, 50]   "tintPositive": [0, 200, 255]
A single-atlas skin gets an auto-generated config.json. Delete it after
renaming frames so the game detects them again.

── ANY TYPE ──
Miss sounds: miss.ogg (or miss1, miss2 ... / missnote1 ...) in the skin
folder; .wav and .mp3 also work. With several, one plays at random.

────────────────────────────────────────────────────────────
ESPAÑOL
────────────────────────────────────────────────────────────
Cada carpeta de "notestyles/" es un skin. El nombre de la carpeta es el
que sale en Settings > Graphics > Note Style (con vista previa).

El juego trae dos skins. Copia uno y editalo para empezar:
  "FMM (Default)/"  tipo Funky Maker (el skin por defecto)
  "FMM (XML)/"      tipo Codename (el mismo aspecto como atlas XML)
Cada uno tiene un skin.json con todas las opciones de su tipo.
Si borras sus archivos, el juego repone los que falten al volver a
iniciarse (tus cambios se conservan).

En skin.json, "noteSystem" elige el tipo: "fmm" = Funky Maker,
"atlas" (o sin skin.json) = Codename.

TIPO FUNKY MAKER: imagenes sueltas, UNA flecha hacia arriba que el
juego gira y colorea. note.png (obligatoria), goodnote.png, badnote.png,
holdtrail.png, holdend.png.
TIPO CODENAME: un PNG + un XML (formato Sparrow).
Los nombres de frames y las opciones de skin.json son los de arriba.

Sonidos de fallo: miss.ogg (o miss1.ogg, miss2.ogg ...) en la carpeta
del skin. Tambien sirven .wav y .mp3.
