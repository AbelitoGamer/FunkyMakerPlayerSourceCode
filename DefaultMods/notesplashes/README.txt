== Funky Maker Player - Note Splashes / Splashes de notas ==

────────────────────────────────────────────────────────────
ENGLISH
────────────────────────────────────────────────────────────
Splashes appear on the strum when you hit a note with a "Sick!".

ADD A SPLASH:
1. Create a folder inside "notesplashes/" with any name.
   The folder name is the name shown in the game.
2. Put a PNG + XML atlas (Sparrow / Codename format) inside, e.g.
   noteSplashes.png + noteSplashes.xml
3. Optional: a splash.json with the settings below.
4. In the game: Settings > Graphics > Note Splash.

"FMM (Default)/" is the game's default splash. Use it as an example.
If you delete its files, the game puts back the missing ones on the
next start.

FRAME NAMES (per lane, numbered 0000, 0001, ...):
  noteLeft splash / noteDown splash / noteUp splash / noteRight splash
  Left splash / Down splash / ...
  purple splash / blue splash / green splash / red splash
  note impact 1 purple / note impact 2 purple / ...
  splash              (same animation for every lane)

splash.json (every setting is optional):
  "fps": 24               animation speed. The splash plays each frame
                          once, so it lasts frames / fps seconds.
  "scale": 1.0            size multiplier of the frames
  "alpha": 1.0            opacity (0 to 1)
  "fade": false           fade out while it plays
  "colorizeByLane": false tint with the lane colours
  "colors": ["#C576A8", "#4AEBEB", "#55E84F", "#E46E75"]
                          tint per lane (left, down, up, right).
                          Overrides colorizeByLane. Also accepts [r, g, b].
  "offsetX": [0, 0, 0, 0] position nudge per lane, in pixels
  "offsetY": [0, 0, 0, 0]

BUILT IN: "Simple" and the "Particles: ..." splashes are drawn by the
game itself. They have no files and can't be edited.

────────────────────────────────────────────────────────────
ESPAÑOL
────────────────────────────────────────────────────────────
Los splashes salen en el strum cuando aciertas una nota con "Sick!".

AÑADIR UN SPLASH:
1. Crea una carpeta dentro de "notesplashes/" con cualquier nombre.
   El nombre de la carpeta es el que se ve en el juego.
2. Pon dentro un atlas PNG + XML (formato Sparrow / Codename), p. ej.
   noteSplashes.png + noteSplashes.xml
3. Opcional: un splash.json con las opciones de arriba.
4. En el juego: Settings > Graphics > Note Splash.

"FMM (Default)/" es el splash por defecto del juego. Usalo como
ejemplo. Si borras sus archivos, el juego repone los que falten al
volver a iniciarse.

Nombres de frames y opciones de splash.json: los mismos de arriba.

INCLUIDOS: "Simple" y los splashes "Particles: ..." los dibuja el
propio juego. No tienen archivos y no se pueden editar.
