═══════════════════════════════════════════════════════════════════════════════
  NOTESPLASHES - Custom Note Splash Effects
═══════════════════════════════════════════════════════════════════════════════

Esta carpeta es para ESTILOS PERSONALIZADOS de notesplash (efectos al hacer Sick!).

═══════════════════════════════════════════════════════════════════════════════
  CÓMO USAR
═══════════════════════════════════════════════════════════════════════════════

1. Crea una SUBCARPETA con el nombre de tu estilo:
   Ejemplo: notesplashes/MiEstilo/

2. Dentro de esa carpeta, añade:
   
   REQUERIDO (estándar FNF):
   - noteSplashes.png   (spritesheet con los frames de animación)
   - noteSplashes.xml   (archivo XML con la info de frames)
   
   OPCIONAL (extra, no estándar):
   - splash.json        (configuración avanzada: FPS, duración, escala, etc.)
                        NO NECESARIO si copias de un mod FNF existente

3. En el juego:
   - Abre Settings (ESC durante gameplay)
   - Navega a "Note Splash: Default"
   - Presiona ENTER
   - Selecciona tu estilo
   - Presiona ENTER para aplicar

═══════════════════════════════════════════════════════════════════════════════
  ESTRUCTURA DE EJEMPLO
═══════════════════════════════════════════════════════════════════════════════

notesplashes/
├── README.txt              (este archivo)
└── MiEstilo/               (tu carpeta personalizada)
    ├── noteSplashes.png    (textura) ← REQUERIDO
    ├── noteSplashes.xml    (atlas)   ← REQUERIDO
    └── splash.json         (config)  ← OPCIONAL (extra, no estándar FNF)

NOTA: FNF solo usa PNG + XML. El JSON es opcional para configuración avanzada.

═══════════════════════════════════════════════════════════════════════════════
  NOMBRES DE ARCHIVO ACEPTADOS
═══════════════════════════════════════════════════════════════════════════════

PNG + XML (usa el mismo nombre base):
  - noteSplashes.png / noteSplashes.xml  (preferido)
  - splash.png / splash.xml
  - notesplash.png / notesplash.xml

═══════════════════════════════════════════════════════════════════════════════
  NOMBRES DE FRAMES EN XML
═══════════════════════════════════════════════════════════════════════════════

El juego busca frames con estos nombres (por lane):

OPCIÓN 1 - Estándar FNF:
  noteLeft splash0000, noteLeft splash0001, noteLeft splash0002, ...
  noteDown splash0000, noteDown splash0001, ...
  noteUp splash0000, noteUp splash0001, ...
  noteRight splash0000, noteRight splash0001, ...

OPCIÓN 2 - Compacto:
  Left splash0000, Left splash0001, ...
  Down splash0000, Down splash0001, ...
  Up splash0000, Up splash0001, ...
  Right splash0000, Right splash0001, ...

OPCIÓN 3 - Por color (purple=izquierda, blue=abajo, green=arriba, red=derecha):
  purple splash0000, purple splash0001, ...
  blue splash0000, blue splash0001, ...
  green splash0000, green splash0001, ...
  red splash0000, red splash0001, ...

OPCIÓN 4 - FNF Base (formato original de Friday Night Funkin'):
  note impact 1 purple0000, note impact 1 purple0001, ...
  note impact 1 blue0000, note impact 1 blue0001, ...
  note impact 1 green0000, note impact 1 green0001, ...
  note impact 1 red0000, note impact 1 red0001, ...
  (También soporta "note impact 2" y variantes con doble espacio)

OPCIÓN 5 - Genérico (mismo splash para todas las lanes):
  splash0000, splash0001, splash0002, ...

═══════════════════════════════════════════════════════════════════════════════
  EJEMPLO DE XML (noteSplashes.xml)
═══════════════════════════════════════════════════════════════════════════════

<?xml version="1.0" encoding="utf-8"?>
<TextureAtlas imagePath="noteSplashes.png">
    <!-- Lane izquierda (purple) -->
    <SubTexture name="noteLeft splash0000" x="0" y="0" width="200" height="200"/>
    <SubTexture name="noteLeft splash0001" x="200" y="0" width="200" height="200"/>
    <SubTexture name="noteLeft splash0002" x="400" y="0" width="200" height="200"/>
    
    <!-- Lane abajo (blue) -->
    <SubTexture name="noteDown splash0000" x="0" y="200" width="200" height="200"/>
    <SubTexture name="noteDown splash0001" x="200" y="200" width="200" height="200"/>
    
    <!-- Lane arriba (green) -->
    <SubTexture name="noteUp splash0000" x="0" y="400" width="200" height="200"/>
    <SubTexture name="noteUp splash0001" x="200" y="400" width="200" height="200"/>
    
    <!-- Lane derecha (red) -->
    <SubTexture name="noteRight splash0000" x="0" y="600" width="200" height="200"/>
    <SubTexture name="noteRight splash0001" x="200" y="600" width="200" height="200"/>
</TextureAtlas>

═══════════════════════════════════════════════════════════════════════════════
  EJEMPLO DE JSON (splash.json) - OPCIONAL Y NO ESTÁNDAR FNF
═══════════════════════════════════════════════════════════════════════════════

IMPORTANTE: FNF normalmente NO usa este archivo. Solo PNG + XML.
Este JSON es una característica EXTRA para configuración avanzada.

Si copias notesplashes de un mod FNF, NO NECESITAS crear este archivo.
El juego funcionará perfectamente solo con PNG + XML.

{
    "fps": 24,
    "duration": 0.3,
    "scale": 1.0,
    "colorizeByLane": false,
    "offsetX": [0, 0, 0, 0],
    "offsetY": [0, 0, 0, 0]
}

PARÁMETROS:
- fps:             Velocidad de animación (frames por segundo)
- duration:        Duración total del efecto en segundos
- scale:           Multiplicador de tamaño (1.0 = normal, 0.5 = mitad, 2.0 = doble)
- colorizeByLane:  Si true, aplica color de lane a la textura (purple/cyan/green/red)
- offsetX/Y:       Ajuste de posición por lane [izq, abajo, arriba, der]

═══════════════════════════════════════════════════════════════════════════════
  MODO DEFAULT
═══════════════════════════════════════════════════════════════════════════════

Si seleccionas "Default" en el menú, el juego usará un splash PROGRAMÁTICO:
  ✓ No requiere archivos
  ✓ Sin problemas de copyright
  ✓ Efecto simple de partículas con colores
  ✓ Siempre disponible

═══════════════════════════════════════════════════════════════════════════════
  COMPATIBILIDAD
═══════════════════════════════════════════════════════════════════════════════

Este sistema es compatible con:
  ✓ Friday Night Funkin' (FNF) vanilla
  ✓ Psych Engine
  ✓ Codename Engine
  ✓ Cualquier mod que use formato Sparrow Atlas XML

Puedes copiar notesplashes de otros mods FNF directamente aquí.

═══════════════════════════════════════════════════════════════════════════════
  CUÁNDO APARECEN LOS SPLASHES
═══════════════════════════════════════════════════════════════════════════════

Los notesplashes solo aparecen cuando:
  ✓ Obtienes rating "Sick!" (±45ms de precisión perfecta)
  ✓ NO aparecen con Good/Bad/Shit

Para practicar y ver tus splashes:
  - Desactiva Autoplay
  - Intenta golpear las notas lo más preciso posible
  - Sick! = splash visible

═══════════════════════════════════════════════════════════════════════════════
  TROUBLESHOOTING
═══════════════════════════════════════════════════════════════════════════════

PROBLEMA: Mi estilo no aparece en el menú
SOLUCIÓN: Verifica que la carpeta tenga al menos un PNG y un XML válidos

PROBLEMA: Los frames no se cargan
SOLUCIÓN: Revisa que los nombres de frames coincidan con las opciones listadas arriba

PROBLEMA: El splash se ve cortado o mal posicionado
SOLUCIÓN: Ajusta offsetX/offsetY en splash.json, o modifica frameX/frameY en el XML

PROBLEMA: La animación va muy rápida/lenta
SOLUCIÓN: Ajusta "fps" en splash.json (default: 24)

═══════════════════════════════════════════════════════════════════════════════
  MÁS INFORMACIÓN
═══════════════════════════════════════════════════════════════════════════════

Lee el archivo NOTESPLASH_README.md en la carpeta principal del proyecto
para documentación técnica completa.

═══════════════════════════════════════════════════════════════════════════════
