using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using FunkyMakerPlayer.Managers;
using FunkyMakerPlayer.Multiplayer;

namespace FunkyMakerPlayer;

/// <summary>
/// GameMultiplayer - PvP multiplayer game mode
/// Displays both players with their character sprites
/// Syncs character animations and note hits in real-time
/// </summary>
public class GameMultiplayer : Game
{
    // ── Core MonoGame ──────────────────────────────────────────────────────────
    private GraphicsDeviceManager _graphics;
    private SpriteBatch? _spriteBatch;
    
    // ── Managers ──────────────────────────────────────────────────────────────
    private CameraManager? _cameraManager;
    private NetworkManager? _networkManager;
    
    // ── Fonts & UI ──────────────────────────────────────────────────────────
    private SpriteFont? _font;
    private Texture2D? _pixel;
    
    // ── Level Data ──────────────────────────────────────────────────────────
    private FnmmLevel? _level;
    private string _levelPath = string.Empty;
    
    // ── Timing ──────────────────────────────────────────────────────────────
    private float _songPosition = 0f;
    private bool _songStarted = false;
    
    // ── Gameplay ──────────────────────────────────────────────────────────
    private int _score = 0;
    private int _opponentScore = 0;
    private int _combo = 0;
    private int _opponentCombo = 0;
    private int _health = 50;
    private int _opponentHealth = 50;
    private bool _gameOver = false;
    private float _gameOverTimer = 0f;
    
    // ── Player Characters ──────────────────────────────────────────────────
    private PlayerCharacter _localPlayer;
    private PlayerCharacter _opponentPlayer;
    
    // ── Strumlines (Note Rendering) ──────────────────────────────────────
    private NoteRenderer? _playerStrumline;
    private NoteRenderer? _opponentStrumline;
    
    // ── Input ──────────────────────────────────────────────────────────────
    private KeyboardState _previousKeyboardState;
    
#if ANDROID
    private TouchControls? _touchControls;
#endif
    
    // ── Multiplayer State ──────────────────────────────────────────────────
    private string _opponentId = string.Empty;
    private string _opponentName = string.Empty;
    private float _countdown = 3f;
    private bool _countdownActive = true;
    
    // ── Character Classes ──────────────────────────────────────────────────
    
    /// <summary>
    /// Represents a player character with sprite and animation
    /// </summary>
    private class PlayerCharacter
    {
        public string PlayerId { get; set; } = string.Empty;
        public string Name { get; set; } = "Player";
        public Texture2D? SpriteSheet { get; set; }
        public Microsoft.Xna.Framework.Color Tint { get; set; } = Microsoft.Xna.Framework.Color.White;
        
        // Sprite info
        public int SpriteWidth { get; set; } = 128;
        public int SpriteHeight { get; set; } = 128;
        public int GridColumns { get; set; } = 5; // idle, left, down, up, right
        
        // Animation state
        public string CurrentAnimation { get; set; } = "idle";
        public int CurrentFrame { get; set; } = 0;
        public float AnimationTimer { get; set; } = 0f;
        public float FrameDuration { get; set; } = 0.1f;
        
        // Position on screen
        public Vector2 Position { get; set; }
        public float Scale { get; set; } = 2.0f;
        public bool FlipX { get; set; } = false;
        
        public void UpdateAnimation(float deltaTime, int direction = -1)
        {
            AnimationTimer += deltaTime;
            
            if (AnimationTimer >= FrameDuration)
            {
                AnimationTimer = 0f;
                CurrentFrame = direction >= 0 ? direction + 1 : 0; // +1 because frame 0 is idle
            }
        }
        
        public void Sing(int direction)
        {
            CurrentAnimation = direction switch
            {
                0 => "left",
                1 => "down",
                2 => "up",
                3 => "right",
                _ => "idle"
            };
            CurrentFrame = direction + 1; // +1 because frame 0 is idle
        }
        
        public void Idle()
        {
            CurrentAnimation = "idle";
            CurrentFrame = 0;
        }
        
        public Microsoft.Xna.Framework.Rectangle GetSourceRectangle()
        {
            return new Microsoft.Xna.Framework.Rectangle(
                CurrentFrame * SpriteWidth,
                0,
                SpriteWidth,
                SpriteHeight
            );
        }
    }
    
    public GameMultiplayer(
        string levelPath,
        NetworkManager networkManager,
        string opponentId,
        string opponentName)
    {
        _graphics = new GraphicsDeviceManager(this);
        Content.RootDirectory = "Content";
        IsMouseVisible = true;
        
        _networkManager = networkManager;
        _opponentId = opponentId;
        _opponentName = opponentName;
        _levelPath = levelPath;
        
        // Initialize player characters
        _localPlayer = new PlayerCharacter
        {
            PlayerId = networkManager.PlayerId,
            Name = networkManager.PlayerName,
            Position = new Vector2(200, 300),
            FlipX = false
        };
        
        _opponentPlayer = new PlayerCharacter
        {
            PlayerId = opponentId,
            Name = opponentName,
            Position = new Vector2(1000, 300),
            FlipX = true
        };
        
        // Setup network callbacks
        SetupNetworkHandlers();
    }
    
    protected override void Initialize()
    {
        _graphics.PreferredBackBufferWidth = 1280;
        _graphics.PreferredBackBufferHeight = 720;
        _graphics.ApplyChanges();
        
        _cameraManager = new CameraManager();
        
        _previousKeyboardState = Keyboard.GetState();
        
        base.Initialize();
    }
    
    protected override void LoadContent()
    {
        _spriteBatch = new SpriteBatch(GraphicsDevice);
        
        // Load fonts
        try
        {
            _font = Content.Load<SpriteFont>("Fonts/DefaultFont");
        }
        catch
        {
            Console.WriteLine("[GameMultiplayer] Warning: Could not load fonts");
        }
        
        // Create pixel texture for UI
        _pixel = new Texture2D(GraphicsDevice, 1, 1);
        _pixel.SetData(new[] { Microsoft.Xna.Framework.Color.White });
        
#if ANDROID
        // Initialize touch controls for Android
        _touchControls = new TouchControls();
        _touchControls.Initialize(GraphicsDevice, 
            GraphicsDevice.PresentationParameters.BackBufferWidth, 
            GraphicsDevice.PresentationParameters.BackBufferHeight);
        
        // Set to Gameplay mode so note buttons work
        _touchControls.SetMode(TouchControls.ControlMode.Gameplay);
        
        Console.WriteLine("[GameMultiplayer] Touch controls initialized for Android (Gameplay mode)");
#endif
        
        // Load level
        LoadLevel(_levelPath);
        
        // Load character sprites
        LoadCharacterSprites();
        
        // Send character info to opponent
        SendCharacterInfo();
    }
    
    private void LoadLevel(string path)
    {
        try
        {
            // Load level using FnmmLoader
            _level = FnmmLoader.Load(path);
            
            if (_level != null)
            {
                Console.WriteLine($"[GameMultiplayer] Loaded level: {_level.LevelName}");
                
                // Initialize strumlines
                _playerStrumline = new NoteRenderer(_level, 1, 200f, GraphicsDevice, null);
                _playerStrumline.SetIsPlayer(true);
                _playerStrumline.SetRenderTargetSize(1280, 720);
                
                _opponentStrumline = new NoteRenderer(_level, 0, 900f, GraphicsDevice, null);
                _opponentStrumline.SetIsPlayer(false);
                _opponentStrumline.DisableAutoplay = true; // Network-controlled
                _opponentStrumline.SetRenderTargetSize(1280, 720);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameMultiplayer] Error loading level: {ex.Message}");
        }
    }
    
    private void LoadCharacterSprites()
    {
        try
        {
            // Load player character from level
            string playerSpritePath = Path.Combine(_levelPath, "characters", "player.png");
            if (File.Exists(playerSpritePath))
            {
                using var stream = File.OpenRead(playerSpritePath);
                _localPlayer.SpriteSheet = Texture2D.FromStream(GraphicsDevice, stream);
                Console.WriteLine($"[GameMultiplayer] Loaded player sprite");
            }
            else
            {
                // Create default sprite
                _localPlayer.SpriteSheet = CreateDefaultCharacterTexture(Microsoft.Xna.Framework.Color.Blue);
            }
            
            // Opponent sprite will be loaded from network packet
            _opponentPlayer.SpriteSheet = CreateDefaultCharacterTexture(Microsoft.Xna.Framework.Color.Red);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameMultiplayer] Error loading character sprites: {ex.Message}");
        }
    }
    
    private Texture2D CreateDefaultCharacterTexture(Microsoft.Xna.Framework.Color color)
    {
        // Create a simple colored sprite (5 frames: idle, left, down, up, right)
        var texture = new Texture2D(GraphicsDevice, 640, 128);
        var colors = new Microsoft.Xna.Framework.Color[640 * 128];
        
        for (int i = 0; i < colors.Length; i++)
        {
            int x = i % 640;
            int y = i / 640;
            int frame = x / 128;
            int localX = x % 128;
            
            // Simple circle for each frame
            int centerX = 64;
            int centerY = 64;
            float distance = MathF.Sqrt((localX - centerX) * (localX - centerX) + (y - centerY) * (y - centerY));
            
            if (distance < 50)
            {
                colors[i] = color;
            }
            else
            {
                colors[i] = Microsoft.Xna.Framework.Color.Transparent;
            }
        }
        
        texture.SetData(colors);
        return texture;
    }
    
    private void SendCharacterInfo()
    {
        try
        {
            var packet = new PlayerCharacterPacket
            {
                PlayerId = _localPlayer.PlayerId,
                CharacterName = _localPlayer.Name,
                HasCustomSprite = true,
                CharacterColor = "#0000FF"
            };
            
            _networkManager?.SendCharacterInfo(packet);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GameMultiplayer] Error sending character info: {ex.Message}");
        }
    }
    
    private void SetupNetworkHandlers()
    {
        if (_networkManager == null) return;
        
        // Handle opponent note hits
        _networkManager.OnOpponentNoteHit += (direction, time, rating) =>
        {
            // Trigger opponent character animation
            _opponentPlayer.Sing(direction);
            
            // Trigger visual hit on opponent strumline
            _opponentStrumline?.TriggerManualHit(direction, rating == "sick");
            
            // Update opponent score
            int scoreGain = rating switch
            {
                "sick" => 350,
                "good" => 200,
                "bad" => 50,
                _ => 0
            };
            _opponentScore += scoreGain;
            _opponentCombo++;
        };
        
        // Handle opponent character animations
        _networkManager.OnCharacterAnimation += (playerId, animState, direction) =>
        {
            if (playerId == _opponentId)
            {
                _opponentPlayer.Sing(direction);
            }
        };
        
        // Handle opponent character sprite
        _networkManager.OnCharacterSprite += (playerId, spriteData) =>
        {
            if (playerId == _opponentId && spriteData != null)
            {
                try
                {
                    using var ms = new System.IO.MemoryStream(spriteData);
                    _opponentPlayer.SpriteSheet = Texture2D.FromStream(GraphicsDevice, ms);
                    Console.WriteLine($"[GameMultiplayer] Loaded opponent sprite");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[GameMultiplayer] Error loading opponent sprite: {ex.Message}");
                }
            }
        };
    }
    
    protected override void Update(GameTime gameTime)
    {
        float deltaTime = (float)gameTime.ElapsedGameTime.TotalSeconds;
        
        // Handle input
        var keyboardState = Keyboard.GetState();
        
#if ANDROID
        // Update touch controls
        _touchControls?.Update();
        
        // Merge touch input with keyboard
        keyboardState = MergeTouchWithKeyboard(keyboardState);
#endif
        
        // Exit
        if (keyboardState.IsKeyDown(Keys.Escape))
        {
            ExitToMenu();
            return;
        }
        
        // Countdown
        if (_countdownActive)
        {
            _countdown -= deltaTime;
            if (_countdown <= 0)
            {
                _countdownActive = false;
                StartSong();
            }
        }
        else if (!_gameOver)
        {
            // Update song position
            if (_level != null)
            {
                _songPosition += deltaTime;
            }
            
            // Update character animations
            _localPlayer.UpdateAnimation(deltaTime);
            _opponentPlayer.UpdateAnimation(deltaTime);
            
            // Update strumlines
            if (_playerStrumline != null)
            {
                _playerStrumline.Update(_songPosition, deltaTime, keyboardState, _previousKeyboardState);
                
                // Update local score and combo from strumline
                _score = _playerStrumline.Score;
                _combo = _playerStrumline.Combo;
            }
            
            if (_opponentStrumline != null)
            {
                _opponentStrumline.Update(_songPosition, deltaTime, keyboardState, _previousKeyboardState);
            }
            
            // Check game over
            if (_health <= 0)
            {
                _gameOver = true;
                _gameOverTimer = 3f;
            }
        }
        else
        {
            // Game over countdown
            _gameOverTimer -= deltaTime;
            if (_gameOverTimer <= 0)
            {
                ExitToMenu();
            }
        }
        
        _previousKeyboardState = keyboardState;
        
        base.Update(gameTime);
    }
    
    private void StartSong()
    {
        _songStarted = true;
        
        // For now, music playback is handled separately
        // TODO: Implement audio playback in multiplayer
        
        Console.WriteLine("[GameMultiplayer] Song started");
    }
    
#if ANDROID
    /// <summary>
    /// Merge touch controls into keyboard state (Android only)
    /// </summary>
    private KeyboardState MergeTouchWithKeyboard(KeyboardState kb)
    {
        if (_touchControls == null) return kb;
        
        // Get all currently pressed keys from keyboard
        var pressedKeys = new List<Keys>(kb.GetPressedKeys());
        
        // Add virtual keys from touch controls (D, F, J, K for note lanes)
        foreach (var key in new[] { Keys.D, Keys.F, Keys.J, Keys.K, Keys.Escape })
        {
            if (_touchControls.IsKeyDown(key) && !pressedKeys.Contains(key))
                pressedKeys.Add(key);
        }
        
        // Create new keyboard state with merged keys
        return new KeyboardState(pressedKeys.ToArray());
    }
#endif
    
    private void ExitToMenu()
    {
        // Stop any active audio
        // TODO: Stop music when audio system is integrated
        
        _networkManager?.Disconnect();
        
        Exit();
    }
    
    protected override void Draw(GameTime gameTime)
    {
        GraphicsDevice.Clear(Microsoft.Xna.Framework.Color.Black);
        
        if (_spriteBatch == null) return;
        
        _spriteBatch.Begin(
            SpriteSortMode.Deferred,
            BlendState.AlphaBlend,
            SamplerState.PointClamp,
            DepthStencilState.Default,
            RasterizerState.CullNone
        );
        
        int sw = GraphicsDevice.Viewport.Width;
        int sh = GraphicsDevice.Viewport.Height;
        
        // Draw countdown
        if (_countdownActive && _font != null)
        {
            int countdownNum = (int)Math.Ceiling(_countdown);
            string countdownText = countdownNum > 0 ? countdownNum.ToString() : "GO!";
            var textSize = _font.MeasureString(countdownText);
            _spriteBatch.DrawString(
                _font,
                countdownText,
                new Vector2(sw / 2 - textSize.X / 2, sh / 2 - textSize.Y / 2),
                Microsoft.Xna.Framework.Color.White
            );
        }
        else
        {
            // Draw strumlines
            _playerStrumline?.Draw(_spriteBatch, _songPosition, _font);
            _opponentStrumline?.Draw(_spriteBatch, _songPosition, _font);
            
            // Draw characters
            DrawCharacter(_localPlayer);
            DrawCharacter(_opponentPlayer);
            
            // Draw UI
            DrawUI(sw, sh);
        }
        
#if ANDROID
        // Draw touch controls
        _touchControls?.Draw(_spriteBatch, _font);
#endif
        
        _spriteBatch.End();
        
        base.Draw(gameTime);
    }
    
    private void DrawCharacter(PlayerCharacter character)
    {
        if (character.SpriteSheet == null || _spriteBatch == null) return;
        
        var sourceRect = character.GetSourceRectangle();
        var origin = new Vector2(sourceRect.Width / 2f, sourceRect.Height / 2f);
        
        _spriteBatch.Draw(
            character.SpriteSheet,
            character.Position,
            sourceRect,
            character.Tint,
            0f,
            origin,
            character.Scale,
            character.FlipX ? SpriteEffects.FlipHorizontally : SpriteEffects.None,
            0f
        );
    }
    
    private void DrawUI(int sw, int sh)
    {
        if (_font == null || _pixel == null) return;
        
        // Draw scores
        _spriteBatch.DrawString(_font, $"Score: {_score}", new Vector2(10, 10), Microsoft.Xna.Framework.Color.White);
        _spriteBatch.DrawString(_font, $"Combo: {_combo}", new Vector2(10, 40), Microsoft.Xna.Framework.Color.Yellow);
        
        var opponentScoreText = $"Opponent: {_opponentScore}";
        var opponentScoreSize = _font.MeasureString(opponentScoreText);
        _spriteBatch.DrawString(_font, opponentScoreText, new Vector2(sw - opponentScoreSize.X - 10, 10), Microsoft.Xna.Framework.Color.White);
        _spriteBatch.DrawString(_font, $"Combo: {_opponentCombo}", new Vector2(sw - 200, 40), Microsoft.Xna.Framework.Color.Yellow);
        
        // Draw health bars
        int healthBarWidth = 300;
        int healthBarHeight = 20;
        int healthBarY = sh - 40;
        
        // Local player health (left)
        _spriteBatch.Draw(_pixel, new Microsoft.Xna.Framework.Rectangle(10, healthBarY, healthBarWidth, healthBarHeight), Microsoft.Xna.Framework.Color.Gray);
        _spriteBatch.Draw(_pixel, new Microsoft.Xna.Framework.Rectangle(10, healthBarY, (int)(healthBarWidth * (_health / 100f)), healthBarHeight), Microsoft.Xna.Framework.Color.Green);
        
        // Opponent health (right)
        _spriteBatch.Draw(_pixel, new Microsoft.Xna.Framework.Rectangle(sw - healthBarWidth - 10, healthBarY, healthBarWidth, healthBarHeight), Microsoft.Xna.Framework.Color.Gray);
        _spriteBatch.Draw(_pixel, new Microsoft.Xna.Framework.Rectangle(sw - healthBarWidth - 10, healthBarY, (int)(healthBarWidth * (_opponentHealth / 100f)), healthBarHeight), Microsoft.Xna.Framework.Color.Red);
        
        // Draw player names
        _spriteBatch.DrawString(_font, _localPlayer.Name, new Vector2(10, healthBarY - 25), Microsoft.Xna.Framework.Color.White);
        var oppNameSize = _font.MeasureString(_opponentPlayer.Name);
        _spriteBatch.DrawString(_font, _opponentPlayer.Name, new Vector2(sw - oppNameSize.X - 10, healthBarY - 25), Microsoft.Xna.Framework.Color.White);
        
        // Game over
        if (_gameOver)
        {
            string gameOverText = _score > _opponentScore ? "YOU WIN!" : "YOU LOSE!";
            var gameOverSize = _font.MeasureString(gameOverText);
            _spriteBatch.Draw(_pixel, new Microsoft.Xna.Framework.Rectangle(0, 0, sw, sh), Microsoft.Xna.Framework.Color.Black * 0.7f);
            _spriteBatch.DrawString(
                _font,
                gameOverText,
                new Vector2(sw / 2 - gameOverSize.X / 2, sh / 2 - gameOverSize.Y / 2),
                _score > _opponentScore ? Microsoft.Xna.Framework.Color.Gold : Microsoft.Xna.Framework.Color.Red
            );
        }
    }
    
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _pixel?.Dispose();
            _localPlayer.SpriteSheet?.Dispose();
            _opponentPlayer.SpriteSheet?.Dispose();
        }
        
        base.Dispose(disposing);
    }
}
