using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using System;
using System.Collections.Generic;

namespace FunkyMakerPlayer
{
    /// <summary>
    /// Paginated help screen explaining how to use mods on Android
    /// Swipe left/right to navigate pages
    /// </summary>
    public class ModsHelpScreen
    {
        private FontRenderer _fontRenderer;
        private Texture2D _pixel;
        
        // Page navigation
        private int _currentPage = 0;
        private int _totalPages = 0;
        
        // Page transition animation
        private float _pageTransition = 0f;
        private int _targetPage = 0;
        
        // Content pages
        private List<HelpPage> _pages = new List<HelpPage>();
        
        public bool IsVisible { get; set; } = false;
        public bool LaunchFolderPicker { get; private set; } = false;
        
        private class HelpPage
        {
            public string Title { get; set; } = "";
            public List<string> Lines { get; set; } = new List<string>();
            public Color TitleColor { get; set; } = new Color(100, 200, 255);
            public bool HasButton { get; set; } = false;
            public string ButtonText { get; set; } = "";
        }
        
        public ModsHelpScreen(FontRenderer fontRenderer, Texture2D pixel)
        {
            _fontRenderer = fontRenderer;
            _pixel = pixel;
            
            InitializePages();
            _totalPages = _pages.Count;
        }
        
        public void Show()
        {
            IsVisible = true;
            _currentPage = 0;
            _targetPage = 0;
            _pageTransition = 0f;
            
            // Enable only Tap gesture for buttons
            TouchPanel.EnabledGestures = GestureType.Tap;
        }
        
        private void InitializePages()
        {
            // PAGE 1: Introducción
            _pages.Add(new HelpPage
            {
                Title = "MODS EN ANDROID",
                TitleColor = new Color(100, 255, 100),
                Lines = new List<string>
                {
                    "",
                    "Bienvenido a la guia de mods",
                    "para Android!",
                    "",
                    "Debido a las restricciones de",
                    "Android 10+, necesitas dar",
                    "permiso para leer archivos.",
                    "",
                    "Esta guia te mostrara como",
                    "agregar tus niveles personalizados.",
                    "",
                    "",
                    "Desliza para continuar"
                }
            });
            
            // PAGE 2: Método recomendado
            _pages.Add(new HelpPage
            {
                Title = "METODO RECOMENDADO",
                TitleColor = new Color(100, 255, 100),
                Lines = new List<string>
                {
                    "",
                    "1. Copia tus archivos .fnmm",
                    "   o .rar a tu telefono",
                    "",
                    "2. Guardalo en:",
                    "   Documents/",
                    "   FunkyMakerPlayerPublicMods/",
                    "   mods/",
                    "",
                    "3. En el menu, toca",
                    "   'GRANT FOLDER ACCESS'",
                    "",
                    "4. Selecciona la carpeta",
                    "   de tus mods"
                }
            });
            
            // PAGE 3: Grant Folder Access
            _pages.Add(new HelpPage
            {
                Title = "GRANT FOLDER ACCESS",
                TitleColor = new Color(255, 200, 100),
                Lines = new List<string>
                {
                    "",
                    "Cuando toques el boton,",
                    "se abrira el selector",
                    "de carpetas de Android.",
                    "",
                    "Navega hasta:",
                    "Documents/",
                    "FunkyMakerPlayerPublicMods",
                    "",
                    "Toca 'USAR ESTA CARPETA'",
                    "o 'SELECT'",
                    "",
                    "El permiso se guarda",
                    "permanentemente!"
                },
                HasButton = true,
                ButtonText = "ABRIR SELECTOR"
            });
            
            // PAGE 4: Import File
            _pages.Add(new HelpPage
            {
                Title = "METODO ALTERNATIVO",
                TitleColor = new Color(100, 200, 255),
                Lines = new List<string>
                {
                    "",
                    "Si 'Grant Folder Access'",
                    "no funciona, usa:",
                    "",
                    "'IMPORT FILE'",
                    "",
                    "1. Toca 'IMPORT FILE'",
                    "   en el menu",
                    "",
                    "2. Selecciona tu archivo",
                    "   .fnmm o .rar",
                    "",
                    "3. El nivel se copiara",
                    "   a la app"
                }
            });
            
            // PAGE 5: Formatos
            _pages.Add(new HelpPage
            {
                Title = "FORMATOS SOPORTADOS",
                TitleColor = new Color(150, 255, 255),
                Lines = new List<string>
                {
                    "",
                    ".fnmm",
                    "Formato nativo de",
                    "FunkyMaker",
                    "",
                    ".rar",
                    "Archivos comprimidos RAR",
                    "",
                    ".zip",
                    "Proximamente...",
                    "",
                    "Los niveles aparecen en",
                    "Freeplay despues de",
                    "reiniciar la app"
                }
            });
            
            // PAGE 6: Problemas
            _pages.Add(new HelpPage
            {
                Title = "PROBLEMAS COMUNES",
                TitleColor = new Color(255, 100, 100),
                Lines = new List<string>
                {
                    "",
                    "No veo mis niveles:",
                    "- Reinicia la app",
                    "- Verifica la carpeta",
                    "- Da permiso con",
                    "  Grant Folder Access",
                    "",
                    "Selector no se abre:",
                    "- Usa Import File",
                    "- Instala un File Manager",
                    "",
                    "App se cierra:",
                    "- Verifica que el archivo",
                    "  no este corrupto"
                }
            });
            
            // PAGE 7: Tips
            _pages.Add(new HelpPage
            {
                Title = "TIPS UTILES",
                TitleColor = new Color(255, 255, 100),
                Lines = new List<string>
                {
                    "",
                    "- Organiza tus niveles",
                    "  en subcarpetas",
                    "",
                    "- La carpeta Documents",
                    "  es accesible via USB",
                    "",
                    "- Comparte niveles via",
                    "  Bluetooth o mensajes",
                    "",
                    "- Mantén backups en",
                    "  la nube",
                    "",
                    "Disfruta jugando!"
                }
            });
        }
        
        
        public void Update(GameTime gameTime)
        {
            if (!IsVisible) return;
            
            float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
            
            // Handle keyboard input
            var kb = Keyboard.GetState();
            if (kb.IsKeyDown(Keys.Escape) || kb.IsKeyDown(Keys.Back))
            {
                IsVisible = false;
                return;
            }
            
            // Reset launch flag
            LaunchFolderPicker = false;
            
            int screenWidth = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Width;
            int screenHeight = GraphicsAdapter.DefaultAdapter.CurrentDisplayMode.Height;
            
            // Navigation button bounds
            int navButtonWidth = 120;
            int navButtonHeight = 60;
            int navButtonY = screenHeight - 200;
            int navButtonMargin = 40;
            
            Rectangle prevButtonBounds = new Rectangle(navButtonMargin, navButtonY, navButtonWidth, navButtonHeight);
            Rectangle nextButtonBounds = new Rectangle(screenWidth - navButtonWidth - navButtonMargin, navButtonY, navButtonWidth, navButtonHeight);
            
            // Green button bounds (page 3)
            Rectangle greenButtonBounds = Rectangle.Empty;
            if (_currentPage == 2 && _pages[_currentPage].HasButton)
            {
                int buttonWidth = (int)(screenWidth * 0.7f);
                int buttonHeight = 60;
                int buttonX = (screenWidth - buttonWidth) / 2;
                int buttonY = screenHeight - 280;
                greenButtonBounds = new Rectangle(buttonX, buttonY, buttonWidth, buttonHeight);
            }
            
            // Process all available gestures
            while (TouchPanel.IsGestureAvailable)
            {
                GestureSample gesture = TouchPanel.ReadGesture();
                
                if (gesture.GestureType == GestureType.Tap)
                {
                    var tapPos = gesture.Position;
                    
                    // Check green button (page 3)
                    if (!greenButtonBounds.IsEmpty && greenButtonBounds.Contains(tapPos))
                    {
                        LaunchFolderPicker = true;
                        IsVisible = false;
                        return;
                    }
                    
                    // Check previous button
                    if (_currentPage > 0 && prevButtonBounds.Contains(tapPos))
                    {
                        _targetPage = _currentPage - 1;
                        _pageTransition = 0f;
                    }
                    
                    // Check next button
                    if (_currentPage < _totalPages - 1 && nextButtonBounds.Contains(tapPos))
                    {
                        _targetPage = _currentPage + 1;
                        _pageTransition = 0f;
                    }
                }
            }
            
            // Animate page transition
            if (_targetPage != _currentPage)
            {
                _pageTransition += deltaTime * 8f;
                
                if (_pageTransition >= 1f)
                {
                    _currentPage = _targetPage;
                    _pageTransition = 0f;
                }
            }
        }
        
        
        public void Draw(SpriteBatch sb, int screenWidth, int screenHeight)
        {
            if (!IsVisible) return;
            
            // Semi-transparent background
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, screenHeight), Color.Black * 0.95f);
            
            var currentPageData = _pages[_currentPage];
            
            // Title bar
            int titleFontSize = 28;
            int bodyFontSize = 22;
            int titleHeight = 80;
            
            sb.Draw(_pixel, new Rectangle(0, 0, screenWidth, titleHeight), currentPageData.TitleColor * 0.3f);
            
            var titleSize = _fontRenderer.MeasureString(currentPageData.Title, titleFontSize);
            int titleX = (int)((screenWidth - titleSize.X) / 2);
            int titleY = (int)((titleHeight - titleSize.Y) / 2);
            
            // Title shadow
            _fontRenderer.DrawString(sb, currentPageData.Title, new Vector2(titleX + 2, titleY + 2), Color.Black * 0.5f, titleFontSize);
            _fontRenderer.DrawString(sb, currentPageData.Title, new Vector2(titleX, titleY), currentPageData.TitleColor, titleFontSize);
            
            // Content area
            int contentY = titleHeight + 40;
            int lineHeight = 32;
            
            foreach (var line in currentPageData.Lines)
            {
                if (!string.IsNullOrEmpty(line))
                {
                    var lineSize = _fontRenderer.MeasureString(line, bodyFontSize);
                    int lineX = (int)((screenWidth - lineSize.X) / 2);
                    
                    _fontRenderer.DrawString(sb, line, new Vector2(lineX, contentY), Color.White * 0.9f, bodyFontSize);
                }
                contentY += lineHeight;
            }
            
            // Navigation buttons (Previous / Next)
            int navButtonWidth = 120;
            int navButtonHeight = 60;
            int navButtonY = screenHeight - 200;
            int navButtonMargin = 40;
            
            // Previous button (left)
            if (_currentPage > 0)
            {
                int prevButtonX = navButtonMargin;
                Rectangle prevBounds = new Rectangle(prevButtonX, navButtonY, navButtonWidth, navButtonHeight);
                
                sb.Draw(_pixel, prevBounds, new Color(50, 150, 200) * 0.9f);
                sb.Draw(_pixel, new Rectangle(prevButtonX - 2, navButtonY - 2, navButtonWidth + 4, navButtonHeight + 4), new Color(30, 100, 150) * 0.7f);
                
                string prevText = "< ATRAS";
                int prevFontSize = 22;
                var prevTextSize = _fontRenderer.MeasureString(prevText, prevFontSize);
                int prevTextX = (int)(prevButtonX + (navButtonWidth - prevTextSize.X) / 2);
                int prevTextY = (int)(navButtonY + (navButtonHeight - prevTextSize.Y) / 2);
                
                _fontRenderer.DrawString(sb, prevText, new Vector2(prevTextX, prevTextY), Color.White, prevFontSize);
            }
            
            // Next button (right)
            if (_currentPage < _totalPages - 1)
            {
                int nextButtonX = screenWidth - navButtonWidth - navButtonMargin;
                Rectangle nextBounds = new Rectangle(nextButtonX, navButtonY, navButtonWidth, navButtonHeight);
                
                sb.Draw(_pixel, nextBounds, new Color(50, 150, 200) * 0.9f);
                sb.Draw(_pixel, new Rectangle(nextButtonX - 2, navButtonY - 2, navButtonWidth + 4, navButtonHeight + 4), new Color(30, 100, 150) * 0.7f);
                
                string nextText = "SIGUIENTE >";
                int nextFontSize = 20;
                var nextTextSize = _fontRenderer.MeasureString(nextText, nextFontSize);
                int nextTextX = (int)(nextButtonX + (navButtonWidth - nextTextSize.X) / 2);
                int nextTextY = (int)(navButtonY + (navButtonHeight - nextTextSize.Y) / 2);
                
                _fontRenderer.DrawString(sb, nextText, new Vector2(nextTextX, nextTextY), Color.White, nextFontSize);
            }
            
            // Page indicator dots
            int dotY = screenHeight - 140;
            int dotSize = 12;
            int dotSpacing = 20;
            int totalDotsWidth = (_totalPages * dotSize) + ((_totalPages - 1) * dotSpacing);
            int dotStartX = (screenWidth - totalDotsWidth) / 2;
            
            for (int i = 0; i < _totalPages; i++)
            {
                int dotX = dotStartX + (i * (dotSize + dotSpacing));
                Color dotColor = (i == _currentPage) ? new Color(100, 200, 255) : Color.White * 0.3f;
                float dotScale = (i == _currentPage) ? 1.2f : 1.0f;
                int scaledSize = (int)(dotSize * dotScale);
                int offset = (scaledSize - dotSize) / 2;
                
                sb.Draw(_pixel, new Rectangle(dotX - offset, dotY - offset, scaledSize, scaledSize), dotColor);
            }
            
            // Button on page 3 (Grant Folder Access)
            if (currentPageData.HasButton)
            {
                int buttonWidth = (int)(screenWidth * 0.7f);
                int buttonHeight = 60;
                int buttonX = (screenWidth - buttonWidth) / 2;
                int buttonY = screenHeight - 280;
                
                // Button background with glow
                sb.Draw(_pixel, new Rectangle(buttonX - 4, buttonY - 4, buttonWidth + 8, buttonHeight + 8), new Color(100, 255, 100) * 0.3f);
                sb.Draw(_pixel, new Rectangle(buttonX, buttonY, buttonWidth, buttonHeight), new Color(100, 255, 100));
                
                // Button text
                int buttonFontSize = 24;
                var buttonTextSize = _fontRenderer.MeasureString(currentPageData.ButtonText, buttonFontSize);
                int buttonTextX = (int)((screenWidth - buttonTextSize.X) / 2);
                int buttonTextY = (int)(buttonY + (buttonHeight - buttonTextSize.Y) / 2);
                
                _fontRenderer.DrawString(sb, currentPageData.ButtonText, new Vector2(buttonTextX, buttonTextY), Color.White, buttonFontSize);
            }
            
            // Page number
            string pageNum = $"{_currentPage + 1}/{_totalPages}";
            int pageNumFontSize = 18;
            var pageNumSize = _fontRenderer.MeasureString(pageNum, pageNumFontSize);
            int pageNumX = screenWidth - (int)pageNumSize.X - 20;
            int pageNumY = titleHeight + 10;
            
            _fontRenderer.DrawString(sb, pageNum, new Vector2(pageNumX, pageNumY), Color.White * 0.5f, pageNumFontSize);
        }
        
        public void Hide()
        {
            IsVisible = false;
            // Restore previous gestures when hiding
            TouchPanel.EnabledGestures = GestureType.None;
        }
    }
}
