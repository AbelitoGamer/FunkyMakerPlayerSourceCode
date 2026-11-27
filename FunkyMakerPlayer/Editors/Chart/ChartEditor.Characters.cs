using System;
using System.Collections.Generic;
using System.Linq;
using FunkyMakerPlayer.Editors.Character;
using FunkyMakerPlayer.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;

namespace FunkyMakerPlayer.Editors.Chart
{
    public partial class ChartEditor
    {
        private bool _charModalOpen = false;
        private int _charModalTargetStrumline = 0; // 0 = BF, 1 = FOE, 2+ = Extra
        private int _charModalSelectedIdx = 0;
        private int _charModalScroll = 0;
        private bool _charModalUpdateIcon = true;
        private bool _charModalAutoScale = false;
        private List<CharacterSummary> _charModalLibrary = new();
        private readonly List<(Rectangle Rect, Action OnClick, bool Repeatable)> _charModalClicks = new();
        private readonly Dictionary<string, Texture2D?> _charModalIcons = new(StringComparer.OrdinalIgnoreCase);
        private Rectangle _charsBtnRect;

        // Hold-to-repeat state for Pos X / Pos Y / Scale buttons (mouse + Android touch)
        private Rectangle _charModalHoldRect;
        private Action? _charModalHoldAction;
        private double _charModalHoldNextTick;
        private bool _charModalPrevRawLeftDown;
        private bool _charModalSuppressRelease;

        private void DisposeCharacterModalIcons()
        {
            foreach (var t in _charModalIcons.Values)
                try { if (t != null && !t.IsDisposed) t.Dispose(); } catch { }
            _charModalIcons.Clear();
        }

        private Texture2D? GetCharModalIcon(GraphicsDevice gd, string name)
        {
            if (_charModalIcons.TryGetValue(name, out var cached)) return cached;
            var tex = CharacterEditor.LoadUiIcon(gd, name);
            _charModalIcons[name] = tex;
            return tex;
        }

        private static (string? Icon, string Label) ResolveModalBtnIcon(string text)
        {
            switch (text)
            {
                case "CLOSE": return ("close", "CLOSE");
                case "+ CHARACTER EDITOR": return ("character", "CHARACTER EDITOR");
                case "PLAYER (BF - SL0)": return ("character", "PLAYER (BF)");
                case "OPPONENT (FOE - SL1)": return ("opponent", "OPPONENT (FOE)");
                case "+ NEW SL": return ("add", "NEW SL");
                case "APPLY CHARACTER TO TRACK": return ("save", "APPLY CHARACTER");
                case "FLIP": return ("flip", "FLIP");
                case "-": return ("remove", "");
                case "+": return ("add", "");
            }
            if (text.StartsWith("EXTRA") || text == "NO EXTRA SL") return ("extra_track", text);
            if (text.StartsWith("[X] ")) return ("check_on", text.Substring(4));
            if (text.StartsWith("[ ] ")) return ("check_off", text.Substring(4));
            return (null, text);
        }

        public bool OpenCharacterEditorRequested { get; private set; }
        private string? _requestedCharEditorSlug;

        public string? ConsumeOpenCharacterEditorRequest()
        {
            OpenCharacterEditorRequested = false;
            string? s = _requestedCharEditorSlug;
            _requestedCharEditorSlug = null;
            return s;
        }

        public void OnReturnedFromCharacterEditor(string? lastSavedSlug)
        {
            _charModalLibrary = CharacterLibrary.ScanCharacters(bakedOnly: false);
            if (!string.IsNullOrWhiteSpace(lastSavedSlug))
            {
                int idx = _charModalLibrary.FindIndex(c => string.Equals(c.Slug, lastSavedSlug, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0)
                    _charModalSelectedIdx = idx;
                ShowToast($"Character '{lastSavedSlug}' ready to assign!");
            }
        }

        private void ToggleCharacterModal()
        {
            _charModalOpen = !_charModalOpen;
            _charModalHoldAction = null;
            _charModalSuppressRelease = false;
            _charModalPrevRawLeftDown = PointerInput.GetRawState().LeftButton == ButtonState.Pressed;
            if (_charModalOpen)
            {
                PausePlayback();
                _charModalLibrary = CharacterLibrary.ScanCharacters(bakedOnly: false);
                _charModalTargetStrumline = _selectedStrumline;
                _charModalSelectedIdx = Math.Clamp(_charModalSelectedIdx, 0, Math.Max(0, _charModalLibrary.Count - 1));
            }
            UiSfx.Play(UiSound.SubTab);
        }

        private void HandleCharacterModalKeyboard(KeyboardState kb)
        {
            if (JustPressed(kb, Keys.Escape) || JustPressed(kb, Keys.B))
            {
                _charModalOpen = false;
                UiSfx.Play(UiSound.Back);
                return;
            }

            if (_charModalLibrary.Count > 0)
            {
                if (JustPressed(kb, Keys.Up))
                {
                    _charModalSelectedIdx = (_charModalSelectedIdx - 1 + _charModalLibrary.Count) % _charModalLibrary.Count;
                    UiSfx.Play(UiSound.Move);
                }
                else if (JustPressed(kb, Keys.Down))
                {
                    _charModalSelectedIdx = (_charModalSelectedIdx + 1) % _charModalLibrary.Count;
                    UiSfx.Play(UiSound.Move);
                }
                else if (JustPressed(kb, Keys.Enter))
                {
                    ApplySelectedModalCharacter();
                }
            }
        }

        private void HandleCharacterModalMouse(MouseState ms)
        {
            int wheel = ms.ScrollWheelValue - _prevMouse.ScrollWheelValue;
            if (wheel != 0)
            {
                int step = wheel > 0 ? -1 : 1;
                _charModalScroll = Math.Clamp(_charModalScroll + step, 0, Math.Max(0, _charModalLibrary.Count - 5));
            }

            var rawMs = PointerInput.GetRawState();
            bool rawLeftDown = rawMs.LeftButton == ButtonState.Pressed;
            bool rawJustPressed = rawLeftDown && !_charModalPrevRawLeftDown;
            _charModalPrevRawLeftDown = rawLeftDown;

            double nowSec = System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;

            // Hold-to-repeat on repeatable buttons (Pos X / Pos Y / Scale +/-)
            if (rawJustPressed)
            {
                for (int i = _charModalClicks.Count - 1; i >= 0; i--)
                {
                    if (_charModalClicks[i].Repeatable && _charModalClicks[i].Rect.Contains(rawMs.Position))
                    {
                        _charModalHoldRect = _charModalClicks[i].Rect;
                        _charModalHoldAction = _charModalClicks[i].OnClick;
                        _charModalHoldNextTick = nowSec + 0.28;
                        _charModalSuppressRelease = PointerInput.IsTouch;
                        _charModalHoldAction();
                        return;
                    }
                }
            }

            if (_charModalHoldAction != null)
            {
                if (rawLeftDown)
                {
                    var tol = _charModalHoldRect;
                    tol.Inflate(8, 8);
                    if (tol.Contains(rawMs.Position) && nowSec >= _charModalHoldNextTick)
                    {
                        _charModalHoldAction();
                        _charModalHoldNextTick = nowSec + 0.055;
                    }
                    return;
                }
                _charModalHoldAction = null;
            }

            bool leftJustPressed = ms.LeftButton == ButtonState.Pressed && _prevMouse.LeftButton == ButtonState.Released;
            if (!leftJustPressed) return;
            if (_charModalSuppressRelease)
            {
                _charModalSuppressRelease = false;
                return;
            }

            for (int i = _charModalClicks.Count - 1; i >= 0; i--)
            {
                if (_charModalClicks[i].Rect.Contains(ms.Position))
                {
                    _charModalClicks[i].OnClick();
                    return;
                }
            }
        }

        private void ApplySelectedModalCharacter()
        {
            if (_level == null || _charModalSelectedIdx < 0 || _charModalSelectedIdx >= _charModalLibrary.Count)
                return;

            var chosen = _charModalLibrary[_charModalSelectedIdx];
            string sprKey = $"char_{CharacterLibrary.SanitizeSlug(chosen.Slug)}_sl{_charModalTargetStrumline}";

            // Dispose stale cached preview GPU textures for this sprite key (and icon key) before replacing
            InvalidateSpritePreviewCache(sprKey);
            InvalidateSpritePreviewCache($"{sprKey}_icon");

            var appliedObj = CharacterLibrary.ApplyCharacterToLevel(
                _level,
                chosen.Slug,
                _charModalTargetStrumline,
                updateIcon: _charModalUpdateIcon,
                autoScaleToReplaced: _charModalAutoScale,
                spriteKeyOverride: sprKey);

            if (appliedObj == null)
            {
                UiSfx.Play(UiSound.Error);
                ShowToast($"Failed to load character '{chosen.Name}'");
                return;
            }

            _manualModified = true;
            _showPreview = true;
            ForceRebuildStagePreview();
            UiSfx.Play(UiSound.Confirm);

            string roleTag = _charModalTargetStrumline == 0 ? "Player (BF)"
                           : _charModalTargetStrumline == 1 ? "Opponent (FOE)"
                           : $"Extra SL{_charModalTargetStrumline}";
            ShowToast($"Assigned '{chosen.Name}' -> {roleTag}!");
        }

        private void AdjustAssignedSceneObject(float dx, float dy, float dScale, bool flipX = false)
        {
            if (_level == null) return;
            var obj = _level.Scene.Objects.FirstOrDefault(o => (int)o.StrumLine == _charModalTargetStrumline && o.Visible)
                   ?? _level.Scene.Objects.FirstOrDefault(o => (int)o.StrumLine == _charModalTargetStrumline);
            if (obj == null) return;

            var pos = obj.GetPosition();
            var sc = obj.GetScale();
            float signX = sc.X < 0 ? -1f : 1f;
            if (flipX) signX = -signX;

            float absX = Math.Clamp(MathF.Round(MathF.Abs(sc.X) + dScale, 2), 0.10f, 8.0f);
            float absY = Math.Clamp(MathF.Round(MathF.Abs(sc.Y) + dScale, 2), 0.10f, 8.0f);

            var newPos = new Vec2 { X = MathF.Round(pos.X + dx, 1), Y = MathF.Round(pos.Y + dy, 1) };
            var newScale = new Vec2 { X = signX * absX, Y = absY };

            CharacterLibrary.SetObjectTransform(obj, newPos, newScale);
            obj.SaveInitialState();

            _manualModified = true;
            ForceRebuildStagePreview();
        }

        private void AddExtraStrumlineFromModal()
        {
            if (_level == null || _level.Data.Strumlines.Count >= MAX_STRUMLINES) return;
            int newSl = _level.Data.Strumlines.Count;
            var sl = new List<List<FnmmNote>>();
            for (int l = 0; l < LANE_COUNT; l++)
                sl.Add(new List<FnmmNote>());
            _level.Data.Strumlines.Add(sl);

            _charModalTargetStrumline = newSl;
            _leftStrumline = newSl;
            _selectedStrumline = newSl;
            _manualModified = true;
            UiSfx.Play(UiSound.Confirm);
            ShowToast($"Added Extra Strumline SL{newSl}!");
        }

        private void InvalidateSpritePreviewCache(string spriteName)
        {
            if (_previewSpriteTextureCache.TryGetValue(spriteName, out var entryMap))
            {
                if (_ownsPreviewTextures)
                {
                    foreach (var ce in entryMap.Values)
                        foreach (var t in ce.Textures)
                            try { if (t != null && !t.IsDisposed) t.Dispose(); } catch { }
                }
                _previewSpriteTextureCache.Remove(spriteName);
            }
            _previewTexCache.Remove(spriteName);
        }

        private void ForceRebuildStagePreview()
        {
            _previewInitialized = false;
            _previewNeedsSeek = true;
            _previewLastSeekTime = -1f;
        }

        private void DrawCharacterModal(SpriteBatch sb, int sw, int sh)
        {
            if (_level == null) return;
            float s = _uiScale;
            _charModalClicks.Clear();

            // Dim backdrop (clicking outside closes modal)
            var fullRect = new Rectangle(0, 0, sw, sh);
            sb.Draw(_pixel, fullRect, Color.Black * 0.68f);

            int mw = Math.Clamp((int)(sw * 0.62f), UiScale.Px(500, s), UiScale.Px(780, s));
            int mh = Math.Clamp((int)(sh * 0.78f), UiScale.Px(400, s), UiScale.Px(600, s));
            var mr = new Rectangle((sw - mw) / 2, (sh - mh) / 2, mw, mh);

            // Register outside-click close first (lowest priority)
            _charModalClicks.Add((fullRect, () => _charModalOpen = false, false));
            // Swallow clicks inside modal body unless a button handles them
            _charModalClicks.Add((mr, () => { }, false));

            sb.Draw(_pixel, mr, BG_PANEL);
            DrawBorder(sb, mr, new Color(178, 102, 255));

            int pad = UiScale.Px(14, s);
            int hdrH = UiScale.Px(46, s);
            sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + 1, mr.Width - 2, hdrH), new Color(19, 18, 29));
            sb.Draw(_pixel, new Rectangle(mr.X + 1, mr.Y + hdrH, mr.Width - 2, 1), Color.White * 0.10f);

            int x = mr.X + pad;
            int w = mw - pad * 2;
            int y = mr.Y + UiScale.Px(12, s);

            int titleFont = UiScale.FontPx(15, s);
            int hdrFont = UiScale.FontPx(11, s);
            int smallFont = UiScale.FontPx(12, s);
            int tinyFont = UiScale.FontPx(11, s);
            int microFont = UiScale.FontPx(10, s);

            DrawText(sb, "SCENE CHARACTERS & ICONS", new Vector2(x, y + UiScale.Px(2, s)), ACCENT_GOLD, titleFont);

            int closeW = UiScale.Px(82, s);
            int openEdW = UiScale.Px(172, s);
            int topBtnH = UiScale.Px(28, s);
            int topBtnY = mr.Y + (hdrH - topBtnH) / 2;
            DrawModalBtn(sb, new Rectangle(mr.Right - pad - closeW, topBtnY, closeW, topBtnH), "CLOSE",
                BG_CARD, Color.White, tinyFont, Color.White * 0.2f, () => _charModalOpen = false);

            DrawModalBtn(sb, new Rectangle(mr.Right - pad - closeW - UiScale.Px(8, s) - openEdW, topBtnY, openEdW, topBtnH),
                "+ CHARACTER EDITOR",
                new Color(52, 34, 78), new Color(210, 165, 255), tinyFont, new Color(178, 102, 255),
                () =>
                {
                    _requestedCharEditorSlug = (_charModalSelectedIdx >= 0 && _charModalSelectedIdx < _charModalLibrary.Count)
                        ? _charModalLibrary[_charModalSelectedIdx].Slug
                        : null;
                    OpenCharacterEditorRequested = true;
                });

            y = mr.Y + hdrH + UiScale.Px(12, s);

            // Role / Strumline Target Tabs — distributed cleanly across the full modal width
            int tabGap = UiScale.Px(6, s);
            int tabH = UiScale.Px(30, s);
            int addSlW = UiScale.Px(96, s);
            int tabGroupW = w - addSlW - tabGap * 3;
            int bfW = (int)(tabGroupW * 0.34f);
            int foeW = (int)(tabGroupW * 0.36f);
            int extraW = tabGroupW - bfW - foeW;

            bool isBf = _charModalTargetStrumline == 0;
            bool isFoe = _charModalTargetStrumline == 1;
            bool isExtra = _charModalTargetStrumline >= 2;

            DrawModalBtn(sb, new Rectangle(x, y, bfW, tabH), "PLAYER (BF - SL0)",
                isBf ? ACCENT_CYAN * 0.28f : BG_CARD, isBf ? Color.White : Color.Gray, tinyFont,
                isBf ? ACCENT_CYAN : Color.White * 0.12f, () => _charModalTargetStrumline = 0);

            DrawModalBtn(sb, new Rectangle(x + bfW + tabGap, y, foeW, tabH), "OPPONENT (FOE - SL1)",
                isFoe ? ACCENT_PINK * 0.28f : BG_CARD, isFoe ? Color.White : Color.Gray, tinyFont,
                isFoe ? ACCENT_PINK : Color.White * 0.12f, () => _charModalTargetStrumline = 1);

            string extraLbl = _level.Data.Strumlines.Count > 2
                ? (isExtra ? $"EXTRA: SL{_charModalTargetStrumline}" : $"EXTRA (SL2..{_level.Data.Strumlines.Count - 1})")
                : "NO EXTRA SL";
            DrawModalBtn(sb, new Rectangle(x + bfW + foeW + tabGap * 2, y, extraW, tabH), extraLbl,
                isExtra ? ACCENT_GOLD * 0.26f : BG_CARD,
                _level.Data.Strumlines.Count > 2 ? Color.White : Color.Gray, tinyFont,
                isExtra ? ACCENT_GOLD : Color.White * 0.12f, () =>
                {
                    if (_level.Data.Strumlines.Count > 2)
                    {
                        if (_charModalTargetStrumline < 2) _charModalTargetStrumline = 2;
                        else _charModalTargetStrumline = 2 + ((_charModalTargetStrumline - 2 + 1) % (_level.Data.Strumlines.Count - 2));
                    }
                });

            DrawModalBtn(sb, new Rectangle(x + bfW + foeW + extraW + tabGap * 3, y, addSlW, tabH), "+ NEW SL",
                BG_CARD, ACCENT_GREEN, tinyFont, ACCENT_GREEN * 0.45f, AddExtraStrumlineFromModal);
            y += tabH + UiScale.Px(12, s);

            // Split body: Left = mods/characters list; Right = Current Strumline Scene Object Transform + Apply options
            int leftW = (int)(w * 0.48f);
            int rightX = x + leftW + UiScale.Px(12, s);
            int rightW = x + w - rightX;
            int bodyH = mr.Bottom - pad - y;

            // Left: Character Library List
            DrawText(sb, $"MODS/CHARACTERS ({_charModalLibrary.Count})", new Vector2(x, y), ACCENT_GOLD, hdrFont);
            int listTop = y + UiScale.Px(18, s);
            int listH = bodyH - UiScale.Px(18, s);
            var listRect = new Rectangle(x, listTop, leftW, listH);
            sb.Draw(_pixel, listRect, BG_DARK);
            DrawBorder(sb, listRect, Color.White * 0.12f);

            if (_charModalLibrary.Count == 0)
            {
                string emptyMsg = "No custom characters in mods/characters/ yet.\nClick '+ CHARACTER EDITOR' above to import\nXML+PNG or Adobe Animate Spritemaps!";
                DrawModalCenteredMultiline(sb, emptyMsg, listRect, Color.White * 0.55f, tinyFont);
            }
            else
            {
                int rowH = UiScale.Px(34, s);
                int maxVis = Math.Max(3, (listH - 8) / (rowH + 3));
                _charModalScroll = Math.Clamp(_charModalScroll, 0, Math.Max(0, _charModalLibrary.Count - maxVis));
                bool needsScroll = _charModalLibrary.Count > maxVis;
                int scrollW = needsScroll ? UiScale.Px(9, s) : 0;

                for (int i = 0; i < maxVis && (_charModalScroll + i) < _charModalLibrary.Count; i++)
                {
                    int idx = _charModalScroll + i;
                    var item = _charModalLibrary[idx];
                    var rr = new Rectangle(listRect.X + 4, listRect.Y + 4 + i * (rowH + 3), listRect.Width - 8 - scrollW, rowH);
                    bool sel = idx == _charModalSelectedIdx;
                    string badge = item.IsRawSourceFolder ? $" [{item.RawSourceHint}]" : (item.HasIcon ? " [ICO]" : "");
                    DrawModalBtn(sb, rr, item.Name + badge,
                        sel ? new Color(178, 102, 255) * 0.30f : BG_CARD,
                        sel ? Color.White : Color.White * 0.85f, smallFont,
                        sel ? new Color(178, 102, 255) : Color.White * 0.08f,
                        () => _charModalSelectedIdx = idx, leftAlign: true);
                }

                if (needsScroll)
                {
                    var track = new Rectangle(listRect.Right - scrollW, listRect.Y + 4, scrollW - 3, listRect.Height - 8);
                    sb.Draw(_pixel, track, Color.White * 0.06f);
                    int thumbH = Math.Max(14, (int)(track.Height * Math.Clamp(maxVis / (float)_charModalLibrary.Count, 0.12f, 1f)));
                    int thumbY = track.Y + (int)((track.Height - thumbH) * (_charModalScroll / (float)Math.Max(1, _charModalLibrary.Count - maxVis)));
                    sb.Draw(_pixel, new Rectangle(track.X, thumbY, track.Width, thumbH), new Color(178, 102, 255) * 0.7f);
                }
            }

            // Right: Selected Character Summary + Apply Options + Transform Controls
            int ry = y;
            DrawText(sb, "ASSIGN & STAGE TRANSFORM", new Vector2(rightX, ry), ACCENT_CYAN, hdrFont);
            ry += UiScale.Px(18, s);

            var curObj = _level.Scene.Objects.FirstOrDefault(o => (int)o.StrumLine == _charModalTargetStrumline && o.Visible)
                      ?? _level.Scene.Objects.FirstOrDefault(o => (int)o.StrumLine == _charModalTargetStrumline);

            // Summary card for current target + selected character
            int sumH = UiScale.Px(46, s);
            var sumCard = new Rectangle(rightX, ry, rightW, sumH);
            sb.Draw(_pixel, sumCard, new Color(19, 18, 29));
            DrawBorder(sb, sumCard, Color.White * 0.10f);

            string curSpr = curObj != null ? curObj.Spr : "(none in scene)";
            string sprLine = TruncateModalText($"Current: {curSpr}", rightW - UiScale.Px(14, s), microFont);
            DrawText(sb, sprLine, new Vector2(sumCard.X + UiScale.Px(8, s), sumCard.Y + UiScale.Px(6, s)), Color.White * 0.88f, microFont);

            if (_charModalSelectedIdx >= 0 && _charModalSelectedIdx < _charModalLibrary.Count)
            {
                var selChar = _charModalLibrary[_charModalSelectedIdx];
                string facing = selChar.NativeFacing == CharacterFacing.Left ? "Faces Left (BF)" : "Faces Right (FOE)";
                string icoStr = selChar.HasIcon ? "Icon: Yes" : "Icon: No";
                string selInfo = TruncateModalText($"Selected: {selChar.Scale:F2}x  •  {facing}  •  {icoStr}", rightW - UiScale.Px(14, s), microFont);
                DrawText(sb, selInfo, new Vector2(sumCard.X + UiScale.Px(8, s), sumCard.Y + UiScale.Px(24, s)), ACCENT_CYAN * 0.9f, microFont);
            }
            else
            {
                DrawText(sb, "Select a character on the left to assign.", new Vector2(sumCard.X + UiScale.Px(8, s), sumCard.Y + UiScale.Px(24, s)), Color.White * 0.45f, microFont);
            }
            ry += sumH + UiScale.Px(8, s);

            int chkH = UiScale.Px(28, s);
            DrawModalBtn(sb, new Rectangle(rightX, ry, rightW, chkH),
                _charModalUpdateIcon ? "[X] Update Health Bar Icon" : "[ ] Keep Existing Health Icon",
                _charModalUpdateIcon ? new Color(24, 68, 52) : BG_CARD,
                _charModalUpdateIcon ? ACCENT_GREEN : Color.Gray, tinyFont,
                _charModalUpdateIcon ? ACCENT_GREEN * 0.5f : Color.White * 0.12f,
                () => _charModalUpdateIcon = !_charModalUpdateIcon);
            ry += chkH + UiScale.Px(6, s);

            DrawModalBtn(sb, new Rectangle(rightX, ry, rightW, chkH),
                _charModalAutoScale ? "[X] Match Replaced Char Height" : "[ ] Use Character Native Scale",
                _charModalAutoScale ? new Color(28, 56, 78) : BG_CARD,
                _charModalAutoScale ? ACCENT_CYAN : Color.White * 0.85f, tinyFont,
                _charModalAutoScale ? ACCENT_CYAN * 0.5f : Color.White * 0.12f,
                () => _charModalAutoScale = !_charModalAutoScale);
            ry += chkH + UiScale.Px(8, s);

            // Big APPLY button
            int applyH = UiScale.Px(34, s);
            bool canApply = _charModalLibrary.Count > 0;
            DrawModalBtn(sb, new Rectangle(rightX, ry, rightW, applyH),
                "APPLY CHARACTER TO TRACK",
                canApply ? new Color(36, 118, 76) : BG_CARD,
                canApply ? Color.White : Color.Gray, smallFont,
                canApply ? ACCENT_GREEN : Color.White * 0.1f,
                ApplySelectedModalCharacter);
            ry += applyH + UiScale.Px(12, s);

            // Live Scene Object Position / Scale / Flip controls (with ±25 and ±5 fine step!)
            if (curObj != null && ry + UiScale.Px(106, s) <= mr.Bottom - pad)
            {
                var pos = curObj.GetPosition();
                var sc = curObj.GetScale();
                DrawText(sb, "SCENE OBJECT FINE-TUNE", new Vector2(rightX, ry), ACCENT_GOLD, hdrFont);
                ry += UiScale.Px(18, s);

                int coarseW = UiScale.Px(34, s);
                int fineW = UiScale.Px(30, s);
                int rowBtnH = UiScale.Px(26, s);
                int g = UiScale.Px(3, s);

                DrawText(sb, $"Pos X: {pos.X:F0}", new Vector2(rightX, ry + 4), Color.White, tinyFont);
                int pxEnd = rightX + rightW;
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW, ry, coarseW, rowBtnH), "+25", BG_CARD, Color.White, microFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(25f, 0f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW - fineW - g, ry, fineW, rowBtnH), "+5", BG_CARD, ACCENT_CYAN, microFont, ACCENT_CYAN * 0.35f, () => AdjustAssignedSceneObject(5f, 0f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW - fineW * 2 - g * 2, ry, fineW, rowBtnH), "-5", BG_CARD, ACCENT_CYAN, microFont, ACCENT_CYAN * 0.35f, () => AdjustAssignedSceneObject(-5f, 0f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW * 2 - fineW * 2 - g * 3, ry, coarseW, rowBtnH), "-25", BG_CARD, Color.White, microFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(-25f, 0f, 0f), repeatable: true);
                ry += rowBtnH + UiScale.Px(5, s);

                DrawText(sb, $"Pos Y: {pos.Y:F0}", new Vector2(rightX, ry + 4), Color.White, tinyFont);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW, ry, coarseW, rowBtnH), "+25", BG_CARD, Color.White, microFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(0f, 25f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW - fineW - g, ry, fineW, rowBtnH), "+5", BG_CARD, ACCENT_CYAN, microFont, ACCENT_CYAN * 0.35f, () => AdjustAssignedSceneObject(0f, 5f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW - fineW * 2 - g * 2, ry, fineW, rowBtnH), "-5", BG_CARD, ACCENT_CYAN, microFont, ACCENT_CYAN * 0.35f, () => AdjustAssignedSceneObject(0f, -5f, 0f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW * 2 - fineW * 2 - g * 3, ry, coarseW, rowBtnH), "-25", BG_CARD, Color.White, microFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(0f, -25f, 0f), repeatable: true);
                ry += rowBtnH + UiScale.Px(5, s);

                int flipW = UiScale.Px(68, s);
                DrawText(sb, $"Scale: {MathF.Abs(sc.X):F2}x", new Vector2(rightX, ry + 4), Color.White, tinyFont);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW, ry, coarseW, rowBtnH), "+", BG_CARD, Color.White, tinyFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(0f, 0f, 0.05f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW * 2 - g, ry, coarseW, rowBtnH), "-", BG_CARD, Color.White, tinyFont, Color.White * 0.15f, () => AdjustAssignedSceneObject(0f, 0f, -0.05f), repeatable: true);
                DrawModalBtn(sb, new Rectangle(pxEnd - coarseW * 2 - flipW - g * 2, ry, flipW, rowBtnH), "FLIP", BG_CARD, ACCENT_CYAN, microFont, ACCENT_CYAN * 0.45f, () => AdjustAssignedSceneObject(0f, 0f, 0f, flipX: true));
            }
        }

        private string TruncateModalText(string text, int maxW, int fontSize)
        {
            if (maxW <= 12 || string.IsNullOrEmpty(text)) return text;
            if (MeasureText(text, fontSize).X <= maxW) return text;
            for (int len = text.Length - 1; len > 2; len--)
            {
                string sub = text.Substring(0, len) + "..";
                if (MeasureText(sub, fontSize).X <= maxW) return sub;
            }
            return text.Length > 2 ? text.Substring(0, 2) + ".." : text;
        }

        private void DrawModalBtn(SpriteBatch sb, Rectangle r, string text, Color bg, Color fg, int fontSize, Color border, Action onClick, bool leftAlign = false, bool repeatable = false)
        {
            bool hov = r.Contains(_prevMouse.Position);
            sb.Draw(_pixel, r, hov ? Color.Lerp(bg, Color.White, 0.12f) : bg);
            DrawBorder(sb, r, hov ? Color.White * 0.65f : border);

            var (iconName, shortLabel) = leftAlign ? ("character", text) : ResolveModalBtnIcon(text);
            Texture2D? icon = iconName != null ? GetCharModalIcon(sb.GraphicsDevice, iconName) : null;
            string label = icon != null ? shortLabel : text;

            if (icon != null && leftAlign)
            {
                int isz = Math.Clamp((int)(r.Height * 0.56f), 12, 26);
                int padX = UiScale.Px(8, _uiScale, 4);
                var iconRect = new Rectangle(r.X + padX, r.Y + (r.Height - isz) / 2, isz, isz);
                sb.Draw(icon, iconRect, fg);
                int textX = iconRect.Right + UiScale.Px(6, _uiScale, 4);
                string clipped = TruncateModalText(label, Math.Max(24, r.Right - textX - UiScale.Px(6, _uiScale)), fontSize);
                var sz = MeasureText(clipped, fontSize);
                DrawText(sb, clipped, new Vector2(textX, r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
            }
            else if (icon != null)
            {
                int isz = Math.Clamp((int)(r.Height * 0.58f), 12, 30);
                int gap = label.Length > 0 ? UiScale.Px(5, _uiScale, 3) : 0;
                float textW = label.Length > 0 ? MeasureText(label, fontSize).X : 0f;
                float total = isz + gap + textW;
                if (total > r.Width - 6 && label.Length > 0)
                {
                    isz = Math.Max(11, (int)(r.Height * 0.46f));
                    total = isz + gap + textW;
                    if (total > r.Width - 6) { label = ""; gap = 0; total = isz; }
                }
                float sx = r.X + (r.Width - total) / 2f;
                var iconRect = new Rectangle((int)sx, r.Y + (r.Height - isz) / 2, isz, isz);
                sb.Draw(icon, iconRect, fg);
                if (label.Length > 0)
                {
                    var sz = MeasureText(label, fontSize);
                    DrawText(sb, label, new Vector2(sx + isz + gap, r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
                }
            }
            else if (leftAlign)
            {
                string clipped = TruncateModalText(text, Math.Max(24, r.Width - UiScale.Px(14, _uiScale)), fontSize);
                var sz = MeasureText(clipped, fontSize);
                DrawText(sb, clipped, new Vector2(r.X + UiScale.Px(8, _uiScale), r.Y + (r.Height - sz.Y) / 2f), fg, fontSize);
            }
            else
            {
                DrawTextCenteredInBox(sb, text, r, fg, fontSize);
            }
            _charModalClicks.Add((r, onClick, repeatable));
        }

        private void DrawModalCenteredMultiline(SpriteBatch sb, string text, Rectangle box, Color color, int fontSize)
        {
            string[] lines = text.Split('\n');
            float lh = MeasureText("Ay", fontSize).Y + 2f;
            float total = lines.Length * lh;
            float sy = box.Y + (box.Height - total) * 0.5f;
            for (int i = 0; i < lines.Length; i++)
            {
                var sz = MeasureText(lines[i], fontSize);
                DrawText(sb, lines[i], new Vector2(box.X + (box.Width - sz.X) * 0.5f, sy + i * lh), color, fontSize);
            }
        }
    }
}
