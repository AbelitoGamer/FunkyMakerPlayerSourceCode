═══════════════════════════════════════════════════════════════════════════
  FUNKY MAKER PLAYER - CUSTOM NOTE STYLES (ARROW SKINS)
═══════════════════════════════════════════════════════════════════════════

Custom visual styles for arrows (receptors and scrolling notes).

HOW TO ADD A NOTE STYLE:
───────────────────────────────────────────────────────────────────────────
1. Create a folder: mods/notestyles/YourStyleName/
2. Add required PNG + XML files (see file names below)
3. OPTIONAL: Add skin.json or config.json for advanced settings
4. Select in-game: Settings → Arrows → YourStyleName

EXAMPLE STRUCTURE:
───────────────────────────────────────────────────────────────────────────
mods/notestyles/
└── MyEpicStyle/
    ├── fmm.png              (notes + receptors combined, preferred)
    ├── fmm.xml
    ├── NOTE_hold_FMM.png    (hold trails, optional)
    ├── skin.json            (optional config)
    └── README.txt           (optional)

ACCEPTED FILE NAMES (PNG + XML pairs):
───────────────────────────────────────────────────────────────────────────
For NOTES (scrolling arrows):
  Priority order (first found is used):
  1. fmm.png / fmm.xml                    (recommended)
  2. notes.png / notes.xml
  3. NOTE_assets.png / NOTE_assets.xml
  4. default.png / default.xml
  5. sonic.png / sonic.xml

For RECEPTORS (static arrows at top/bottom):
  Priority order (first found is used):
  1. notefmm.png / notefmm.xml            (preferred)
  2. noteStrumline.png / noteStrumline.xml
  3. NOTE_assets.png / NOTE_assets.xml    (can share with notes)
  4. default.png / default.xml

For HOLD TRAILS (long note visuals):
  • NOTE_hold_FMM.png
  • NOTE_hold_assets.png
  • hold.png

Note: If receptors not found, it uses the notes texture for both.

REQUIRED FRAMES IN XML (for notes):
───────────────────────────────────────────────────────────────────────────
Standard FNF naming (recommended):
  • purple0 / purple       (left note)
  • blue0 / blue           (down note)
  • green0 / green         (up note)
  • red0 / red             (right note)

Alternative naming (also supported):
  • noteLeft0 / noteLeft
  • noteDown0 / noteDown
  • noteUp0 / noteUp
  • noteRight0 / noteRight

Codename Engine naming:
  • arrowLEFT
  • arrowDOWN
  • arrowUP
  • arrowRIGHT

REQUIRED FRAMES IN XML (for receptors):
───────────────────────────────────────────────────────────────────────────
For each lane (Left, Down, Up, Right):

State 0 - STATIC (idle/waiting):
  • staticLeft0000, staticLeft0001, ... (can be multi-frame animation)
  • left static0000, left static0001, ... (Codename format)
  • arrowLEFT (single frame fallback)

State 1 - PRESS (key pressed but no note hit):
  • pressLeft0000, pressLeft0001, ...
  • left press0000, left press0001, ...

State 2 - CONFIRM (note hit perfectly):
  • confirmLeft0000, confirmLeft0001, ...
  • left confirm0000, left confirm0001, ...

State 3 - CONFIRM HOLD (holding long note):
  • confirmHoldLeft0000, confirmHoldLeft0001, ...
  • (fallback: uses confirm frames if confirmHold not found)

Frame numbering: 0000-0015 indicates animation sequence (16 frames)

HOLD TRAIL FRAMES (optional, in NOTE_hold_*.png):
───────────────────────────────────────────────────────────────────────────
For each lane (purple=left, blue=down, green=up, red=right):
  • purple hold piece / purpleholdpiece / purple hold
  • purple hold end / purpleholdend / purple end hold
  • (repeat for blue, green, red)

SKIN.JSON FORMAT (optional):
───────────────────────────────────────────────────────────────────────────
{
  "fps": [12, 24, 24, 12],
  "noteScale": 0.7,
  "receptorScale": 0.7,
  "colorizeByLane": true,
  "tintDamage": [255, 100, 100],
  "tintInstakill": [0, 0, 0],
  "tintHeal": [100, 255, 100]
}

Fields:
• fps              - Array [static, press, confirm, confirmHold]
                     Animation speeds in frames per second
                     Example: [12, 24, 24, 12] = 12 FPS idle, 24 FPS actions
• noteScale        - Size multiplier for scroll notes (0.7 = 70% size)
• receptorScale    - Size multiplier for receptors
• colorizeByLane   - Tint notes by lane color (purple/blue/green/red)
                     Set to false if your sprites are pre-colored
• tintDamage       - RGB color [0-255] for damage note type
• tintInstakill    - RGB color [0-255] for instakill note type
• tintHeal         - RGB color [0-255] for heal note type

CONFIG.JSON FORMAT (optional, for simple Codename skins):
───────────────────────────────────────────────────────────────────────────
Auto-generated if not present. Used by Codename Engine style skins.

{
  "version": "1.0",
  "colorizeByLane": false,
  "notes": {
    "left": "arrowLEFT",
    "down": "arrowDOWN",
    "up": "arrowUP",
    "right": "arrowRIGHT"
  },
  "receptors": {
    "left": {
      "static": "left static",
      "press": "left press",
      "confirm": "left confirm",
      "confirmHold": "left confirm"
    }
    // ... repeat for down, up, right
  },
  "holds": {
    "left": {
      "piece": "purple hold piece",
      "end": "purple hold end"
    }
    // ... repeat for down, up, right
  }
}

TIPS:
───────────────────────────────────────────────────────────────────────────
✅ Use standard FNF frame names for best compatibility
✅ Frame numbers (0000-0015) indicate animation sequence
✅ Test with "Default" style first to verify game works
✅ Check console for loading errors (press F3 for debug info)
✅ Receptor animations: 12 FPS idle (static), 24 FPS actions
✅ Set colorizeByLane=false if your sprites are pre-colored
✅ All frame names in XML are case-sensitive!

IMPORTANT NOTES:
───────────────────────────────────────────────────────────────────────────
⚠️ Note styles are GLOBAL - they apply to all songs
⚠️ You cannot change note style mid-song with events
⚠️ Changes apply immediately when selected in Settings menu
⚠️ Your choice is saved to %AppData%/FunkyMakerPlayer/settings.json
⚠️ Hold trail textures are optional (uses fallback if missing)

DEFAULT FALLBACK:
───────────────────────────────────────────────────────────────────────────
If custom style fails to load or is incomplete, the game
uses the built-in default arrows (Assets/images/NOTE_assets).

TROUBLESHOOTING:
───────────────────────────────────────────────────────────────────────────
❓ Style not appearing in menu?
  → Check folder is in mods/notestyles/ (not just notestyles/)
  → Ensure PNG and XML files exist with matching names
  → Restart game to refresh style list

❓ Style loads but looks wrong?
  → Verify XML frame names match expected format (see above)
  → Check console (F3) for frame loading errors
  → Try "Default" style to test if issue is mod-specific

❓ Hold notes look weird?
  → Add NOTE_hold_FMM.png for custom hold visuals
  → Or define hold frames in your atlas XML

═══════════════════════════════════════════════════════════════════════════
